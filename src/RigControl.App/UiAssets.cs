using System.IO;
using System.Reflection;
using System.Text;

namespace RigControl.App;

/// <summary>Serves the web UI that was embedded into the exe at build time (resources named "ui/...").</summary>
internal static class UiAssets
{
    private static readonly Assembly Asm = typeof(UiAssets).Assembly;
    private static readonly Dictionary<string, string> Map = BuildMap();

    private static Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Asm.GetManifestResourceNames())
        {
            var normalised = name.Replace('\\', '/');
            if (normalised.StartsWith("ui/", StringComparison.OrdinalIgnoreCase))
                map[normalised[3..]] = name;
        }
        return map;
    }

    public static bool HasUi => Map.ContainsKey("index.html");

    /// <summary>Opens the asset for a URL path such as "/assets/index-abc.js". "/" maps to index.html.</summary>
    public static bool TryOpen(string urlPath, out Stream stream, out string contentType)
    {
        var key = urlPath.TrimStart('/').Replace('\\', '/');
        if (key.Length == 0) key = "index.html";

        if (!HasUi && key.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            stream = new MemoryStream(Encoding.UTF8.GetBytes(MissingUiPage));
            contentType = "text/html; charset=utf-8";
            return true;
        }

        if (key.Contains("..", StringComparison.Ordinal) || !Map.TryGetValue(key, out var resource))
        {
            stream = Stream.Null;
            contentType = "text/plain";
            return false;
        }

        using var source = Asm.GetManifestResourceStream(resource);
        if (source is null)
        {
            stream = Stream.Null;
            contentType = "text/plain";
            return false;
        }
        var copy = new MemoryStream();
        source.CopyTo(copy);
        copy.Position = 0;
        stream = copy;
        contentType = ContentTypeFor(key);
        return true;
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private const string MissingUiPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>RigControl</title></head>" +
        "<body style=\"background:#0D1014;color:#E8EAED;font-family:Segoe UI,sans-serif;padding:40px\">" +
        "<h2>The interface was not included in this build</h2>" +
        "<p>Build the UI first (npm run build in src/ui) and then rebuild RigControl.</p></body></html>";
}
