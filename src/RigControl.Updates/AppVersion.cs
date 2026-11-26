using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace RigControl.Updates;

/// <summary>
/// A RigControl version: three numbers, "1.1.2", optionally with a SemVer pre-release part for test builds, "1.1.3-rc.1".
/// A leading "v" (as in a release tag) is allowed. Ordering follows SemVer 2.0: 1.1.3-rc.1 &lt; 1.1.3-rc.2 &lt; 1.1.3.
/// Build metadata ("+abc") is not part of a version here: <see cref="TryParseLoose"/> drops it, <see cref="TryParse"/>
/// refuses it.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<AppVersion>
{
    /// <summary>The longest pre-release part accepted (it ends up in a tag and a file name).</summary>
    public const int MaxPreReleaseLength = 40;

    /// <summary>True for a test build ("1.1.3-rc.1").</summary>
    public bool IsPreRelease => PreRelease is not null;

    /// <summary>The same version without its pre-release part.</summary>
    public AppVersion Core => new(Major, Minor, Patch);

    /// <summary>Strict: "1.2.3" or "1.2.3-rc.1", with an optional leading "v". Nothing else.</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (!ValidPreRelease(pre)) return false;
        }
        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            // Digits only: no signs, spaces or leading "+".
            if (parts[i].Length is 0 or > 6 || !parts[i].All(char.IsAsciiDigit)) return false;
            numbers[i] = int.Parse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new AppVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public static AppVersion Parse(string text)
        => TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a version like 1.1.2 or 1.1.3-rc.1.");

    /// <summary>
    /// The running app's version string: "1.1.2", "1.1.3-rc.1+abcdef" (build metadata dropped) or a longer one whose
    /// first three numbers count ("1.1.2.0").
    /// </summary>
    public static bool TryParseLoose(string? text, [NotNullWhen(true)] out AppVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Split('+')[0];
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
        }
        var parts = s.Split('.');
        if (parts.Length < 3) return false;
        var core = string.Join('.', parts.Take(3));
        if (!TryParse(pre is null ? core : core + "-" + pre, out var v)) return false;
        version = v;
        return true;
    }

    /// <summary>SemVer pre-release: dot-separated identifiers of [0-9A-Za-z-], numeric ones without leading zeros.</summary>
    private static bool ValidPreRelease(string pre)
    {
        if (pre.Length is 0 or > MaxPreReleaseLength) return false;
        foreach (var id in pre.Split('.'))
        {
            if (id.Length == 0) return false;
            if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
            if (id.All(char.IsAsciiDigit) && id.Length > 1 && id[0] == '0') return false;
        }
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release is newer than any of its pre-releases.
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xNum = x[i].All(char.IsAsciiDigit);
            var yNum = y[i].All(char.IsAsciiDigit);
            int c;
            if (xNum && yNum)
            {
                // No leading zeros, so the longer number is the larger one; same length compares digit by digit.
                c = x[i].Length.CompareTo(y[i].Length);
                if (c == 0) c = string.CompareOrdinal(x[i], y[i]);
            }
            else if (xNum) c = -1; // numbers sort before words
            else if (yNum) c = 1;
            else c = string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return Math.Sign(c);
        }
        return x.Length.CompareTo(y.Length);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (PreRelease is null ? "" : "-" + PreRelease);

    /// <summary>The release tag for this version: "v1.1.2", "v1.1.3-rc.1".</summary>
    public string Tag => "v" + ToString();

    /// <summary>The name the setup exe has on a release: "RigControl-Setup-v1.1.2.exe".</summary>
    public string SetupFileName => "RigControl-Setup-v" + ToString() + ".exe";
}
