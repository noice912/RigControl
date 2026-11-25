using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RigControl.Lighting;

/// <summary>
/// The capture card's picture as OBS shows it, through OBS's own WebSocket server (protocol 5, built into OBS 28+).
/// Used while OBS has the card open: many USB capture cards can only be open in one program at a time. The
/// connection details are read from OBS's settings on this PC (the password is never logged or shown); only
/// read-only requests are sent (input list and settings, source screenshots).
/// </summary>
public sealed class ObsSource : IObsPicture
{
    private ClientWebSocket? _ws;
    private string? _sourceName;
    private int _requestId;
    private readonly byte[] _buffer = new byte[1 << 16];

    /// <summary>What is being read (the OBS source's name), for the status line.</summary>
    public string? SourceName => _sourceName;

    /// <summary>The newest picture shrunk to 48x27 BGRA, or null before the first.</summary>
    public byte[]? Grid { get; private set; }

    /// <summary>OBS's WebSocket settings (enabled, port, password) from its config file, or null if it has none.</summary>
    internal static (bool Enabled, int Port, string? Password)? ReadConfig()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "plugin_config", "obs-websocket", "config.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        var enabled = r.TryGetProperty("server_enabled", out var e) && e.ValueKind == JsonValueKind.True;
        var port = r.TryGetProperty("server_port", out var p) && p.TryGetInt32(out var n) ? n : 4455;
        var auth = r.TryGetProperty("auth_required", out var a) && a.ValueKind == JsonValueKind.True;
        var password = auth && r.TryGetProperty("server_password", out var pw) ? pw.GetString() : null;
        return (enabled, port, password);
    }

    /// <summary>OBS's answer to an authentication challenge (protocol 5).</summary>
    internal static string AuthResponse(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>Connects and finds the OBS source that shows the capture card named <paramref name="cardName"/>.</summary>
    public async Task StartAsync(string cardName, CancellationToken ct = default)
    {
        var cfg = ReadConfig() ?? throw new InvalidOperationException("OBS's WebSocket server isn't set up (OBS: Tools > WebSocket Server Settings)");
        if (!cfg.Enabled) throw new InvalidOperationException("OBS's WebSocket server is off (OBS: Tools > WebSocket Server Settings > Enable)");
        _ws = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{cfg.Port}"), timeout.Token).ConfigureAwait(false);

        var hello = await ReceiveAsync(timeout.Token).ConfigureAwait(false);
        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
        if (hello?["d"]?["authentication"] is JsonObject auth)
        {
            if (string.IsNullOrEmpty(cfg.Password)) throw new InvalidOperationException("OBS asks for a WebSocket password but none is saved in its settings");
            identify["authentication"] = AuthResponse(cfg.Password, (string)auth["salt"]!, (string)auth["challenge"]!);
        }
        await SendAsync(new JsonObject { ["op"] = 1, ["d"] = identify }, timeout.Token).ConfigureAwait(false);
        var identified = await ReceiveAsync(timeout.Token).ConfigureAwait(false);
        if ((int?)identified?["op"] != 2) throw new InvalidOperationException("OBS refused the WebSocket connection (wrong password?)");

        // The video capture source that uses this card; otherwise whatever OBS is showing.
        var inputs = await RequestAsync("GetInputList", new JsonObject { ["inputKind"] = "dshow_input" }, timeout.Token).ConfigureAwait(false);
        string? first = null;
        foreach (var input in inputs?["inputs"]?.AsArray() ?? new JsonArray())
        {
            var name = (string?)input?["inputName"];
            if (name is null) continue;
            first ??= name;
            var settings = await RequestAsync("GetInputSettings", new JsonObject { ["inputName"] = name }, timeout.Token).ConfigureAwait(false);
            var device = (string?)settings?["inputSettings"]?["video_device_id"] ?? "";
            if (cardName != "" && device.StartsWith(cardName, StringComparison.OrdinalIgnoreCase))
            {
                _sourceName = name;
                break;
            }
        }
        _sourceName ??= first;
        if (_sourceName is null)
        {
            var scene = await RequestAsync("GetCurrentProgramScene", null, timeout.Token).ConfigureAwait(false);
            _sourceName = (string?)scene?["currentProgramSceneName"] ?? throw new InvalidOperationException("OBS shows nothing to follow");
        }
    }

    /// <summary>The picture's colour (0xRRGGBB), or null when OBS sent nothing usable this time.</summary>
    public int? Read()
    {
        if (_ws is null || _sourceName is null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var shot = RequestAsync("GetSourceScreenshot", new JsonObject
        {
            ["sourceName"] = _sourceName,
            ["imageFormat"] = "bmp",
            ["imageWidth"] = ColorMath.GridW,
            ["imageHeight"] = ColorMath.GridH,
        }, timeout.Token).GetAwaiter().GetResult();
        var data = (string?)shot?["imageData"];
        if (data is null) return null;
        var comma = data.IndexOf(',');
        var grid = ParseBmp(Convert.FromBase64String(comma >= 0 ? data[(comma + 1)..] : data), ColorMath.GridW, ColorMath.GridH);
        if (grid is null) return null;
        Grid = grid;
        return ColorMath.Average(grid, ColorMath.GridW, ColorMath.GridH);
    }

    /// <summary>A 24- or 32-bit BMP of exactly width x height as top-down BGRA, or null for anything else.</summary>
    internal static byte[]? ParseBmp(byte[] bmp, int width, int height)
    {
        if (bmp.Length < 54 || bmp[0] != 'B' || bmp[1] != 'M') return null;
        var offset = BitConverter.ToInt32(bmp, 10);
        var w = BitConverter.ToInt32(bmp, 18);
        var h = BitConverter.ToInt32(bmp, 22);
        int bpp = BitConverter.ToUInt16(bmp, 28);
        if (w != width || Math.Abs(h) != height || bpp is not (24 or 32)) return null;
        var bytes = bpp / 8;
        var stride = (w * bytes + 3) & ~3;
        if (offset < 0 || offset + stride * height > bmp.Length) return null;
        var grid = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = h > 0 ? height - 1 - y : y; // positive height: stored bottom-up
            for (var x = 0; x < width; x++)
            {
                var s = offset + row * stride + x * bytes;
                var d = 4 * (y * width + x);
                grid[d] = bmp[s];
                grid[d + 1] = bmp[s + 1];
                grid[d + 2] = bmp[s + 2];
                grid[d + 3] = 255;
            }
        }
        return grid;
    }

    private async Task<JsonNode?> RequestAsync(string type, JsonObject? data, CancellationToken ct)
    {
        var id = (++_requestId).ToString();
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data is not null) d["requestData"] = data;
        await SendAsync(new JsonObject { ["op"] = 6, ["d"] = d }, ct).ConfigureAwait(false);
        while (true)
        {
            var msg = await ReceiveAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("OBS closed the connection");
            if ((int?)msg["op"] != 7 || (string?)msg["d"]?["requestId"] != id) continue;
            var status = msg["d"]?["requestStatus"];
            if ((bool?)status?["result"] != true) throw new InvalidOperationException($"OBS: {(string?)status?["comment"] ?? type + " failed"}");
            return msg["d"]?["responseData"];
        }
    }

    private Task SendAsync(JsonNode msg, CancellationToken ct) =>
        _ws!.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private async Task<JsonNode?> ReceiveAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws!.ReceiveAsync(_buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(_buffer, 0, r.Count);
            if (ms.Length > 16 << 20) throw new InvalidOperationException("OBS sent too much");
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray());
    }

    public void Dispose()
    {
        try { _ws?.Abort(); } catch { /* ignore */ }
        try { _ws?.Dispose(); } catch { /* ignore */ }
        _ws = null;
    }
}
