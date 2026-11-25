namespace RigControl.Desktop;

/// <summary>
/// One RigControl per user: a lock file in the data folder, held open for as long as the app runs.
/// The system lets go of it when the process ends, however it ends, so a crash never leaves RigControl locked out.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string FileName = "running.lock";
    private FileStream? _file;

    private SingleInstance(FileStream file) => _file = file;

    /// <summary>
    /// Takes the lock. Returns null when another RigControl holds it.
    /// When the file cannot be made at all (a read-only folder, say) the error goes to <paramref name="problem"/> and a
    /// lock that holds nothing is returned: better to start than to refuse over a file.
    /// </summary>
    public static SingleInstance? TryTake(string dataDirectory, out string? problem)
    {
        problem = null;
        var path = Path.Combine(dataDirectory, FileName);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new SingleInstance(new FileStream(path, options));
        }
        catch (IOException) when (File.Exists(path))
        {
            // Open somewhere else with no sharing: that is the other RigControl.
            return null;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return new SingleInstance(null!);
        }
    }

    public bool Held => _file is not null;

    public void Dispose()
    {
        var f = Interlocked.Exchange(ref _file, null);
        if (f is null) return;
        try { f.Dispose(); } catch { /* closing */ }
    }
}
