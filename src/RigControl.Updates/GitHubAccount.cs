using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RigControl.Core.Logging;
using RigControl.Core.Platform;
using RigControl.Core.Settings;

namespace RigControl.Updates;

/// <summary>Settings section "github": only the account name in plain text; the tokens are protected for this user (DPAPI on Windows).</summary>
public sealed class GitHubSettings
{
    public const string Section = "github";

    /// <summary>The signed-in GitHub account's name, for showing. Not a secret.</summary>
    public string? Login { get; set; }

    /// <summary>The tokens, protected with <see cref="ISecretProtector"/> and written as base64. Never in plain text.</summary>
    public string? ProtectedTokens { get; set; }
}

/// <summary>
/// The GitHub account RigControl is signed in with: keeps the tokens protected, renews the access token when it has
/// run out, and forgets everything on sign-out. Tokens never leave this class except as the Authorization header.
/// </summary>
public sealed class GitHubAccount
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("RigControl.GitHub.Tokens.v1");

    /// <summary>A token is renewed this long before GitHub says it runs out.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private readonly SettingsStore _settings;
    private readonly ISecretProtector _secrets;
    private readonly GitHubSignIn _signIn;
    private readonly GitHubApi _api;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitHubAccount(SettingsStore settings, ISecretProtector secrets, GitHubSignIn signIn, GitHubApi api, ILog log, Func<DateTimeOffset>? now = null)
    {
        _settings = settings;
        _secrets = secrets;
        _signIn = signIn;
        _api = api;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Configured => _signIn.Configured;

    public GitHubSignIn SignIn => _signIn;

    /// <summary>The account name, or null when not signed in.</summary>
    public string? Login
    {
        get
        {
            var s = _settings.Get<GitHubSettings>(GitHubSettings.Section);
            return string.IsNullOrEmpty(s.ProtectedTokens) ? null : s.Login;
        }
    }

    /// <summary>Waits for the user to accept <paramref name="device"/>, then keeps the tokens and reads the account name.</summary>
    public async Task<string> CompleteSignInAsync(DeviceCode device, CancellationToken ct)
    {
        var tokens = await _signIn.WaitAsync(device, ct).ConfigureAwait(false);
        var login = await _api.GetLoginAsync(tokens.AccessToken, ct).ConfigureAwait(false);
        Store(tokens, login);
        _log.Info("Signed in with GitHub as " + login + ".");
        return login;
    }

    /// <summary>
    /// A token that works now: the stored one, or a renewed one when it has run out. Throws a plain
    /// <see cref="GitHubException"/> when the user has to sign in (again).
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tokens = Load() ?? throw new GitHubException("Sign in with GitHub first.");
            if (tokens.AccessExpiresAt is not { } expires || _now() < expires - Margin) return tokens.AccessToken;

            if (string.IsNullOrEmpty(tokens.RefreshToken) || (tokens.RefreshExpiresAt is { } r && _now() >= r))
            {
                Forget("its renewal had run out");
                throw new GitHubException("The GitHub sign-in has run out. Sign in with GitHub again.");
            }
            try
            {
                var renewed = await _signIn.RefreshAsync(tokens.RefreshToken, ct).ConfigureAwait(false);
                Store(renewed, _settings.Get<GitHubSettings>(GitHubSettings.Section).Login);
                _log.Info("The GitHub sign-in was renewed.");
                return renewed.AccessToken;
            }
            catch (GitHubException ex) when (RefusedByGitHub(ex))
            {
                // GitHub refused the refresh token (used up, revoked): the user has to sign in again.
                Forget("GitHub refused to renew it (" + ex.Message + ")");
                throw;
            }
            // Anything else (no network, a time-out, GitHub busy) keeps the sign-in: the next try may well work.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Signs out: the tokens and the account name are deleted from this PC.</summary>
    public void SignOut()
    {
        Forget(null);
        _log.Info("Signed out of GitHub; the sign-in was deleted from this PC.");
    }

    /// <summary>
    /// True only when GitHub itself said no to the refresh token: HTTP 400/401 from the token address, or one of the
    /// sign-in error codes that mean the token is no good. A failure to reach GitHub is not a refusal.
    /// </summary>
    internal static bool RefusedByGitHub(GitHubException ex)
        => ex.Status is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized
           || ex.OAuthError is "bad_refresh_token" or "invalid_grant" or "unauthorized" or "unauthorized_client" or "expired_token";

    /// <summary>Deletes the sign-in; with a reason it is also written to the log, so a dropped sign-in is never a mystery.</summary>
    private void Forget(string? reason)
    {
        if (reason is not null) _log.Warn("The GitHub sign-in was deleted from this PC: " + reason + ". Sign in with GitHub again.");
        _settings.Set(GitHubSettings.Section, new GitHubSettings());
    }

    private void Store(GitHubTokens tokens, string? login)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new StoredTokens
        {
            Access = tokens.AccessToken,
            AccessExpires = tokens.AccessExpiresAt,
            Refresh = tokens.RefreshToken,
            RefreshExpires = tokens.RefreshExpiresAt,
        });
        try
        {
            var protectedBytes = _secrets.Protect(json, Purpose);
            _settings.Set(GitHubSettings.Section, new GitHubSettings { Login = login, ProtectedTokens = Convert.ToBase64String(protectedBytes) });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private GitHubTokens? Load()
    {
        var s = _settings.Get<GitHubSettings>(GitHubSettings.Section);
        if (string.IsNullOrEmpty(s.ProtectedTokens)) return null;
        try
        {
            var json = _secrets.Unprotect(Convert.FromBase64String(s.ProtectedTokens), Purpose);
            var t = JsonSerializer.Deserialize<StoredTokens>(json);
            CryptographicOperations.ZeroMemory(json);
            if (t is null || string.IsNullOrEmpty(t.Access)) return null;
            return new GitHubTokens(t.Access, t.AccessExpires, t.Refresh, t.RefreshExpires);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            // Protected for another Windows account, or damaged: it cannot be used, so it is dropped.
            Forget("it could not be read (" + ex.GetType().Name + ")");
            return null;
        }
    }

    private sealed class StoredTokens
    {
        public string? Access { get; set; }
        public DateTimeOffset? AccessExpires { get; set; }
        public string? Refresh { get; set; }
        public DateTimeOffset? RefreshExpires { get; set; }
    }
}
