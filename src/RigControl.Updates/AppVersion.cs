using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace RigControl.Updates;

/// <summary>A RigControl version: exactly three numbers, "1.1.2". A leading "v" (as in a release tag) is allowed.</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            // Digits only: no signs, spaces or leading "+".
            if (parts[i].Length is 0 or > 6 || !parts[i].All(char.IsAsciiDigit)) return false;
            numbers[i] = int.Parse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new AppVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public static AppVersion Parse(string text)
        => TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a version like 1.1.2.");

    /// <summary>The running app's version string ("1.1.2", or a longer one whose first three numbers count).</summary>
    public static bool TryParseLoose(string? text, [NotNullWhen(true)] out AppVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Split('+', '-')[0];
        var parts = core.Split('.');
        if (parts.Length < 3) return false;
        if (!TryParse(string.Join('.', parts.Take(3)), out var v)) return false;
        version = v;
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>The release tag for this version: "v1.1.2".</summary>
    public string Tag => "v" + ToString();

    /// <summary>The name the setup exe has on a release: "RigControl-Setup-v1.1.2.exe".</summary>
    public string SetupFileName => "RigControl-Setup-v" + ToString() + ".exe";
}
