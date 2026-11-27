using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RigControl.Radio.Lyrics;

/// <summary>One line of lyrics and when it starts in the song (seconds).</summary>
public sealed record LyricLine(double Seconds, string Text);

/// <summary>Turns lyrics and heard speech into the same plain words, so they can be compared.</summary>
public static class LyricsText
{
    private static readonly Regex Timed = new(@"^\[(\d{1,2}):(\d{2}(?:\.\d{1,3})?)\]\s*(.*)$", RegexOptions.CultureInvariant);

    /// <summary>Lower case, no accents, apostrophes dropped ("don't" -> "dont"), everything else that isn't a letter or digit splits words.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\'' or '’' or '‘' or '`') continue;
            plain.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Lines of LRC-style synced lyrics ("[mm:ss.xx] text"); lines without a time or without words are left out.</summary>
    public static IReadOnlyList<LyricLine> ParseSynced(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrEmpty(lrc)) return lines;
        foreach (var raw in lrc.Split('\n'))
        {
            var m = Timed.Match(raw.Trim());
            if (!m.Success) continue;
            var text = m.Groups[3].Value.Trim();
            if (Words(text).Count == 0) continue;
            var seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                          + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            lines.Add(new LyricLine(seconds, text));
        }
        return lines;
    }
}
