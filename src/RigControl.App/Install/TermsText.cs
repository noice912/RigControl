using System.Text;
using System.Text.RegularExpressions;

namespace RigControl.App.Install;

/// <summary>
/// The terms of use (Markdown in the repository) as plain text for the setup window: no stars, link targets or
/// table pipes, so the words read the same as on GitHub.
/// </summary>
internal static partial class TermsText
{
    public static string Plain(string markdown)
    {
        var output = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (TableRule().IsMatch(line)) continue; // |---|---|
            if (line.StartsWith('|'))
            {
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0);
                line = "• " + string.Join(" — ", cells);
            }
            line = Heading().Replace(line, "");
            line = Link().Replace(line, "$1");
            line = line.Replace("**", "").Replace("`", "");
            output.Append(line).Append('\n');
        }
        return output.ToString().Trim();
    }

    [GeneratedRegex(@"^\|?\s*:?-{3,}")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex Heading();

    /// <summary>[text](target) keeps the text; the file names it points to are named in the text already.</summary>
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();
}
