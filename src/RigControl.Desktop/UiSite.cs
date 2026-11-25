using System.Text;
using System.Text.RegularExpressions;
using RigControl.Hosting;

namespace RigControl.Desktop;

/// <summary>What one file of the page is, so RigControl can tell a page that is loading from one that never asked.</summary>
internal enum UiFileKind
{
    Other,
    /// <summary>index.html</summary>
    Page,
    /// <summary>A script file.</summary>
    Script,
}

/// <summary>One file of the page, ready to hand to the window.</summary>
/// <param name="Classic">True when the window asked for it the "classic" way (see <see cref="UiMode.Classic"/>).</param>
internal sealed record UiResponse(byte[] Body, string ContentType, UiFileKind Kind = UiFileKind.Other, bool Classic = false);

/// <summary>
/// Hands the interface (the files carried inside RigControl.Hosting.dll) to the window, from memory, through the
/// window's own address (app://rigcontrol/). Nothing listens on any network port for this.
/// The files are the same ones the Windows app shows. Three small changes are made on the way out:
///  - the page's Content-Security-Policy is replaced by a stricter one that fits this window
///    (no connections at all: everything goes through the window's own message channel);
///  - this run's <see cref="PageKey"/> is written into the page, so RigControl can tell its own page's messages
///    from those of any other page;
///  - "classic": the one script is handed over as a plain script instead of a module, for a web view that will not
///    load module scripts from the window's own address.
/// </summary>
internal sealed partial class UiSite
{
    public const string Scheme = "app";
    public const string Host = "rigcontrol";
    public const string ClassicMark = "classic=1";

    public delegate bool AssetOpener(string urlPath, out Stream stream, out string contentType);

    private readonly AssetOpener _open;
    private readonly string? _pageKey;

    /// <param name="open">Where the files come from. Left out: the interface carried inside the program.</param>
    /// <param name="pageKey">This run's <see cref="PageKey"/> value, written into index.html. Left out: none is written.</param>
    public UiSite(AssetOpener? open = null, string? pageKey = null)
    {
        _open = open ?? UiAssets.TryOpen;
        _pageKey = pageKey;
    }

    /// <summary>The address the window opens for each way of loading the page.</summary>
    public static Uri SchemeAddress(bool classic)
        => new($"{Scheme}://{Host}/index.html{(classic ? "?" + ClassicMark : "")}");

    /// <summary>
    /// Answers a request the window made on its own scheme ("app://rigcontrol/assets/x.js?classic=1").
    /// Null for anything that is not one of RigControl's own files.
    /// </summary>
    public UiResponse? ForSchemeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return null;
        var classic = uri.Query.TrimStart('?').Split('&').Contains(ClassicMark, StringComparer.Ordinal);
        return Get(uri.AbsolutePath, classic);
    }

    /// <summary>One file. <paramref name="path"/> is the part after the origin ("/", "/index.html", "/assets/x.css").</summary>
    public UiResponse? Get(string path, bool classic)
    {
        var key = Uri.UnescapeDataString(path).TrimStart('/');
        if (key.Length == 0) key = "index.html";
        if (key.Contains("..", StringComparison.Ordinal) || key.Contains('\\')) return null;
        if (!_open("/" + key, out var stream, out var contentType)) return null;

        byte[] body;
        using (stream)
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            body = copy.ToArray();
        }
        // Only the bare type: the window's scheme handlers take "text/html", not "text/html; charset=utf-8".
        // Every text file here is UTF-8 and the page says so itself.
        var type = contentType.Split(';')[0].Trim();

        var kind = UiFileKind.Other;
        if (key.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            kind = UiFileKind.Page;
            body = Encoding.UTF8.GetBytes(Index(Encoding.UTF8.GetString(body), classic, _pageKey));
        }
        else if (type == "text/javascript")
        {
            kind = UiFileKind.Script;
            if (classic) body = Encoding.UTF8.GetBytes(AsClassicScript(Encoding.UTF8.GetString(body)));
        }
        return new UiResponse(body, type, kind, classic);
    }

    /// <summary>
    /// The policy for this window. Nothing may be loaded from anywhere but RigControl's own files, and the page may
    /// not connect anywhere. The window's own scheme is named next to 'self', because it is not known whether these
    /// web views count a scheme of the app's own as "self".
    /// </summary>
    public static string Policy()
    {
        const string own = "'self' " + Scheme + ":";
        return $"default-src 'none'; script-src {own}; style-src {own} 'unsafe-inline'; img-src {own} data:; " +
               $"font-src {own} data:; connect-src 'none'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'";
    }

    internal static string Index(string html, bool classic, string? pageKey = null)
    {
        // A page file never brings a key of its own: only this run's is in the page the window gets.
        html = KeyMeta().Replace(html, "");
        var meta = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{Policy()}\" />";
        if (!string.IsNullOrEmpty(pageKey) && pageKey.All(char.IsAsciiLetterOrDigit))
            meta += $"<meta name=\"{PageKey.MetaName}\" content=\"{pageKey}\" />";
        if (CspMeta().IsMatch(html)) html = CspMeta().Replace(html, meta, 1);
        else if (HeadOpen().IsMatch(html)) html = HeadOpen().Replace(html, m => m.Value + meta, 1);

        if (classic)
        {
            html = ModulePreload().Replace(html, "");
            html = ModuleScript().Replace(html, m =>
            {
                var src = m.Groups["src"].Value;
                return $"<script defer src=\"{src}{(src.Contains('?') ? "&" : "?")}{ClassicMark}\"></script>";
            });
            html = CrossOrigin().Replace(html, "");
        }
        return html;
    }

    /// <summary>
    /// True when the built script can run as a plain script: it brings in nothing with "import" at its top, hands
    /// nothing out with "export", and does not ask where it was loaded from.
    /// </summary>
    internal static bool CanBeClassic(string js)
    {
        var start = js.AsSpan().TrimStart();
        if (start.StartsWith("import", StringComparison.Ordinal) && start.Length > 6 && !char.IsLetterOrDigit(start[6]) && start[6] != '_' && start[6] != '$' && start[6] != '(')
            return false;
        if (js.Contains("import.meta", StringComparison.Ordinal)) return false;
        return !TrailingExport().IsMatch(js);
    }

    /// <summary>Wraps the script so its names stay its own and it runs in strict mode, as a module would.</summary>
    internal static string AsClassicScript(string js)
        => CanBeClassic(js) ? "(function(){\"use strict\";\n" + js + "\n})();" : js;

    [GeneratedRegex(@"<meta\s+http-equiv=""Content-Security-Policy""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex CspMeta();

    [GeneratedRegex(@"<meta\s+name=""" + PageKey.MetaName + @"""[^>]*>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex KeyMeta();

    [GeneratedRegex(@"<head[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadOpen();

    [GeneratedRegex(@"<link\b[^>]*\brel=""modulepreload""[^>]*>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ModulePreload();

    [GeneratedRegex(@"<script\b(?=[^>]*\btype=""module"")[^>]*\bsrc=""(?<src>[^""]+)""[^>]*>\s*</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ModuleScript();

    [GeneratedRegex(@"(?<=<(?:link|script)\b[^>]*)\s+crossorigin(?:=""[^""]*"")?", RegexOptions.IgnoreCase)]
    private static partial Regex CrossOrigin();

    [GeneratedRegex(@"export\s*(\{[^}]*\}|default\b[^;]*)\s*;?\s*$")]
    private static partial Regex TrailingExport();
}
