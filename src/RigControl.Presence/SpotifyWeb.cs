using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using RigControl.Core.Logging;

namespace RigControl.Presence;

/// <summary>What the Spotify account is playing, as Spotify's Web API reports it, with the album art (if any).</summary>
public sealed record SpotifyNow(MusicNow Music, string? ArtUrl);

/// <summary>
/// Reads what a Spotify account is playing on any of its devices, from Spotify's Web API, with a sign-in the phone
/// made (PKCE, the app's public client id, read-only scopes). Used for the Discord music card when the PC itself
/// isn't playing anything.
///
/// Rules:
///  - Only https to accounts.spotify.com and api.spotify.com, no redirects, bounded answers, strict parsing.
///  - Neither token is ever logged or returned. The refresh token is handed to <c>onTokenChanged</c> (the module keeps
///    it encrypted for the Windows user); the access token lives in memory only.
///  - At most one request every 10 s; a 429 waits as long as Spotify's Retry-After says.
///  - A sign-in Spotify no longer accepts (invalid_grant) is forgotten and <see cref="Problem"/> says so.
/// </summary>
public sealed class SpotifyWebSource
{
    /// <summary>RigControl's public Spotify client id (PKCE: there is no client secret).</summary>
    public const string ClientId = "891243e2ce084409945d7f5e49b95fc8";
    public const string ExpiredMessage = "Spotify sign-in expired, connect again from the phone";
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);
    /// <summary>A reading older than this no longer counts as playing (the last few polls failed).</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(35);

    internal static readonly Uri TokenUri = new("https://accounts.spotify.com/api/token");
    internal static readonly Uri PlayingUri = new("https://api.spotify.com/v1/me/player/currently-playing");
    private const int MaxAnswerBytes = 256 * 1024;
    private const int MaxText = 256;
    private static readonly JsonDocumentOptions Strict = new() { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    private readonly HttpClient _http;
    private readonly ILog _log;
    private readonly Func<DateTime> _now;
    private readonly Action<string?> _onTokenChanged;
    private readonly object _gate = new();

    private string? _refresh;
    private string? _access;
    private DateTime _accessUntil = DateTime.MinValue;
    /// <summary>Bumped on every connect and disconnect, so an answer to an older sign-in is dropped.</summary>
    private int _generation;
    private DateTime _nextPoll = DateTime.MinValue;
    private SpotifyNow? _current;
    private DateTime _currentAt = DateTime.MinValue;
    private string? _problem;
    private string? _lastLogged;

    /// <param name="onTokenChanged">Called with a new refresh token Spotify handed out, or null when the sign-in was forgotten.</param>
    public SpotifyWebSource(HttpClient http, ILog log, Action<string?> onTokenChanged, Func<DateTime>? now = null)
    {
        _http = http;
        _log = log;
        _onTokenChanged = onTokenChanged;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>A dedicated client: no redirects, no cookies, short timeouts.</summary>
    public static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var h = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("RigControl");
        return h;
    }

    public bool Connected
    {
        get { lock (_gate) return _refresh is not null; }
    }

    /// <summary>True when <paramref name="refreshToken"/> is the sign-in in use right now.</summary>
    public bool IsCurrent(string refreshToken)
    {
        lock (_gate) return _refresh == refreshToken;
    }

    /// <summary>Why Spotify can't be read right now, in plain words; null when fine.</summary>
    public string? Problem
    {
        get { lock (_gate) return _problem; }
    }

    /// <summary>Uses this sign-in from now on (a new one replaces the old). Does not call <c>onTokenChanged</c>.</summary>
    public void Connect(string refreshToken)
    {
        lock (_gate)
        {
            _refresh = refreshToken;
            _access = null;
            _accessUntil = DateTime.MinValue;
            _generation++;
            _nextPoll = DateTime.MinValue;
            _current = null;
            _problem = null;
            _lastLogged = null;
        }
    }

    /// <summary>Forgets the sign-in. Does not call <c>onTokenChanged</c>.</summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            _refresh = null;
            _access = null;
            _generation++;
            _current = null;
            _problem = null;
        }
    }

    /// <summary>What the account plays now (the position moved on since the last poll), or null.</summary>
    public SpotifyNow? Get()
    {
        lock (_gate)
        {
            if (_current is null || _refresh is null) return null;
            var age = _now() - _currentAt;
            if (age > Fresh || age < TimeSpan.Zero) return null;
            var m = _current.Music;
            if (!m.Playing) return _current;
            var pos = m.PositionSec + age.TotalSeconds;
            if (m.DurationSec > 0) pos = Math.Min(pos, m.DurationSec);
            return _current with { Music = m with { PositionSec = pos } };
        }
    }

    /// <summary>
    /// One poll when one is due (every 10 s, later after a 429). Never throws except for cancellation; problems are
    /// logged once per change and kept in <see cref="Problem"/>.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        int gen;
        lock (_gate)
        {
            if (_refresh is null || _now() < _nextPoll) return;
            _nextPoll = _now() + PollEvery;
            gen = _generation;
        }
        try
        {
            await PollAsync(gen, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // No network, a timeout: try again at the next poll. The reading goes stale by itself.
            Note(gen, "Spotify didn't answer; trying again.", warn: false);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            Note(gen, "Spotify's answer couldn't be read; trying again.", warn: true);
        }
    }

    private async Task PollAsync(int gen, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var access = await AccessTokenAsync(gen, ct).ConfigureAwait(false);
            if (access is null) return;
            using var req = new HttpRequestMessage(HttpMethod.Get, PlayingUri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var res = await SendAsync(req, ct).ConfigureAwait(false);
            switch (res.StatusCode)
            {
                case HttpStatusCode.NoContent:
                    Set(gen, null);
                    return;
                case HttpStatusCode.OK:
                    var body = await ReadAsync(res, ct).ConfigureAwait(false);
                    Set(gen, body.Length == 0 ? null : Parse(body));
                    return;
                case HttpStatusCode.Unauthorized when attempt == 0:
                    // The access token ran out early (or was revoked): refresh once and ask again.
                    lock (_gate)
                    {
                        if (gen != _generation) return;
                        _access = null;
                    }
                    continue;
                case HttpStatusCode.TooManyRequests:
                    BackOff(gen, res);
                    return;
                default:
                    Note(gen, $"Spotify answered {(int)res.StatusCode}; trying again.", warn: (int)res.StatusCode < 500);
                    if ((int)res.StatusCode >= 500) Later(gen, TimeSpan.FromSeconds(30));
                    return;
            }
        }
    }

    /// <summary>The access token, refreshed when it has (nearly) run out. Null when there is none to be had.</summary>
    private async Task<string?> AccessTokenAsync(int gen, CancellationToken ct)
    {
        string refresh;
        lock (_gate)
        {
            if (gen != _generation || _refresh is null) return null;
            if (_access is not null && _now() < _accessUntil) return _access;
            refresh = _refresh;
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = ClientId,
            }),
        };
        using var res = await SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
        {
            BackOff(gen, res);
            return null;
        }
        var body = await ReadAsync(res, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var error = ErrorCode(body);
            if (error == "invalid_grant")
            {
                // Revoked, expired or used up: it will never work again.
                bool forget;
                lock (_gate)
                {
                    forget = gen == _generation;
                    if (forget)
                    {
                        _refresh = null;
                        _access = null;
                        _current = null;
                        _generation++;
                        _problem = ExpiredMessage;
                    }
                }
                if (forget)
                {
                    _log.Warn("Discord status: " + ExpiredMessage + ". The saved Spotify sign-in was forgotten.");
                    _onTokenChanged(null);
                }
                return null;
            }
            Note(gen, $"Spotify refused to renew the sign-in ({(error is null ? ((int)res.StatusCode).ToString(CultureInfo.InvariantCulture) : error)}); trying again later.", warn: true);
            Later(gen, TimeSpan.FromMinutes(5));
            return null;
        }
        var (access, expiresIn, newRefresh) = ParseToken(body);
        bool rotated;
        lock (_gate)
        {
            if (gen != _generation) return null;
            _access = access;
            // A minute early, so a token never runs out in the middle of a poll.
            _accessUntil = _now().AddSeconds(Math.Max(30, expiresIn - 60));
            rotated = newRefresh is not null && newRefresh != _refresh;
            if (rotated) _refresh = newRefresh;
            _problem = null;
        }
        if (rotated) _onTokenChanged(newRefresh);
        return access;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (!Allowed(req.RequestUri)) throw new InvalidOperationException("Not a Spotify address.");
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>Only https to Spotify's sign-in and Web API hosts, on the standard port.</summary>
    internal static bool Allowed(Uri? u) =>
        u is { IsAbsoluteUri: true } && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo == ""
        && (u.Host == "accounts.spotify.com" || u.Host == "api.spotify.com");

    /// <summary>The answer's body, at most 256 KB (anything bigger is refused, not cut).</summary>
    private static async Task<byte[]> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength is > MaxAnswerBytes) throw new InvalidOperationException("Spotify's answer is too big.");
        await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxAnswerBytes) throw new InvalidOperationException("Spotify's answer is too big.");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private void BackOff(int gen, HttpResponseMessage res)
    {
        var wait = TimeSpan.FromSeconds(30);
        if (res.Headers.RetryAfter?.Delta is { } d) wait = d;
        else if (res.Headers.RetryAfter?.Date is { } at) wait = at - DateTimeOffset.UtcNow;
        else if (res.Headers.TryGetValues("Retry-After", out var v)
                 && int.TryParse(v.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var secs)) wait = TimeSpan.FromSeconds(secs);
        wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, PollEvery.TotalSeconds, 3600));
        Later(gen, wait);
        Note(gen, $"Spotify asked to wait {(int)wait.TotalSeconds} s.", warn: false);
    }

    private void Later(int gen, TimeSpan wait)
    {
        lock (_gate)
        {
            if (gen != _generation) return;
            var at = _now() + wait;
            if (at > _nextPoll) _nextPoll = at;
        }
    }

    private void Set(int gen, SpotifyNow? now)
    {
        lock (_gate)
        {
            if (gen != _generation) return;
            _current = now;
            _currentAt = _now();
            _problem = null;
            _lastLogged = null;
        }
    }

    /// <summary>Logs a problem once until something works again. Messages never carry a token.</summary>
    private void Note(int gen, string message, bool warn)
    {
        lock (_gate)
        {
            if (gen != _generation || _lastLogged == message) return;
            _lastLogged = message;
        }
        if (warn) _log.Warn("Discord status: " + message);
        else _log.Info("Discord status: " + message);
    }

    // ---------------------------------------------------------------- parsing

    internal static (string Access, int ExpiresIn, string? Refresh) ParseToken(byte[] body)
    {
        using var doc = JsonDocument.Parse(body, Strict);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) throw new FormatException("Not an object.");
        if (!r.TryGetProperty("access_token", out var a) || a.ValueKind != JsonValueKind.String || a.GetString() is not { Length: >= 16 and <= 4096 } access
            || access.Any(c => c <= ' ' || c > '~'))
            throw new FormatException("No access token.");
        var expires = 3600;
        if (r.TryGetProperty("expires_in", out var e))
        {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out expires) || expires <= 0 || expires > 86400) throw new FormatException("Bad expiry.");
        }
        string? refresh = null;
        if (r.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
        {
            var t = rt.GetString()!;
            if (t.Length is >= 16 and <= 1024 && t.All(c => char.IsAsciiLetterOrDigit(c) || "._~+/=-".Contains(c))) refresh = t;
        }
        return (access, expires, refresh);
    }

    /// <summary>The "error" of an OAuth error answer, when it is one.</summary>
    internal static string? ErrorCode(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, Strict);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= 64 } s && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                return s;
        }
        catch (JsonException) { /* not JSON */ }
        return null;
    }

    /// <summary>
    /// GET /v1/me/player/currently-playing. A song gives a reading; an ad, a podcast episode or anything else Spotify
    /// doesn't describe as a track (item null) gives none.
    /// </summary>
    internal static SpotifyNow? Parse(byte[] body)
    {
        using var doc = JsonDocument.Parse(body, Strict);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) throw new FormatException("Not an object.");
        if (r.TryGetProperty("currently_playing_type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != "track") return null;
        if (!r.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
        if (item.TryGetProperty("type", out var it) && it.ValueKind == JsonValueKind.String && it.GetString() != "track") return null;
        var title = Text(item, "name");
        if (title == "") return null;
        var playing = r.TryGetProperty("is_playing", out var p) && p.ValueKind == JsonValueKind.True;
        var progress = Ms(r, "progress_ms");
        var duration = Ms(item, "duration_ms");
        var artists = new List<string>();
        if (item.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array)
            foreach (var x in ar.EnumerateArray().Take(10))
                if (x.ValueKind == JsonValueKind.Object && Text(x, "name") is { Length: > 0 } n) artists.Add(n);
        var artist = Cut(string.Join(", ", artists));
        var album = "";
        string? art = null;
        if (item.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
        {
            album = Text(al, "name");
            if (al.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0
                && imgs[0].ValueKind == JsonValueKind.Object && imgs[0].TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                art = ArtUrl(u.GetString());
        }
        if (duration > 0) progress = Math.Min(progress, duration);
        return new SpotifyNow(new MusicNow("Spotify", title, artist, album, playing, progress, duration), art);
    }

    /// <summary>Album art is used only from Spotify's own image host, over https.</summary>
    internal static string? ArtUrl(string? s)
    {
        if (s is null || s.Length > 512 || !Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps || u.Host != "i.scdn.co" || !u.IsDefaultPort || u.UserInfo != "") return null;
        return u.AbsoluteUri;
    }

    private static string Text(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        var s = new string(v.GetString()!.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        return Cut(s);
    }

    private static string Cut(string s)
    {
        if (s.Length <= MaxText) return s;
        var end = char.IsHighSurrogate(s[MaxText - 1]) ? MaxText - 1 : MaxText;
        return s[..end];
    }

    private static double Ms(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var ms) || ms < 0) return 0;
        return Math.Min(ms, 86_400_000L) / 1000.0;
    }
}

/// <summary>
/// What the phone's Music app is playing, as the linked phone last reported it (about every 15 s while its app is
/// open). It counts for the music card only while fresh: a report older than 45 s is dropped.
/// </summary>
public sealed class PhoneMusic
{
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(45);
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private MusicNow? _music;
    private DateTime _at;

    public PhoneMusic(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>The newest report; null = nothing is playing.</summary>
    public void Set(MusicNow? music)
    {
        lock (_gate)
        {
            _music = music;
            _at = _now();
        }
    }

    /// <summary>The song, with its position moved on since the report, while the report is fresh; otherwise null.</summary>
    public MusicNow? Get()
    {
        lock (_gate)
        {
            if (_music is null) return null;
            var age = _now() - _at;
            if (age > Fresh || age < TimeSpan.Zero)
            {
                _music = null; // stale: the phone app was closed or lost its link
                return null;
            }
            if (!_music.Playing) return _music;
            var pos = _music.PositionSec + age.TotalSeconds;
            if (_music.DurationSec > 0) pos = Math.Min(pos, _music.DurationSec);
            return _music with { PositionSec = pos };
        }
    }
}
