using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RigControl.Core.Util;

/// <summary>
/// Reads what Windows' task scheduler prints with "schtasks /Query /XML". Unlike the table and CSV output, the XML
/// has the same element names in every Windows language, so nothing here depends on English words.
/// </summary>
public static class ScheduledTaskXml
{
    /// <summary>schtasks writes either UTF-16 or the console's code page, depending on the Windows version.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0) return Encoding.Unicode.GetString(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        // Element names and the task paths that matter are plain ASCII; other letters may come out wrong and are not used.
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>
    /// Whether the one task in this XML is enabled: its Settings/Enabled element, which Windows leaves out when it is
    /// "true". Null when the text is not a task at all.
    /// </summary>
    public static bool? Enabled(string xml)
    {
        var task = Parse(xml)?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "Task");
        return task is null ? null : Enabled(task);
    }

    /// <summary>
    /// Every task in the output of "schtasks /Query /XML ONE" (one document) or "schtasks /Query /XML" (every task's
    /// own document, one after the other), as (full path, enabled). The path is the comment Windows prints before
    /// each task, or else the task's own URI.
    /// </summary>
    public static IReadOnlyList<(string Path, bool Enabled)> List(string xml)
    {
        var result = new List<(string Path, bool Enabled)>();
        var root = Parse(xml);
        if (root is null) return result;
        string? comment = null;
        foreach (var node in root.DescendantNodesAndSelf())
        {
            if (node is XComment c)
            {
                comment = c.Value.Trim();
                continue;
            }
            if (node is not XElement { Name.LocalName: "Task" } task) continue;
            var uri = task.Elements().FirstOrDefault(e => e.Name.LocalName == "RegistrationInfo")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "URI")?.Value.Trim();
            var path = comment is { Length: > 1 } && comment[0] == Path.DirectorySeparatorChar ? comment : uri;
            comment = null;
            if (string.IsNullOrEmpty(path) || result.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add((path, Enabled(task)));
        }
        return result;
    }

    private static bool Enabled(XElement task)
    {
        var value = task.Elements().FirstOrDefault(e => e.Name.LocalName == "Settings")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value.Trim();
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0";
    }

    private static readonly Regex Declaration = new(@"<\?xml\b[^>]*\?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static XElement? Parse(string xml)
    {
        // Windows prints one "<?xml ... ?>" line for the whole list ("/XML ONE") or one before every task (plain
        // "/XML"), and the path comment may come before or after it. That line no longer fits a decoded string and
        // may only stand at the very start of a document, so every one is taken out.
        if (xml.IndexOf('<') < 0) return null;
        xml = Declaration.Replace(xml, "");
        try
        {
            // Wrapped, so several tasks (or a comment before the first) still make one document.
            return XElement.Parse("<all>" + xml.Replace("\0", "") + "</all>");
        }
        catch
        {
            return null;
        }
    }
}
