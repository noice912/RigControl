using System.IO.Compression;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Signing;

/// <summary>
/// Lists the bundles in an .ipa that need their own provisioning profile: the app (Payload/X.app) first, then its
/// app extensions (X.app/PlugIns/*.appex and X.app/Extensions/*.appex). Frameworks are signed too but need no
/// profile, so they are not listed.
/// </summary>
public static class IpaInspector
{
    private const int MaxInfoPlistSize = 1 << 20;

    public static IReadOnlyList<BundleEntry> ListBundles(string ipaPath)
    {
        using var zip = ZipFile.OpenRead(ipaPath);
        if (zip.Entries.Count > IpaArchive.MaxEntries)
            throw new InvalidDataException("The .ipa has too many files.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = IpaArchive.CheckEntryName(entry);
            if (name != null && !entry.FullName.EndsWith('/'))
                entries.TryAdd(name, entry);
        }
        return List(entries.Keys, path =>
        {
            if (!entries.TryGetValue(path, out var e))
                return null;
            if (e.Length > MaxInfoPlistSize)
                throw new InvalidDataException($"{path} is too large.");
            using var s = e.Open();
            using var ms = new MemoryStream();
            var buffer = new byte[1 << 16];
            int n;
            while ((n = s.Read(buffer)) > 0)
            {
                if (ms.Length + n > MaxInfoPlistSize)
                    throw new InvalidDataException($"{path} is too large.");
                ms.Write(buffer, 0, n);
            }
            return ms.ToArray();
        });
    }

    /// <summary>The same listing over an unpacked .ipa (the folder that holds Payload/).</summary>
    internal static IReadOnlyList<BundleEntry> ListBundlesInFolder(string root)
    {
        var full = Path.GetFullPath(root);
        var files = Directory.EnumerateFiles(Path.Combine(full, "Payload"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(full, f).Replace('\\', '/'));
        return List(files, path =>
        {
            var file = Path.Combine(full, path);
            if (!File.Exists(file))
                return null;
            if (new FileInfo(file).Length > MaxInfoPlistSize)
                throw new InvalidDataException($"{path} is too large.");
            return File.ReadAllBytes(file);
        });
    }

    private static IReadOnlyList<BundleEntry> List(IEnumerable<string> files, Func<string, byte[]?> read)
    {
        var paths = files.ToList();
        var apps = paths
            .Select(p => p.Split('/'))
            .Where(parts => parts.Length >= 3 && parts[1].EndsWith(".app", StringComparison.Ordinal))
            .Select(parts => $"{parts[0]}/{parts[1]}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (apps.Count != 1)
            throw new InvalidDataException(apps.Count == 0
                ? "The .ipa has no app inside Payload/."
                : "The .ipa has more than one app inside Payload/.");
        var app = apps[0];

        var result = new List<BundleEntry> { Read(app, isExtension: false, read) };
        var extensions = paths
            .Select(p => p.Split('/'))
            .Where(parts => parts.Length >= 5 && $"{parts[0]}/{parts[1]}" == app
                && parts[2] is "PlugIns" or "Extensions" && parts[3].EndsWith(".appex", StringComparison.Ordinal))
            .Select(parts => string.Join('/', parts.Take(4)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal);
        foreach (var ext in extensions)
            result.Add(Read(ext, isExtension: true, read));
        return result;
    }

    private static BundleEntry Read(string bundlePath, bool isExtension, Func<string, byte[]?> read)
    {
        var bytes = read(bundlePath + "/Info.plist")
            ?? throw new InvalidDataException($"{bundlePath} has no Info.plist.");
        PlistDict info;
        try
        {
            info = PlistFile.ParseDict(bytes);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidDataException($"{bundlePath}/Info.plist can't be read.", e);
        }
        var id = info.String("CFBundleIdentifier");
        var exe = info.String("CFBundleExecutable");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(exe) || !IsSafeName(exe))
            throw new InvalidDataException($"{bundlePath}/Info.plist lacks a valid CFBundleIdentifier or CFBundleExecutable.");
        return new BundleEntry(bundlePath, id, exe, isExtension);
    }

    /// <summary>A single file name: no separators, not "." or "..".</summary>
    internal static bool IsSafeName(string name) =>
        name.Length is > 0 and < 256 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', ':', '\0']) < 0;
}
