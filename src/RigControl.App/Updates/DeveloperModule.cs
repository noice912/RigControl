using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using RigControl.Core.Rpc;
using RigControl.Updates;

namespace RigControl.App.Updates;

/// <summary>Settings section "dev". The token is not a secret: it only works together with this PC's Windows Hello key.</summary>
internal sealed class DevSettings
{
    public const string Section = "dev";

    public string? Token { get; set; }
}

/// <summary>
/// The "dev" RPC module: dev mode bound to this PC's TPM through Windows Hello (see <see cref="DevEnrollment"/>), and,
/// once unlocked, publishing an update and reading reviews. Unlocking asks Windows Hello every time the Developer page
/// opens and again before publishing; it lasts for this run only and closes after 15 minutes without use.
/// Events: "dev.status" when the state changes, "dev.publish" with each publishing step.
/// </summary>
internal sealed class DeveloperModule : IRpcModule
{
    private const int MaxKeyFileBytes = 16 * 1024;

    private readonly AppServices _services;
    private readonly GitHubServices _github;
    private readonly IDeviceKey _key;
    private readonly DevSession _session = new();
    private readonly RigControl.Core.Logging.ILog _log;
    private readonly SemaphoreSlim _hello = new(1, 1);
    private int _publishing;

    public DeveloperModule(AppServices services, GitHubServices github, IDeviceKey key)
    {
        _services = services;
        _github = github;
        _key = key;
        _log = services.Log.For("dev");
    }

    public string Name => "dev";

    public static string DefaultSigningKeyPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rigcontrol-keys", "update-signing.pem");

    /// <summary>Closes an open dev session (on GitHub sign-out).</summary>
    public void Close() => _session.Close();

    public async Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
    {
        // Dev mode exists only for a signed-in GitHub account that may push to RigControl's repository. For anyone
        // else these calls answer exactly like calls that are not there.
        if (!_github.Maintainer.IsMaintainer)
        {
            _session.Close();
            throw RpcException.UnknownMethod(Name, method);
        }
        switch (method)
        {
            case "getStatus":
                return await StatusAsync().ConfigureAwait(false);
            case "registerPc":
                return await RegisterAsync().ConfigureAwait(false);
            case "setToken":
                return await SetTokenAsync(RpcArgs.Require<TokenArgs>(args).Token).ConfigureAwait(false);
            case "forget":
                return await ForgetAsync().ConfigureAwait(false);
            case "unlock":
                await UnlockAsync().ConfigureAwait(false);
                return await PublishStatusAsync().ConfigureAwait(false);
            case "lock":
                _session.Close();
                return await PublishStatusAsync().ConfigureAwait(false);
            case "publishUpdate":
                return await PublishUpdateAsync(RpcArgs.Require<PublishArgs>(args), ct).ConfigureAwait(false);
            case "listReviews":
                return await ListReviewsAsync(ct).ConfigureAwait(false);
            default:
                throw RpcException.UnknownMethod(Name, method);
        }
    }

    private string? StoredToken => _services.Settings.Get<DevSettings>(DevSettings.Section).Token;

    private async Task<object> StatusAsync()
    {
        var supported = await _key.SupportedAsync().ConfigureAwait(false);
        byte[]? spki = null;
        string? keyProblem = null;
        if (supported)
        {
            try { spki = await _key.OpenAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { keyProblem = ex.Message; }
        }

        var token = StoredToken;
        string? tokenProblem = null;
        DevTokenInfo? info = null;
        if (!string.IsNullOrEmpty(token))
        {
            info = DevEnrollment.Verify(token, TrustedKeys.DevKeyIssuerPublicKey, out tokenProblem);
            if (info is not null && (spki is null || DevEnrollment.DeviceHash(spki) != info.DeviceHash))
            {
                tokenProblem = "The saved dev token was made for another PC or an earlier registration of this one.";
                info = null;
            }
        }

        return new
        {
            supported,
            notSupported = supported ? null : "Set up Windows Hello (a PIN is enough) first.",
            registered = spki is not null,
            request = spki is null ? null : DevEnrollment.MakeRequest(spki),
            pcName = Environment.MachineName,
            keyProblem,
            hasToken = !string.IsNullOrEmpty(token),
            tokenProblem,
            // Enrolled: a genuine token for this PC's key. The Developer page is shown; opening it asks Windows Hello.
            enrolled = info is not null,
            subject = info?.Subject,
            unlocked = info is not null && _session.Unlocked,
            locksAt = _session.LocksAt?.ToString("o"),
            defaultKeyPath = DefaultSigningKeyPath,
        };
    }

    private async Task<object> PublishStatusAsync()
    {
        var status = await StatusAsync().ConfigureAwait(false);
        try { _services.Events.Publish("dev.status", status); }
        catch { /* the page may be closing */ }
        return status;
    }

    private async Task<object> RegisterAsync()
    {
        if (!await _key.SupportedAsync().ConfigureAwait(false))
            throw RpcException.Unavailable("Set up Windows Hello (a PIN is enough) first.");
        await _hello.WaitAsync().ConfigureAwait(false);
        try
        {
            var spki = await _key.CreateAsync().ConfigureAwait(false);
            // A new key makes any earlier token useless, so it is dropped with it.
            _services.Settings.Set(DevSettings.Section, new DevSettings());
            _session.Close();
            _log.Info("This PC was registered for dev mode (new Windows Hello key " + DevEnrollment.DeviceHash(spki) + ").");
        }
        catch (OperationCanceledException)
        {
            throw new RpcException(RpcErrorCodes.Cancelled, "Windows Hello was cancelled, so nothing changed.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw RpcException.Unavailable(ex.Message);
        }
        finally
        {
            _hello.Release();
        }
        return await PublishStatusAsync().ConfigureAwait(false);
    }

    private async Task<object> SetTokenAsync(string? token)
    {
        var info = DevEnrollment.Verify(token, TrustedKeys.DevKeyIssuerPublicKey, out var problem)
                   ?? throw RpcException.InvalidArgs(problem ?? "That dev token is not valid.");
        var spki = await _key.OpenAsync().ConfigureAwait(false)
                   ?? throw RpcException.Unavailable("Register this PC first; the token belongs to its Windows Hello key.");
        if (DevEnrollment.DeviceHash(spki) != info.DeviceHash)
            throw RpcException.InvalidArgs("That dev token was made for another PC (or an earlier registration of this one).");
        _services.Settings.Set(DevSettings.Section, new DevSettings { Token = token!.Trim() });
        _log.Info("A dev token for this PC was saved (id " + info.Id + ").");
        return await PublishStatusAsync().ConfigureAwait(false);
    }

    private async Task<object> ForgetAsync()
    {
        _session.Close();
        _services.Settings.Set(DevSettings.Section, new DevSettings());
        await _key.DeleteAsync().ConfigureAwait(false);
        _log.Info("Dev mode was removed from this PC (token and Windows Hello key deleted).");
        return await PublishStatusAsync().ConfigureAwait(false);
    }

    /// <summary>The full check, with the Windows Hello prompt. Throws a plain RpcException when it does not unlock.</summary>
    private async Task UnlockAsync()
    {
        await _hello.WaitAsync().ConfigureAwait(false);
        try
        {
            string? problem;
            try
            {
                problem = await DevEnrollment.UnlockAsync(StoredToken, _key, TrustedKeys.DevKeyIssuerPublicKey).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                problem = ex.Message;
            }
            if (problem is not null)
            {
                _session.Close();
                throw RpcException.Unavailable(problem);
            }
            _session.Open();
            _log.Info("Dev mode unlocked for this session.");
        }
        finally
        {
            _hello.Release();
        }
    }

    private void RequireUnlocked()
    {
        if (!_session.Touch()) throw RpcException.Unavailable("Dev mode is locked. Open the Developer page to unlock it with Windows Hello.");
    }

    private async Task<object> PublishUpdateAsync(PublishArgs a, CancellationToken ct)
    {
        RequireUnlocked();
        if (!AppVersion.TryParse(a.Version, out var version))
            throw RpcException.InvalidArgs("The version must look like 1.2.3 (or 1.2.3-rc.1 for a development build).");
        var testBuild = a.TestBuild == true;
        if (ReleasePublisher.ChannelProblem(version, testBuild) is { } channelProblem) throw RpcException.InvalidArgs(channelProblem);
        var exe = a.ExePath?.Trim() ?? "";
        if (exe.Length == 0 || !File.Exists(exe)) throw RpcException.InvalidArgs("Choose the RigControl.exe to publish.");
        CheckExeVersion(exe, version);
        var keyPath = string.IsNullOrWhiteSpace(a.KeyPath) ? DefaultSigningKeyPath : a.KeyPath.Trim();
        if (!File.Exists(keyPath)) throw RpcException.InvalidArgs("The signing key file was not found: " + keyPath);
        if (new FileInfo(keyPath).Length > MaxKeyFileBytes) throw RpcException.InvalidArgs("That file is too large to be a signing key.");
        if (Interlocked.Exchange(ref _publishing, 1) != 0) throw RpcException.Unavailable("An update is already being published.");

        try
        {
            // Asked again right before anything is signed or sent.
            await UnlockAsync().ConfigureAwait(false);
            var token = await _github.Account.GetAccessTokenAsync(ct).ConfigureAwait(false);
            // The key text is read here, used once and never logged or sent anywhere.
            var pem = await File.ReadAllTextAsync(keyPath, ct).ConfigureAwait(false);
            var progress = new Progress<string>(step =>
            {
                try { _services.Events.Publish("dev.publish", new { step }); } catch { /* page closing */ }
            });
            var result = await new ReleasePublisher(_github.Api).PublishAsync(token, exe, version, a.Notes, pem, testBuild, progress, ct).ConfigureAwait(false);
            _log.Info("Published RigControl " + version + (testBuild ? " (development build, pre-release)" : "") + " on GitHub: " + result.ReleaseUrl);
            return new { url = result.ReleaseUrl, version = version.ToString(), testBuild };
        }
        catch (Exception ex) when (ex is GitHubException or UpdateException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            _log.Warn("Publishing " + version + " failed: " + ex.Message);
            throw RpcException.Unavailable(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _publishing, 0);
            try { _services.Events.Publish("dev.publish", new { step = (string?)null }); } catch { /* page closing */ }
        }
    }

    /// <summary>A RigControl.exe that says it is another version would be offered as the wrong update: refused.</summary>
    private static void CheckExeVersion(string exe, AppVersion version)
    {
        string? text;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            text = info.ProductVersion ?? info.FileVersion;
        }
        catch (Exception)
        {
            return; // no version in the file: nothing to compare
        }
        if (AppVersion.TryParseLoose(text, out var inFile) && inFile.Value != version)
            throw RpcException.InvalidArgs("That program says it is version " + inFile.Value + ", not " + version + ".");
    }

    private async Task<object> ListReviewsAsync(CancellationToken ct)
    {
        RequireUnlocked();
        try
        {
            // Signed in: GitHub allows more searches. Not signed in: the public search still works.
            string? token = null;
            if (_github.Account.Login is not null)
            {
                try { token = await _github.Account.GetAccessTokenAsync(ct).ConfigureAwait(false); }
                catch (GitHubException) { token = null; }
            }
            var issues = await _github.Api.SearchIssuesAsync(token, Reviews.MarkerPhrase, ct).ConfigureAwait(false);
            return issues.Select(Reviews.Read).Where(r => r is not null).Select(r => new
            {
                number = r!.Number,
                url = r.Url,
                author = r.Author,
                stars = r.Stars,
                version = r.Version,
                text = r.Text,
                createdAt = r.CreatedAt?.ToString("o"),
                state = r.State,
            }).ToArray();
        }
        catch (GitHubException ex)
        {
            throw RpcException.Unavailable(ex.Message);
        }
    }

    private sealed class TokenArgs
    {
        public string? Token { get; set; }
    }

    private sealed class PublishArgs
    {
        public string? ExePath { get; set; }
        public string? Version { get; set; }
        public string? Notes { get; set; }
        public string? KeyPath { get; set; }

        /// <summary>A test build: published as a GitHub pre-release, so only the test channel offers it.</summary>
        public bool? TestBuild { get; set; }
    }
}
