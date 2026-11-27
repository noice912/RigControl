using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RigControl.Mobile.Core.Music;

/// <summary>
/// PKCE values for one sign-in (RFC 7636): a 64-character verifier from the URL-safe alphabet and its S256 challenge.
/// </summary>
public static class Pkce
{
    /// <summary>RFC 7636's unreserved characters.</summary>
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    public const int VerifierLength = 64;

    /// <summary>A new random verifier (64 characters, unreserved URL characters only).</summary>
    public static string CreateVerifier() => RandomNumberGenerator.GetString(Alphabet, VerifierLength);

    /// <summary>A random state value for one sign-in (32 URL-safe characters).</summary>
    public static string CreateState() => RandomNumberGenerator.GetString(Alphabet, 32);

    /// <summary>BASE64URL(SHA-256(ASCII(verifier))) without padding.</summary>
    public static string Challenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>The Spotify part of RigControl: the app's own client id (no secret: PKCE) and the read-only scopes.</summary>
public static class SpotifyApp
{
    public const string ClientId = "891243e2ce084409945d7f5e49b95fc8";
    public const string RedirectUri = "rigcontrol://callback";
    public const string CallbackScheme = "rigcontrol";
    public const string Scopes = "user-read-currently-playing user-read-playback-state";
    public const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
    public static readonly Uri TokenEndpoint = new("https://accounts.spotify.com/api/token");

    /// <summary>The sign-in page to open in the browser.</summary>
    public static Uri AuthorizeUrl(string codeChallenge, string state)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeChallenge);
        ArgumentException.ThrowIfNullOrEmpty(state);
        return new Uri(AuthorizeEndpoint
            + "?client_id=" + ClientId
            + "&response_type=code"
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&code_challenge_method=S256"
            + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
            + "&state=" + Uri.EscapeDataString(state)
            + "&scope=" + Uri.EscapeDataString(Scopes));
    }
}

/// <summary>One sign-in that was started: what to open, and what the callback must match.</summary>
public sealed class SpotifySignInRequest
{
    internal SpotifySignInRequest(string verifier, string state)
    {
        Verifier = verifier;
        State = state;
        Url = SpotifyApp.AuthorizeUrl(Pkce.Challenge(verifier), state);
    }

    public Uri Url { get; }
    internal string Verifier { get; }
    internal string State { get; }

    /// <summary>Only the page's address (the query stays out of logs).</summary>
    public override string ToString() => "Spotify sign-in " + SpotifyApp.AuthorizeEndpoint;
}

/// <summary>The end of a sign-in: a refresh token to hand to the PC, or a sentence for the person.</summary>
public sealed record SpotifySignInResult
{
    public string? RefreshToken { get; private init; }
    public string? Error { get; private init; }
    public bool Ok => RefreshToken is not null;

    public static SpotifySignInResult Success(string refreshToken) => new() { RefreshToken = refreshToken };
    public static SpotifySignInResult Fail(string error) => new() { Error = error };

    /// <summary>Never prints the token.</summary>
    public override string ToString() => Ok ? "Spotify sign-in: ok" : "Spotify sign-in failed: " + Error;
}

/// <summary>
/// Spotify sign-in with PKCE, as the phone does it: <see cref="Start"/> makes the page to open, and
/// <see cref="CompleteAsync"/> takes the rigcontrol://callback query, checks the state, and swaps the code for a
/// refresh token at Spotify (no client secret). Nothing is stored and nothing is logged: the token goes to the PC.
/// Only the most recent sign-in counts; a callback for an older one, or a second callback, is refused.
/// </summary>
public sealed class SpotifySignIn
{
    /// <summary>Same bounds as the PC checks (RigControl.Link LinkActions).</summary>
    private static readonly Regex TokenShape = new(@"\A[A-Za-z0-9._~+/=-]{16,1024}\z", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private SpotifySignInRequest? _pending;

    public SpotifySignIn(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Starts a new sign-in (replacing any unfinished one).</summary>
    public SpotifySignInRequest Start()
    {
        var request = new SpotifySignInRequest(Pkce.CreateVerifier(), Pkce.CreateState());
        lock (_gate)
            _pending = request;
        return request;
    }

    /// <summary>Whether a sign-in was started and is waiting for its callback.</summary>
    public bool IsWaiting
    {
        get { lock (_gate) return _pending is not null; }
    }

    /// <summary>Drops an unfinished sign-in (the person closed the browser).</summary>
    public void Cancel()
    {
        lock (_gate)
            _pending = null;
    }

    /// <summary>Finishes the sign-in from the callback's query (code and state, or error and state).</summary>
    public async Task<SpotifySignInResult> CompleteAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        SpotifySignInRequest pending;
        lock (_gate)
        {
            var state = query.GetValueOrDefault("state");
            if (_pending is null || state is null || !SameText(state, _pending.State))
                return SpotifySignInResult.Fail("That sign-in doesn't match the one this app started. Tap Connect Spotify and try again.");
            pending = _pending;
            _pending = null; // one use, whatever happens next
        }

        if (query.TryGetValue("error", out var error))
            return SpotifySignInResult.Fail(error == "access_denied"
                ? "Spotify sign-in was cancelled."
                : "Spotify didn't sign you in. Try again.");
        if (!query.TryGetValue("code", out var code) || code.Length is 0 or > 2048)
            return SpotifySignInResult.Fail("Spotify didn't send a sign-in code. Try again.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = SpotifyApp.RedirectUri,
            ["client_id"] = SpotifyApp.ClientId,
            ["code_verifier"] = pending.Verifier,
        });
        using var message = new HttpRequestMessage(HttpMethod.Post, SpotifyApp.TokenEndpoint) { Content = form };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return SpotifySignInResult.Fail(Readable(body) ?? $"Spotify refused the sign-in ({(int)response.StatusCode}). Try again.");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("refresh_token", out var rt) || rt.ValueKind != JsonValueKind.String
                || rt.GetString() is not { } token || !TokenShape.IsMatch(token))
                return SpotifySignInResult.Fail("Spotify's answer had no sign-in to keep. Try again.");
            return SpotifySignInResult.Success(token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return SpotifySignInResult.Fail("Couldn't reach Spotify. Check the internet connection and try again.");
        }
        catch (TaskCanceledException)
        {
            return SpotifySignInResult.Fail("Spotify took too long to answer. Try again.");
        }
        catch (JsonException)
        {
            return SpotifySignInResult.Fail("Spotify's answer couldn't be read. Try again.");
        }
    }

    /// <summary>A short sentence for Spotify's OAuth error codes; never echoes the body.</summary>
    private static string? Readable(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                return e.GetString() switch
                {
                    "invalid_grant" => "The sign-in code expired or was already used. Tap Connect Spotify and try again.",
                    "invalid_client" => "Spotify doesn't accept this app's sign-in right now.",
                    _ => null,
                };
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static bool SameText(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
