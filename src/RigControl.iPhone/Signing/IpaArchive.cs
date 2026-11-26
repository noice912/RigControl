using System.Buffers.Binary;
using System.IO.Compression;

namespace RigControl.iPhone.Signing;

/// <summary>
/// Unpacks and repacks .ipa files (zip archives with a Payload/ folder). The input is treated as hostile: entry
/// names must be plain relative paths under Payload/ (no "..", no absolute or drive paths, no backslashes), no
/// symbolic links, no two names that differ only by case (Windows would merge them), and entry count and sizes
/// are capped, counting the bytes actually inflated rather than trusting the header.
/// </summary>
internal static class IpaArchive
{
    public const int MaxEntries = 50_000;
    public const long MaxFileSize = 1L << 30;
    public const long MaxTotalSize = 2L << 30;
    public const string PayloadPrefix = "Payload/";

    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymlink = 0xA000;
    private const int UnixDirectory = 0x4000;

    /// <summary>Checks an entry name and returns it without a trailing slash, or null for entries outside Payload/.</summary>
    internal static string? CheckEntryName(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        if (name.Length == 0 || name.Length > 1024 || name.Contains('\\') || name.Contains('\0') || name.Contains(':')
            || name.StartsWith('/'))
            throw new InvalidDataException($"The .ipa has an unsafe entry name: {Printable(name)}");
        var trimmed = name.TrimEnd('/');
        foreach (var part in trimmed.Split('/'))
            // Windows drops trailing dots and spaces, which could make two names land on one file.
            if (part is "" or "." or ".." || part.Any(char.IsControl) || part.EndsWith('.') || part.EndsWith(' '))
                throw new InvalidDataException($"The .ipa has an unsafe entry name: {Printable(name)}");
        var mode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (mode == UnixSymlink)
            throw new InvalidDataException($"The .ipa contains a symbolic link ({Printable(name)}), which is not allowed.");
        return name.StartsWith(PayloadPrefix, StringComparison.Ordinal) ? trimmed : null;
    }

    private static string Printable(string s) =>
        new(s.Take(200).Select(c => char.IsControl(c) ? '?' : c).ToArray());

    /// <summary>
    /// Unpacks the Payload/ part of <paramref name="ipaPath"/> into <paramref name="destination"/> (which must be
    /// empty). Returns the Unix permission bits each file had, keyed by its path relative to the destination.
    /// </summary>
    public static Dictionary<string, int> Extract(string ipaPath, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var zip = ZipFile.OpenRead(ipaPath);
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidDataException("The .ipa has too many files.");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = CheckEntryName(entry);
            if (name == null)
                continue;   // iTunesMetadata.plist, SwiftSupport/, Symbols/: not part of the app
            if (!seen.Add(name))
            {
                // A directory entry next to files inside it is normal; a second file with the same name is not.
                if (entry.FullName.EndsWith('/'))
                    continue;
                throw new InvalidDataException($"The .ipa has two entries named {Printable(name)} (ignoring case).");
            }
            var full = Path.GetFullPath(Path.Combine(root, name));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The .ipa entry {Printable(name)} points outside the folder.");

            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if (entry.FullName.EndsWith('/') || (unixMode & UnixFileTypeMask) == UnixDirectory)
            {
                Directory.CreateDirectory(full);
                continue;
            }
            if (entry.Length > MaxFileSize)
                throw new InvalidDataException($"The .ipa entry {Printable(name)} is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var input = entry.Open())
            using (var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[1 << 16];
                long written = 0;
                int n;
                while ((n = input.Read(buffer)) > 0)
                {
                    written += n;
                    total += n;
                    if (written > MaxFileSize || written > entry.Length || total > MaxTotalSize)
                        throw new InvalidDataException($"The .ipa entry {Printable(name)} inflates past its size or the limit.");
                    output.Write(buffer, 0, n);
                }
            }
            modes[name] = unixMode & 0xFFF;
        }
        return modes;
    }

    /// <summary>
    /// Packs everything in <paramref name="sourceDir"/> (which holds Payload/) into a new zip. Executables keep
    /// mode 0755 (Mach-O files always get it), everything else 0644, and the entries are marked as made on Unix so
    /// the iPhone honours those modes.
    /// </summary>
    public static void Pack(string sourceDir, string outputPath, IReadOnlyDictionary<string, int> originalModes,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(sourceDir);
        using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/'))
                .OrderBy(d => d, StringComparer.Ordinal);
            foreach (var dir in dirs)
            {
                var e = zip.CreateEntry(dir + "/");
                e.ExternalAttributes = ((UnixDirectory | 0x1ED) << 16) | 0x10;   // drwxr-xr-x + MS-DOS directory bit
            }
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal);
            foreach (var rel in files)
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.Combine(root, rel);
                var executable = (originalModes.TryGetValue(rel, out var m) && (m & 0x49) != 0)
                    || MachOImage.FileLooksLikeMachO(full);
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                entry.ExternalAttributes = (0x8000 | (executable ? 0x1ED : 0x1A4)) << 16;   // -rwxr-xr-x or -rw-r--r--
                using var input = File.OpenRead(full);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        MarkMadeOnUnix(outputPath);
    }

    /// <summary>
    /// .NET writes "made by MS-DOS" in each central directory record on Windows, and unzip tools then ignore the
    /// Unix modes in the external attributes. This flips the "made by" system to Unix (3) in place.
    /// </summary>
    internal static void MarkMadeOnUnix(string zipPath)
    {
        using var f = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite);
        var tailLength = (int)Math.Min(f.Length, 22 + 65535);
        var tail = new byte[tailLength];
        f.Seek(-tailLength, SeekOrigin.End);
        f.ReadExactly(tail);
        var eocd = -1;
        for (var i = tailLength - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50)
            {
                eocd = i;
                break;
            }
        if (eocd < 0)
            throw new InvalidDataException("The written zip has no end record.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        var cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        var cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        if (count == 0xFFFF || cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF)
            throw new InvalidDataException("The signed .ipa is too large (ZIP64 is not supported).");

        var cd = new byte[cdSize];
        f.Seek(cdOffset, SeekOrigin.Begin);
        f.ReadExactly(cd);
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            if (at + 46 > cd.Length || BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(at)) != 0x02014b50)
                throw new InvalidDataException("The written zip's directory is damaged.");
            cd[at + 5] = 3;
            at += 46 + BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(at + 28))
                + BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(at + 30))
                + BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(at + 32));
        }
        f.Seek(cdOffset, SeekOrigin.Begin);
        f.Write(cd);
    }
}
