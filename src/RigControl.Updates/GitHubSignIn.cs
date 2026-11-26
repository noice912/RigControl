using System.Text.Json;

namespace RigControl.Updates;

/// <summary>What the user is shown while signing in: type <see cref="UserCode"/> at <see cref="VerificationUri"/>.</summary>
public sealed record DeviceCode(string Code, string UserCode, string VerificationUri, DateTimeOffset ExpiresAt, TimeSpan Interval);

/// <summary>
/// The sign-in's tokens. Never logged, never sent to the page, only kept protected (<see cref="GitHubAccount"/>).
/// GitHub App tokens run out (8 hours) and come with a refresh token (about 6 months).
/// </summary>
public sealed record GitHubTokens(string AccessToken, DateTimeOffset? AccessExpiresAt, string? RefreshToken, DateTimeOffset? RefreshExpiresAt)
{
    // The record's own text would hold the tokens; nothing that prints this object may ever show them.
    public override string ToString() => "GitHubTokens(hidden)";
}

/// <summary>
/// GitHub sign-in by device flow: the app asks GitHub for a code, the user types it on github.com in their normal
/// browser, and the app asks GitHub every few seconds whether it was accepted. Nothing listens on this PC.
/// </summary>
public sealed class GitHubSignIn
{
    /// <summary>
    /// The client id of the GitHub App "RigControl" (slug rigcontrol-desktop). A public value, not a secret. A build that
    /// blanks it has no GitHub sign-in ("not set up in this build"); anyone building their own can put their own app's id here.
    /// </summary>
    public const string ClientId = "Iv23lim0ousz4VbgaSJZ";

    public const string NotSetUp = "GitHub sign-in isn't set up in this build of RigControl.";

    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string GrantDevice = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;

    public GitHubSignIn(HttpClient http, string? clientId = ClientId, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _clientId = clientId ?? "";
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Configured => !string.IsNullOrWhiteSpace(_clientId);

    /// <summary>Asks GitHub for a code for the user to type.</summary>
    public async Task<DeviceCode> StartAsync(CancellationToken ct)
    {
        if (!Configured) throw new GitHubException(NotSetUp);
        using var doc = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string> { ["client_id"] = _clientId }, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (Str(root, "error") is { } error) throw new GitHubException(SignInError(error, Str(root, "error_description")));
        var code = Str(root, "device_code");
        var user = Str(root, "user_code");
        var uri = Str(root, "verification_uri");
        if (code is null || user is null || uri is null || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !GitHubHosts.IsAllowed(parsed))
            throw new GitHubException("GitHub's answer to the sign-in was not what RigControl expected.");
        var expires = Num(root, "expires_in") ?? 900;
        var interval = Num(root, "interval") ?? 5;
        return new DeviceCode(code, user, parsed.AbsoluteUri, _now().AddSeconds(expires), TimeSpan.FromSeconds(Math.Max(1, interval)));
    }

    /// <summary>
    /// Waits for the user to accept the code on github.com. Keeps to GitHub's interval and slows down when asked;
    /// gives up when the code runs out or the user declines.
    /// </summary>
    public async Task<GitHubTokens> WaitAsync(DeviceCode device, CancellationToken ct)
    {
        var interval = device.Interval;
        while (true)
        {
            await _delay(interval, ct).ConfigureAwait(false);
            if (_now() >= device.ExpiresAt) throw new GitHubException("The sign-in code ran out before it was used. Start the sign-in again.");

            using var doc = await PostFormAsync(TokenUrl, new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["device_code"] = device.Code,
                ["grant_type"] = GrantDevice,
            }, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            switch (Str(root, "error"))
            {
                case null:
                    return ReadTokens(root);
                case "authorization_pending":
                    continue;
                case "slow_down":
                    // GitHub says how long to wait from now on; without that, five seconds more than before.
                    interval = Num(root, "interval") is { } s ? TimeSpan.FromSeconds(s) : interval + TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                    throw new GitHubException("The sign-in code ran out before it was used. Start the sign-in again.");
                case "access_denied":
                    throw new GitHubException("The sign-in was declined on GitHub.");
                case { } other:
                    throw new GitHubException(SignInError(other, Str(root, "error_description")));
            }
        }
    }

    /// <summary>Gets a fresh access token with the refresh token (no client secret is needed for this app).</summary>
    public async Task<GitHubTokens> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (!Configured) throw new GitHubException(NotSetUp);
        using var doc = await PostFormAsync(TokenUrl, new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (Str(root, "error") is { } error)
            throw new GitHubException("GitHub would not renew the sign-in (" + error + "). Sign in with GitHub again.") { OAuthError = error };
        return ReadTokens(root);
    }

    private GitHubTokens ReadTokens(JsonElement root)
    {
        var access = Str(root, "access_token");
        if (string.IsNullOrEmpty(access)) throw new GitHubException("GitHub's answer to the sign-in held no token.");
        var now = _now();
        DateTimeOffset? accessExpires = Num(root, "expires_in") is { } a and > 0 ? now.AddSeconds(a) : null;
        DateTimeOffset? refreshExpires = Num(root, "refresh_token_expires_in") is { } r and > 0 ? now.AddSeconds(r) : null;
        return new GitHubTokens(access, accessExpires, Str(root, "refresh_token"), refreshExpires);
    }

    private async Task<JsonDocument> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");
        // GitHub answers these with 200 and an "error" field, which the callers read.
        return await GitHubHttp.SendJsonAsync(_http, request, ct, 64 * 1024).ConfigureAwait(false);
    }

    private static string SignInError(string error, string? description) => error switch
    {
        "device_flow_disabled" => "GitHub sign-in by code is switched off for RigControl's GitHub App.",
        "incorrect_client_credentials" => "GitHub does not know RigControl's GitHub App id in this build.",
        "unsupported_grant_type" or "incorrect_device_code" => "GitHub did not accept the sign-in code. Start the sign-in again.",
        _ => "GitHub sign-in failed (" + error + (string.IsNullOrEmpty(description) ? "" : ": " + description) + ").",
    };

    private static string? Str(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static long? Num(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n) ? n : null;
}
