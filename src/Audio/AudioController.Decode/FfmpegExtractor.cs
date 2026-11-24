using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace AudioController.Decode;

/// <summary>One file carried inside the exe: its plain file name, its expected SHA-256 (64 hex digits) and how to read it.</summary>
public sealed record EmbeddedFile(string FileName, string Sha256Hex, Func<Stream> Open);

/// <summary>
/// RigControl (added for hosting): RigControl is a single exe, so the FFmpeg tools travel inside it as embedded
/// resources and are written to disk the first time FFmpeg is needed, because a child process needs a real file.
/// </summary>
/// <remarks>
/// <para>Layout: <c>&lt;root&gt;\&lt;key&gt;\&lt;file&gt;</c>, normally <c>%LOCALAPPDATA%\RigControl\ffmpeg\&lt;key&gt;\</c>.
/// <c>&lt;key&gt;</c> is the first 16 hex digits of the SHA-256 of the payload manifest (one line
/// <c>name:sha256</c> per file, sorted, lower case), so a new FFmpeg build gets a new folder.</para>
/// <para>Each file is reused when it exists and its SHA-256 matches. Otherwise it is written to a unique
/// <c>.tmp</c> file in the same folder, flushed, hashed again from disk, and only then moved into place
/// (replacing a damaged copy). Any failure returns <see langword="null"/>: callers fall back to the regular
/// FFmpeg search, and finally to "import disabled", exactly as without the embedded copy.</para>
/// <para>After a successful extraction, folders of older payloads (16 hex-digit names) and stale <c>.tmp</c>
/// files under the root are deleted, best effort.</para>
/// </remarks>
public static class FfmpegExtractor
{
    /// <summary>Manifest resource name prefix of the embedded files (<c>ffmpeg/ffmpeg.exe</c>, ...).</summary>
    public const string ResourcePrefix = "ffmpeg/";

    /// <summary>Key prefix of the <see cref="AssemblyMetadataAttribute"/> that carries each file's SHA-256 (written at build time).</summary>
    public const string MetadataPrefix = "ffmpeg-sha256:";

    private const int KeyLength = 16;

    /// <summary><c>%LOCALAPPDATA%\RigControl\ffmpeg</c>.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigControl", "ffmpeg");

    /// <summary>
    /// The files embedded in <paramref name="assembly"/>: every <c>[AssemblyMetadata("ffmpeg-sha256:&lt;name&gt;", "&lt;sha256&gt;")]</c>
    /// whose resource <c>ffmpeg/&lt;name&gt;</c> exists. Never throws; empty when there is nothing usable.
    /// </summary>
    public static IReadOnlyList<EmbeddedFile> FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        try
        {
            return FromManifest(assembly, () => assembly)
                .Where(f => assembly.GetManifestResourceInfo(ResourcePrefix + f.FileName) is not null)
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Embedded FFmpeg manifest unreadable: " + ex.Message);
            return Array.Empty<EmbeddedFile>();
        }
    }

    /// <summary>
    /// The same list, but with the names and hashes read from <paramref name="manifestAssembly"/> (an assembly that is
    /// loaded anyway and carries the same <c>ffmpeg-sha256:</c> metadata) and the bytes read from the assembly
    /// <paramref name="payloadAssembly"/> returns. That function is only called when a file really has to be written,
    /// so the large payload assembly is not loaded while the copy on disk is intact. Never throws; a missing resource
    /// surfaces when the file is opened (extraction then fails and the regular FFmpeg search follows).
    /// </summary>
    public static IReadOnlyList<EmbeddedFile> FromManifest(Assembly manifestAssembly, Func<Assembly> payloadAssembly)
    {
        ArgumentNullException.ThrowIfNull(manifestAssembly);
        ArgumentNullException.ThrowIfNull(payloadAssembly);
        var files = new List<EmbeddedFile>();
        try
        {
            foreach (var attribute in manifestAssembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (!attribute.Key.StartsWith(MetadataPrefix, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(attribute.Value))
                    continue;

                string name = attribute.Key[MetadataPrefix.Length..];
                string resource = ResourcePrefix + name;
                files.Add(new EmbeddedFile(name, attribute.Value, () =>
                    payloadAssembly().GetManifestResourceStream(resource) ?? throw new FileNotFoundException("Embedded resource missing.", resource)));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Embedded FFmpeg manifest unreadable: " + ex.Message);
            return Array.Empty<EmbeddedFile>();
        }

        return files;
    }

    /// <summary>
    /// Makes sure every file in <paramref name="files"/> exists, verified, in <c>&lt;rootDirectory&gt;\&lt;key&gt;\</c>.
    /// </summary>
    /// <returns>That folder, or <see langword="null"/> when there is nothing to extract or anything failed. Never throws.</returns>
    public static string? TryExtract(IReadOnlyList<EmbeddedFile> files, string rootDirectory)
    {
        try
        {
            return Extract(files, rootDirectory);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("FFmpeg extraction failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>The folder name for a payload: first 16 hex digits (lower case) of the SHA-256 of its manifest.</summary>
    public static string ComputeKey(IReadOnlyList<EmbeddedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var manifest = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
            manifest.Append(file.FileName.ToLowerInvariant()).Append(':').Append(file.Sha256Hex.ToLowerInvariant()).Append('\n');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()));
        return Convert.ToHexString(hash)[..KeyLength].ToLowerInvariant();
    }

    private static string? Extract(IReadOnlyList<EmbeddedFile> files, string rootDirectory)
    {
        if (files is null || files.Count == 0 || string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathFullyQualified(rootDirectory))
            return null;

        foreach (var file in files)
        {
            if (!IsPlainFileName(file.FileName) || !IsSha256Hex(file.Sha256Hex))
                return null;
        }

        string key = ComputeKey(files);
        string directory = Path.Combine(rootDirectory, key);
        Directory.CreateDirectory(directory);

        foreach (var file in files)
        {
            if (!EnsureFile(directory, file))
                return null;
        }

        RemoveLeftovers(rootDirectory, directory);
        return directory;
    }

    private static bool EnsureFile(string directory, EmbeddedFile file)
    {
        string target = Path.Combine(directory, file.FileName);
        string expected = file.Sha256Hex.ToLowerInvariant();

        if (File.Exists(target) && string.Equals(HashOf(target), expected, StringComparison.Ordinal))
            return true;

        string temp = Path.Combine(directory, $"{file.FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (Stream source = file.Open())
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                source.CopyTo(output, 1 << 16);
                output.Flush(flushToDisk: true);
            }

            // Hash what actually reached the disk: catches a damaged payload and a bad write alike.
            if (!string.Equals(HashOf(temp), expected, StringComparison.Ordinal))
            {
                Debug.WriteLine($"Embedded {file.FileName} does not match its SHA-256; not used.");
                return false;
            }

            File.Move(temp, target, overwrite: true);
            return true;
        }
        finally
        {
            TryDeleteFile(temp); // nothing left behind on failure; a no-op after the move
        }
    }

    private static string HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Best effort: older payload folders (16 hex-digit names) and stale .tmp files from interrupted runs.</summary>
    private static void RemoveLeftovers(string rootDirectory, string currentDirectory)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(rootDirectory))
            {
                string name = Path.GetFileName(dir);
                if (name.Length != KeyLength || !name.All(char.IsAsciiHexDigitLower)) continue;
                if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(currentDirectory), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Old FFmpeg folder {name} not removed: {ex.Message}");
                }
            }

            foreach (string temp in Directory.EnumerateFiles(currentDirectory, "*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(temp) < DateTime.UtcNow.AddMinutes(-10))
                    TryDeleteFile(temp);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("FFmpeg folder clean-up skipped: " + ex.Message);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort (another process may hold it).
        }
    }

    private static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);

    private static bool IsSha256Hex(string? hex) => hex is { Length: 64 } && hex.All(char.IsAsciiHexDigit);
}
