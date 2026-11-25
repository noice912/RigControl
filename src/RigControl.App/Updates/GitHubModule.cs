using System.Net.Http;
using System.Text.Json;
using RigControl.Core.Rpc;
using RigControl.Updates;

namespace RigControl.App.Updates;

/// <summary>The one GitHub connection the app keeps: an HTTP client that only reaches GitHub, the API calls and the account.</summary>
internal sealed class GitHubServices : IDisposable
{
    /// <param name="network">Tests pass a fake; the app passes nothing and gets the real (GitHub-only) connection.</param>
    public GitHubServices(AppServices services, HttpMessageHandler? network = null)
    {
        Http = GitHubHttp.CreateClient(network);
        Api = new GitHubApi(Http);
        Account = new GitHubAccount(services.Settings, services.Platform.Secrets, new GitHubSignIn(Http), Api, services.Log.For("github"));
        Maintainer = new MaintainerCheck(Account, Api, services.Log.For("github"));
    }

    public HttpClient Http { get; }
    public GitHubApi Api { get; }
    public GitHubAccount Account { get; }

    /// <summary>Whether the signed-in account may push to RigControl's repository; dev mode exists only then.</summary>
    public MaintainerCheck Maintainer { get; }

    public void Dispose() => Http.Dispose();
}

/// <summary>
/// The "github" RPC module, for every user: sign in with GitHub (device flow: a code to type on github.com in the normal
/// browser; nothing listens on this PC), sign out, and write a review. Tokens never reach the page.
/// Events: "github.status" while signing in and when the account changes.
/// </summary>
internal sealed class GitHubModule : IRpcModule
{
    private readonly AppServices _services;
    private readonly GitHubServices _github;
    private readonly RigControl.Core.Logging.ILog _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _signIn;

    /// <summary>Raised on sign-out, so an open dev session is closed with it.</summary>
    public event Action? DevModeClosed;
    private DeviceCode? _code;
    private string? _problem;

    public GitHubModule(AppServices services, GitHubServices github)
    {
        _services = services;
        _github = github;
        _log = services.Log.For("github");
    }

    public string Name => "github";

    /// <summary>At start: asks GitHub once whether the signed-in account may push (dev mode exists only then).</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try { await _github.Maintainer.RefreshAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn("Checking the GitHub account failed: " + ex.Message); }
            Publish();
        });
    }

    public async Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        switch (method)
        {
            case "getStatus":
                return Status();
            case "startSignIn":
                return await StartSignInAsync(ct).ConfigureAwait(false);
            case "cancelSignIn":
                CancelSignIn();
                return Status();
            case "signOut":
                CancelSignIn();
                _github.Account.SignOut();
                _github.Maintainer.Clear();
                DevModeClosed?.Invoke();
                Publish();
                return Status();
            case "submitReview":
                return await SubmitReviewAsync(RpcArgs.Require<ReviewArgs>(args), ct).ConfigureAwait(false);
            default:
                throw RpcException.UnknownMethod(Name, method);
        }
    }

    private object Status()
    {
        lock (_gate)
        {
            return new
            {
                configured = _github.Account.Configured,
                notSetUp = _github.Account.Configured ? null : GitHubSignIn.NotSetUp,
                login = _github.Account.Login,
                // True only for an account that may push to RigControl's repository: the page then shows dev mode.
                maintainer = _github.Maintainer.IsMaintainer,
                signingIn = _code is not null,
                userCode = _code?.UserCode,
                verificationUri = _code?.VerificationUri,
                expiresAt = _code?.ExpiresAt.ToString("o"),
                problem = _problem,
            };
        }
    }

    private void Publish()
    {
        try { _services.Events.Publish("github.status", Status()); }
        catch { /* the page may be closing */ }
    }

    private async Task<object> StartSignInAsync(CancellationToken ct)
    {
        if (!_github.Account.Configured) throw RpcException.Unavailable(GitHubSignIn.NotSetUp);
        lock (_gate)
        {
            if (_code is not null && _code.ExpiresAt > DateTimeOffset.UtcNow) return Status(); // already waiting
        }

        DeviceCode code;
        try
        {
            code = await _github.Account.SignIn.StartAsync(ct).ConfigureAwait(false);
        }
        catch (GitHubException ex)
        {
            throw RpcException.Unavailable(ex.Message);
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _signIn?.Cancel();
            _signIn = cts;
            _code = code;
            _problem = null;
        }
        try { ShellActions.OpenHttpsUrl(code.VerificationUri); }
        catch (Exception ex) { _log.Warn("The GitHub page could not be opened in the browser: " + ex.Message); }
        _ = WaitForSignInAsync(code, cts);
        Publish();
        return Status();
    }

    private async Task WaitForSignInAsync(DeviceCode code, CancellationTokenSource cts)
    {
        try
        {
            await _github.Account.CompleteSignInAsync(code, cts.Token).ConfigureAwait(false);
            await _github.Maintainer.RefreshAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cancelled by the user
        }
        catch (GitHubException ex)
        {
            lock (_gate) _problem = ex.Message;
            _log.Warn("GitHub sign-in: " + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (_signIn == cts)
                {
                    _signIn = null;
                    _code = null;
                }
            }
            cts.Dispose();
            Publish();
        }
    }

    private void CancelSignIn()
    {
        lock (_gate)
        {
            try { _signIn?.Cancel(); } catch (ObjectDisposedException) { /* already over */ }
            _signIn = null;
            _code = null;
        }
        Publish();
    }

    /// <summary>
    /// Opens a public issue on RigControl's GitHub with the stars, the words and the RigControl version. Nothing about
    /// the PC is added. The page has already said it will be public under the user's GitHub name.
    /// </summary>
    private async Task<object> SubmitReviewAsync(ReviewArgs a, CancellationToken ct)
    {
        if (!AppVersion.TryParseLoose(_services.Environment.Version, out var version))
            throw RpcException.Unavailable("RigControl's own version is unknown, so the review can't say which version it is about.");
        (string Title, string Body) review;
        try
        {
            review = Reviews.Compose(a.Stars ?? 0, a.Text, version.Value);
        }
        catch (ArgumentException ex)
        {
            throw RpcException.InvalidArgs(ex.Message);
        }
        try
        {
            var token = await _github.Account.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var issue = await _github.Api.CreateIssueAsync(token, review.Title, review.Body, new[] { Reviews.Label }, ct).ConfigureAwait(false);
            _log.Info("Review posted to GitHub as issue #" + issue.Number + ".");
            Publish(); // a refused sign-in may have been forgotten on the way
            return new { url = issue.HtmlUrl, number = issue.Number };
        }
        catch (GitHubException ex)
        {
            Publish();
            throw RpcException.Unavailable(ex.Message);
        }
    }

    private sealed class ReviewArgs
    {
        public int? Stars { get; set; }
        public string? Text { get; set; }
    }
}
