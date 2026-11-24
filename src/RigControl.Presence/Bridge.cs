using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RigControl.Core.Logging;

namespace RigControl.Presence;

/// <summary>
/// How the handed-out browser extension tells this user's RigControl from any other program listening on the same
/// ports: a random secret per Windows user, kept in RigControl's data folder and written into the extension copy that
/// "Get the browser extension" hands out. The extension sends a random number; RigControl answers with
/// HMAC-SHA256(secret, number).
/// </summary>
public static class ExtensionLink
{
    /// <summary>The file in the handed-out extension folder that holds the secret.</summary>
    public const string TokenFile = "link-token.js";

    private static readonly Regex Nonce = new(@"(?:^|&)nonce=([0-9a-f]{16,64})(?:&|$)");
    private static readonly Regex Token = new(@"^[0-9a-f]{64}$");

    /// <summary>The number to sign from a query string ("nonce=..."), or null when there is none or it is not hex.</summary>
    public static string? NonceFrom(string query) => Nonce.Match(query) is { Success: true } m ? m.Groups[1].Value : null;

    public static string Proof(string token, string nonce) =>
        Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();

    /// <summary>Reads this user's secret, making a new one the first time. Null when the file can't be read or made.</summary>
    public static string? LoadOrCreateToken(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var have = File.ReadAllText(file).Trim();
                if (Token.IsMatch(have)) return have;
            }
            var made = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, made);
            return made;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The text of <see cref="TokenFile"/> for a handed-out copy.</summary>
    public static string TokenScript(string? token) =>
        "// Written by RigControl for this Windows user. With it the extension only reports to your own RigControl.\n" +
        "// Keep this folder to yourself.\n" +
        $"const RP_TOKEN = '{(token is not null && Token.IsMatch(token) ? token : "")}';\n";
}

/// <summary>
/// The local link the browser extension posts to (RichPresence's protocol): http://127.0.0.1:47610-47619,
/// POST /watching and GET /status. Only this PC can reach it, and only browser-extension requests are accepted: a
/// website can't send the X-RichPresence header without a CORS preflight (never allowed), and its Origin is never
/// chrome-extension:// or moz-extension://.
///
/// One link per Windows user: each signed-in user's RigControl takes the first free port of the ten, and a
/// connection is refused unless the program that made it runs in this Windows session as this user. So another
/// user's browser can never set this user's Watching card; its extension moves on to the next port.
///
/// The other direction cannot be guarded from here: nothing stops another program on the PC from listening on a
/// lower port and calling itself RigControl. So the copy of the extension that RigControl hands out carries this
/// user's secret (<see cref="ExtensionLink"/>), asks "/status" to prove it knows it, and only reports to a listener
/// that does. A copy of the extension without the secret (loaded from the source folder) cannot check and says so.
/// </summary>
public sealed class ExtensionBridge : IDisposable
{
    /// <summary>The first of the ports tried, and how many are tried (47610-47619). The extension knows the same range.</summary>
    public const int FirstPort = 47610;
    public const int PortCount = 10;
    private static readonly Regex ExtOrigin = new(@"^(chrome|moz)-extension://[\w-]+$");
    private static readonly Regex Thumb = new(@"^https://(i\.ytimg\.com/vi/[\w-]{6,20}/\w+\.jpg|static-cdn\.jtvnw\.net/previews-ttv/live_user_\w{3,25}-\d+x\d+\.jpg)$");

    private readonly ILog _log;
    private readonly string _version;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ExtReport>> _reports = new();
    private readonly Dictionary<string, string> _logged = new();
    private readonly int _firstPort;
    private readonly Func<string?>? _linkToken;
    private PeerGuard? _guard;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _busyLogged;
    private volatile int _port;

    /// <param name="guard">Who may connect (this Windows user's programs only). Tests pass their own.</param>
    /// <param name="firstPort">The first of the ten ports to try. Tests use a range of their own.</param>
    /// <param name="linkToken">This user's secret for the handed-out extension (null: no proof is given).</param>
    public ExtensionBridge(ILog log, string version, Func<DateTime>? now = null, PeerGuard? guard = null, int firstPort = FirstPort,
        Func<string?>? linkToken = null)
    {
        _linkToken = linkToken;
        _log = log;
        _version = version;
        _now = now ?? (() => DateTime.UtcNow);
        _guard = guard;
        _firstPort = firstPort;
    }

    public bool Listening => _listener is not null;

    /// <summary>The port it is listening on, or null while it isn't.</summary>
    public int? Port => _port > 0 ? _port : null;

    /// <summary>The report to use for a service (playing tab first, then newest), if one arrived in the last 15 s.</summary>
    public ExtReport? Get(string service) => Watching.Pick(_reports.GetValueOrDefault(service), 15, _now());

    /// <summary>Starts listening on the first free port of the ten; if every one is taken it tries again every 30 s.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _guard ??= PeerGuard.ForThisUser();
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var l = Bind();
                    if (l is null)
                    {
                        if (!_busyLogged) _log.Info($"Browser extension link: ports {_firstPort}-{_firstPort + PortCount - 1} are all in use; trying again every 30 s.");
                        _busyLogged = true;
                    }
                    else
                    {
                        _listener = l;
                        _port = ((IPEndPoint)l.LocalEndpoint).Port;
                        _busyLogged = false;
                        _log.Info($"Browser extension link listening on 127.0.0.1:{_port}.");
                        await AcceptLoop(l, ct).ConfigureAwait(false);
                    }
                }
                catch (SocketException) { /* the listener broke: start again below */ }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                finally
                {
                    try { _listener?.Stop(); } catch { /* ignore */ }
                    _listener = null;
                    _port = 0;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    /// <summary>The first free port, or null. A port another Windows user's RigControl (or RichPresence) holds is passed over.</summary>
    private TcpListener? Bind()
    {
        for (var port = _firstPort; port < _firstPort + PortCount; port++)
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            try
            {
                // Nobody else may listen on the same port alongside this one.
                l.ExclusiveAddressUse = true;
                l.Start();
                return l;
            }
            catch (SocketException)
            {
                try { l.Stop(); } catch { /* ignore */ }
            }
        }
        return null;
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() =>
            {
                using (client)
                {
                    try
                    {
                        // Who is on the other end? Only this Windows user's own programs are answered.
                        var mine = client.Client.RemoteEndPoint is IPEndPoint remote && client.Client.LocalEndPoint is IPEndPoint local
                                   && IPAddress.IsLoopback(remote.Address) && _guard!.Allows(remote.Port, local.Port);
                        Handle(client.GetStream(), mine);
                    }
                    catch { /* a bad request just closes */ }
                }
            }, ct);
        }
    }

    private void Handle(Stream stream, bool mine)
    {
        stream.ReadTimeout = 2000;
        stream.WriteTimeout = 2000;
        var buf = new byte[8192];
        int got = 0, end = -1;
        while (end < 0 && got < buf.Length)
        {
            var n = stream.Read(buf, got, buf.Length - got);
            if (n <= 0) break;
            got += n;
            end = Encoding.ASCII.GetString(buf, 0, got).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        if (end < 0) return;
        if (!mine)
        {
            // Another Windows user's browser, or a program that can't be identified: refused whatever it asked.
            // That user's extension then finds its own RigControl on another port.
            lock (_logged)
            {
                if (_logged.TryAdd("peer", "")) _log.Info("Browser extension link: refused a connection that didn't come from this Windows user.");
            }
            Respond(stream, 403, null, null);
            return;
        }
        var head = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
        var reqLine = head[0].Split(' ');
        if (reqLine.Length < 2) return;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        var origin = headers.GetValueOrDefault("origin") ?? "";
        var allow = ExtOrigin.IsMatch(origin) ? origin : null;
        var fromExtension = allow is not null && headers.GetValueOrDefault("x-richpresence") == "1";
        var body = HandleRequest(reqLine[0], reqLine[1], fromExtension, () =>
        {
            if (!int.TryParse(headers.GetValueOrDefault("content-length"), out var len) || len <= 0 || len > 8192) return null;
            var b = new byte[len];
            var have = Math.Min(got - (end + 4), len);
            if (have > 0) Array.Copy(buf, end + 4, b, 0, have);
            while (have < len)
            {
                var n = stream.Read(b, have, len - have);
                if (n <= 0) break;
                have += n;
            }
            return Encoding.UTF8.GetString(b, 0, have);
        });
        // What a browser sends that is refused or only checked, once per kind: tells apart a site, an extension and a
        // browser's own "local network" check when something doesn't connect.
        if (body.Code is 403 or 204 && reqLine[0] != "POST")
        {
            // Only fixed values go into the key, so a page sending endless different requests can't grow it or flood the log.
            var method = reqLine[0] is "GET" or "OPTIONS" ? reqLine[0] : "other";
            var path = reqLine[1].Split('?')[0] is "/status" or "/watching" ? reqLine[1].Split('?')[0] : "other";
            var kind = $"{method} {path} origin={(origin == "" ? "none" : allow is not null ? "extension" : "other")} header={(headers.ContainsKey("x-richpresence") ? "yes" : "no")}"
                       + $" pna={(headers.ContainsKey("access-control-request-private-network") || headers.ContainsKey("access-control-request-local-network") ? "yes" : "no")}";
            lock (_logged)
            {
                if (_logged.TryAdd("req:" + kind, ""))
                {
                    // A site's origin, shown once per kind: printable characters only, short.
                    var shown = new string(origin.Where(ch => ch is >= ' ' and <= '~').Take(60).ToArray());
                    _log.Info($"Browser extension link: {body.Code} for {kind}" + (allow is null && shown != "" ? $" ({shown})" : ""));
                }
            }
        }
        var localCheck = headers.ContainsKey("access-control-request-private-network") || headers.ContainsKey("access-control-request-local-network");
        Respond(stream, body.Code, body.Json, allow, localCheck && allow is not null);
    }

    /// <summary>The request logic, separate from the socket so it can be tested.</summary>
    internal (int Code, string? Json) HandleRequest(string method, string path, bool fromExtension, Func<string?> readBody)
    {
        if (method == "OPTIONS") return (204, null);
        if (!fromExtension) return (403, null);
        var query = "";
        var q = path.IndexOf('?');
        if (q >= 0)
        {
            query = path[(q + 1)..];
            path = path[..q];
        }
        if (method == "GET" && path == "/status")
        {
            var list = new JsonArray();
            foreach (var (svc, tabs) in _reports)
            {
                var e = Watching.Pick(tabs, 30, _now());
                if (e is not null) list.Add(new JsonObject { ["service"] = svc, ["title"] = e.Title, ["episode"] = e.Episode, ["playing"] = e.Playing });
            }
            var answer = new JsonObject { ["app"] = "RigControl", ["version"] = _version, ["watching"] = list };
            // The handed-out extension sends a random number and expects it back signed with this user's secret.
            // The secret itself is never sent.
            if (ExtensionLink.NonceFrom(query) is { } nonce)
            {
                string? token = null;
                try { token = _linkToken?.Invoke(); } catch { /* no proof */ }
                if (!string.IsNullOrEmpty(token)) answer["proof"] = ExtensionLink.Proof(token, nonce);
            }
            return (200, answer.ToJsonString());
        }
        if (method == "POST" && path == "/watching")
        {
            var text = readBody();
            if (text is null) return (400, null);
            JsonNode? j;
            try { j = JsonNode.Parse(text); } catch { return (400, null); }
            var svc = Str(j?["service"]);
            if (!Watching.ServiceNames.Contains(svc)) return (400, null);
            // An older extension sends no tab id: all its tabs share one slot.
            var tab = Str(j?["tab"]);
            if (!Regex.IsMatch(tab, @"^[\w-]{1,64}$")) tab = "default";
            // Numbers always arrive with a dot, whatever this PC's language is.
            double.TryParse(Str(j?["position"]), NumberStyles.Float, CultureInfo.InvariantCulture, out var pos);
            double.TryParse(Str(j?["duration"]), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur);
            // A thumbnail is only taken from the sites' own image servers.
            var img = Str(j?["image"]).Trim();
            if (!Thumb.IsMatch(img)) img = "";
            var playing = j?["playing"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb;
            var report = new ExtReport(Clip(Str(j?["title"]), 128), Clip(Str(j?["episode"]), 128), img, playing,
                double.IsFinite(pos) ? pos : 0, double.IsFinite(dur) ? dur : 0, _now());
            // A fresh table every time, without tabs gone quiet for a minute.
            var tabs = new Dictionary<string, ExtReport>();
            if (_reports.TryGetValue(svc, out var old))
                foreach (var (k, v) in old)
                    if ((_now() - v.At).TotalSeconds < 60) tabs[k] = v;
            tabs[tab] = report;
            _reports[svc] = tabs;
            var pick = Watching.Pick(tabs, 15, _now());
            lock (_logged)
            {
                if (pick is { Playing: true, Title.Length: > 0 } && _logged.GetValueOrDefault(svc) != $"{pick.Title}|{pick.Episode}")
                {
                    _logged[svc] = $"{pick.Title}|{pick.Episode}";
                    _log.Info($"Browser extension: {svc} - {pick.Title}{(pick.Episode != "" ? " - " + pick.Episode : "")}");
                }
            }
            return (204, null);
        }
        return (404, null);
    }

    private static string Str(JsonNode? n)
    {
        if (n is not JsonValue v) return "";
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        return v.ToJsonString();
    }

    private static string Clip(string v, int n)
    {
        var t = Regex.Replace(v, @"[\r\n\t]+", " ").Trim();
        return t.Length > n ? t[..n] : t;
    }

    /// <param name="allowLocal">
    /// Chrome and Edge's "local network access" check: before a page reaches 127.0.0.1 the browser may ask first, and
    /// only goes ahead when the server says yes. Said only to the extension's own origin.
    /// </param>
    private static void Respond(Stream s, int code, string? json, string? origin, bool allowLocal = false)
    {
        var status = code switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", _ => "Not Found" };
        var sb = new StringBuilder($"HTTP/1.1 {code} {status}\r\nConnection: close\r\nCache-Control: no-store\r\n");
        if (origin is not null)
            sb.Append($"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Headers: content-type, x-richpresence\r\nAccess-Control-Allow-Methods: GET, POST\r\n");
        if (origin is not null && allowLocal)
            sb.Append("Access-Control-Allow-Private-Network: true\r\nAccess-Control-Allow-Local-Network: true\r\n");
        var b = json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
        if (b.Length > 0) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append($"Content-Length: {b.Length}\r\n\r\n");
        var h = Encoding.ASCII.GetBytes(sb.ToString());
        s.Write(h, 0, h.Length);
        if (b.Length > 0) s.Write(b, 0, b.Length);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
    }
}
