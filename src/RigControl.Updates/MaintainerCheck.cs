using RigControl.Core.Logging;

namespace RigControl.Updates;

/// <summary>
/// Whether the signed-in GitHub account may push to the maintainers' private repository. Only then does anything about dev mode exist
/// in the app (the Settings section, the Developer page, the "dev" calls). Asked at each start and after sign-in, kept
/// for this run only; signed out or any failure means no.
/// </summary>
public sealed class MaintainerCheck
{
    private readonly GitHubAccount _account;
    private readonly GitHubApi _api;
    private readonly ILog _log;
    private volatile bool _maintainer;

    public MaintainerCheck(GitHubAccount account, GitHubApi api, ILog log)
    {
        _account = account;
        _api = api;
        _log = log;
    }

    /// <summary>The last answer (false until asked).</summary>
    public bool IsMaintainer => _maintainer && _account.Login is not null;

    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (_account.Login is null)
        {
            _maintainer = false;
            return false;
        }
        try
        {
            var token = await _account.GetAccessTokenAsync(ct).ConfigureAwait(false);
            _maintainer = await _api.CanPushAsync(token, ct).ConfigureAwait(false);
        }
        catch (GitHubException ex)
        {
            _log.Warn("Could not ask GitHub about this account's access: " + ex.Message);
            _maintainer = false;
        }
        return _maintainer;
    }

    public void Clear() => _maintainer = false;
}
