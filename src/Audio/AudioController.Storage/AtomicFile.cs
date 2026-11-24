namespace AudioController.Storage;

/// <summary>
/// Crash-safe file replacement: data is fully written and flushed to <c>&lt;path&gt;.tmp</c>
/// first, then moved over the target in a single rename.
/// </summary>
/// <remarks>
/// If the process stops part-way through a write, the original file is left untouched and only
/// the stale <c>.tmp</c> remains, which the next write to the same path removes. Callers are
/// responsible for passing already-encrypted bytes; this class never transforms data.
/// </remarks>
public static class AtomicFile
{
    /// <summary>Suffix appended to the target path for the in-progress file.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// Atomically replaces (or creates) <paramref name="path"/> with <paramref name="data"/>.
    /// </summary>
    /// <param name="path">The destination file. Its directory is created if missing.</param>
    /// <param name="data">The complete file contents.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
    /// <exception cref="IOException">The file could not be written or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file or directory was denied.</exception>
    public static void Write(string path, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = fullPath + TempSuffix;

        // A leftover .tmp means an earlier write was interrupted. Remove it if we can;
        // if it is locked, FileMode.Create below reports the real problem.
        TryDelete(tempPath);

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.None))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }
}
