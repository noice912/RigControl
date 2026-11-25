using System.IO;
using System.Reflection;
using System.Text;

namespace RigControl.App.Install;

/// <summary>One licence or notice text carried inside the exe. Name is its path under licenses\ ("LICENSES/MIT.txt").</summary>
internal sealed record LicenceText(string Name, string Text);

/// <summary>
/// The licence texts, notices and terms carried inside the exe (the repository's LICENSE, LICENSES\*,
/// THIRD-PARTY-NOTICES.md, TERMS-OF-USE.md, PRIVACY.md, SECURITY.md and README.md; see the App project file). The
/// installer writes them to &lt;install&gt;\licenses and Settings shows them.
/// </summary>
internal static class LicenceFiles
{
    /// <summary>The resource name prefix the project file gives them.</summary>
    public const string Prefix = "licenses/";

    public const string Terms = "TERMS-OF-USE.md";

    /// <summary>
    /// The path under licenses\ for a resource name, with "/" between folders, or null when it is not one of these
    /// texts or would point outside the folder.
    /// </summary>
    public static string? RelativeName(string resourceName)
    {
        if (!resourceName.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = resourceName[Prefix.Length..].Replace('\\', '/');
        var parts = rest.Split('/');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return null;
        return string.Join('/', parts);
    }

    /// <summary>
    /// The order they are listed in: the terms first, then RigControl's own licence, the notices, and the rest by name.
    /// </summary>
    public static int Rank(string name) => name switch
    {
        Terms => 0,
        "LICENSE" => 1,
        "THIRD-PARTY-NOTICES.md" => 2,
        "PRIVACY.md" => 3,
        "SECURITY.md" => 4,
        "README.md" => 5,
        _ => 10,
    };

    public static IReadOnlyList<string> Names(Assembly? assembly = null)
    {
        assembly ??= typeof(LicenceFiles).Assembly;
        return assembly.GetManifestResourceNames()
            .Select(RelativeName)
            .OfType<string>()
            .OrderBy(Rank)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Stream? Open(string name, Assembly? assembly = null)
        => (assembly ?? typeof(LicenceFiles).Assembly).GetManifestResourceStream(Prefix + name);

    public static string? Read(string name)
    {
        using var s = Open(name);
        if (s is null) return null;
        using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<LicenceText> All() =>
        Names().Select(n => new LicenceText(n, Read(n) ?? string.Empty)).ToList();

    /// <summary>Writes every text into this folder (sub-folders as in the repository). Returns how many were written.</summary>
    public static int WriteTo(string folder)
    {
        var count = 0;
        foreach (var name in Names())
        {
            var target = Path.GetFullPath(Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = Open(name) ?? throw new FileNotFoundException("A licence text is missing from the program.", name);
            var temp = target + ".new";
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                source.CopyTo(output);
            File.Move(temp, target, overwrite: true);
            count++;
        }
        return count;
    }
}
