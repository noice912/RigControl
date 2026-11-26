using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Signing;

/// <summary>
/// Builds a bundle's _CodeSignature/CodeResources: the seal over every file in the bundle except the main
/// executable and the signature folder itself. iOS bundles are "shallow" (no Contents/ or Resources/), and
/// codesign's default rules for them have no "nested" rule, so files inside nested bundles (frameworks, app
/// extensions, including their own _CodeSignature/CodeResources) are sealed one by one like any other file.
/// "files" is the old form (SHA-1 only); "files2" carries SHA-1 (hash) and SHA-256 (hash2), matching a
/// signature with both CodeDirectories.
/// </summary>
internal static class CodeResources
{
    private sealed record Rule(string Pattern, bool Omit = false, bool Optional = false, int? Weight = null)
    {
        public Regex Regex { get; } = new(Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        public int Order => Weight ?? 1;

        public object ToPlist()
        {
            if (!Omit && !Optional && Weight == null)
                return true;
            var d = new PlistDict();
            if (Omit) d["omit"] = true;
            if (Optional) d["optional"] = true;
            if (Weight != null) d["weight"] = (double)Weight.Value;
            return d;
        }
    }

    // codesign's defaults for a bundle without a Resources/ folder (every iOS app, extension and framework).
    private static readonly Rule[] Rules1 =
    [
        new("^.*"),
        new("^.*\\.lproj/", Optional: true, Weight: 1000),
        new("^.*\\.lproj/locversion.plist$", Omit: true, Weight: 1100),
        new("^Base\\.lproj/", Weight: 1010),
        new("^version.plist$"),
    ];

    private static readonly Rule[] Rules2 =
    [
        new(".*\\.dSYM($|/)", Weight: 11),
        new("^(.*/)?\\.DS_Store$", Omit: true, Weight: 2000),
        new("^.*"),
        new("^.*\\.lproj/", Optional: true, Weight: 1000),
        new("^.*\\.lproj/locversion.plist$", Omit: true, Weight: 1100),
        new("^Base\\.lproj/", Weight: 1010),
        new("^Info\\.plist$", Omit: true, Weight: 20),
        new("^PkgInfo$", Omit: true, Weight: 20),
        new("^embedded\\.provisionprofile$", Weight: 20),
        new("^version\\.plist$", Weight: 20),
    ];

    // Never sealed: the seal itself and the App Store receipt.
    private static readonly Regex[] Excluded =
    [
        new("^_CodeSignature/", RegexOptions.CultureInvariant),
        new("^CodeResources$", RegexOptions.CultureInvariant),
        new("^_MASReceipt$", RegexOptions.CultureInvariant),
    ];

    /// <summary>Seals <paramref name="bundleDir"/>. <paramref name="mainExecutable"/> is relative to it (or null).</summary>
    public static byte[] Build(string bundleDir, string? mainExecutable, CancellationToken ct = default)
    {
        var files = new PlistDict();
        var files2 = new PlistDict();
        var root = Path.GetFullPath(bundleDir);

        var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        foreach (var rel in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (rel == mainExecutable || Excluded.Any(r => r.IsMatch(rel)))
                continue;
            var rule1 = Match(Rules1, rel);
            var rule2 = Match(Rules2, rel);
            var include1 = rule1 is { Omit: false };
            var include2 = rule2 is { Omit: false };
            if (!include1 && !include2)
                continue;

            var (sha1, sha256) = HashFile(Path.Combine(root, rel));
            if (include1)
                files[rel] = rule1!.Optional ? new PlistDict { ["hash"] = sha1, ["optional"] = true } : sha1;
            if (include2)
            {
                var entry = new PlistDict { ["hash"] = sha1, ["hash2"] = sha256 };
                if (rule2!.Optional)
                    entry["optional"] = true;
                files2[rel] = entry;
            }
        }

        var plist = new PlistDict
        {
            ["files"] = files,
            ["files2"] = files2,
            ["rules"] = RulesDict(Rules1),
            ["rules2"] = RulesDict(Rules2),
        };
        return PlistFile.ToXml(plist);
    }

    /// <summary>The rule that decides a path: the highest weight that matches (ties keep list order).</summary>
    private static Rule? Match(Rule[] rules, string path) =>
        rules.Where(r => r.Regex.IsMatch(path)).OrderByDescending(r => r.Order).FirstOrDefault();

    private static PlistDict RulesDict(Rule[] rules)
    {
        var d = new PlistDict();
        foreach (var r in rules.OrderBy(r => r.Pattern, StringComparer.Ordinal))
            d[r.Pattern] = r.ToPlist();
        return d;
    }

    private static (byte[] Sha1, byte[] Sha256) HashFile(string path)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var buffer = new byte[1 << 16];
        int n;
        while ((n = f.Read(buffer)) > 0)
        {
            sha1.AppendData(buffer, 0, n);
            sha256.AppendData(buffer, 0, n);
        }
        return (sha1.GetHashAndReset(), sha256.GetHashAndReset());
    }
}
