namespace AudioController.Decode;

/// <summary>
/// Locates the FFmpeg command-line tools used for decoding (SPEC section 9).
/// </summary>
/// <remarks>
/// Search order, first existing file wins:
/// <list type="number">
/// <item><description>RigControl only: the folder FFmpeg was extracted to from the exe, when a host registered it
/// with <see cref="SetEmbeddedDirectoryProvider"/> (see <see cref="FfmpegExtractor"/>). Without a registration
/// (the standalone app, the tests) this step does not exist.</description></item>
/// <item><description><c>&lt;exe dir&gt;\ffmpeg\&lt;tool&gt;</c> (published layout).</description></item>
/// <item><description><c>&lt;exe dir&gt;\&lt;tool&gt;</c>.</description></item>
/// <item><description>DEBUG builds only: <c>tools\ffmpeg\&lt;tool&gt;</c> in
/// <see cref="AppContext.BaseDirectory"/> or a parent directory that also contains
/// <c>AudioController.sln</c> (this project's development tree). The ancestor walk is
/// compiled out of release builds entirely (binary-planting hardening).</description></item>
/// <item><description>Each fully qualified directory on <c>PATH</c>, in order. Relative <c>PATH</c>
/// entries are ignored so the current directory can never supply the executable.</description></item>
/// </list>
/// </remarks>
public static class FfmpegLocator
{
    /// <summary>File name of the FFmpeg executable on this platform (<c>ffmpeg.exe</c> on Windows).</summary>
    public static string FfmpegExeName { get; } = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>File name of the FFprobe executable on this platform (<c>ffprobe.exe</c> on Windows).</summary>
    public static string FfprobeExeName { get; } = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    /// <summary>Finds <c>ffmpeg.exe</c> using the documented search order.</summary>
    /// <returns>The full path of the first match, or <see langword="null"/> when FFmpeg is not found.</returns>
    public static string? Find() => FindTool(FfmpegExeName);

    /// <summary>
    /// Finds <c>ffprobe.exe</c>, preferring the directory of the ffmpeg that <see cref="Find"/>
    /// chose so the pair always comes from one place, then falling back to the same search order.
    /// </summary>
    /// <returns>The full path of the first match, or <see langword="null"/> when FFprobe is not found.</returns>
    public static string? FindProbe()
    {
        string? ffmpeg = Find();
        if (ffmpeg is not null)
        {
            string? dir = Path.GetDirectoryName(ffmpeg);
            if (!string.IsNullOrEmpty(dir))
            {
                string? sibling = TryGetExistingFullPath(Path.Combine(dir, FfprobeExeName));
                if (sibling is not null)
                    return sibling;
            }
        }

        return FindTool(FfprobeExeName);
    }

    private static Lazy<string?>? s_embeddedDirectory;

    /// <summary>
    /// RigControl: registers where the FFmpeg tools embedded in the exe live, tried before every other location.
    /// <paramref name="provider"/> runs once, the first time a tool is looked for (normally it extracts the tools
    /// and returns their folder); returning <see langword="null"/>, a relative path or throwing simply skips this
    /// location, and the regular search order follows. Pass <see langword="null"/> to remove the registration.
    /// </summary>
    public static void SetEmbeddedDirectoryProvider(Func<string?>? provider)
    {
        Lazy<string?>? lazy = provider is null
            ? null
            : new Lazy<string?>(() =>
            {
                try
                {
                    return provider();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Embedded FFmpeg unavailable: " + ex.Message);
                    return null;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        Volatile.Write(ref s_embeddedDirectory, lazy);
    }

    private static string? FindTool(string exeName)
    {
        foreach (string candidate in GetCandidates(exeName))
        {
            string? match = TryGetExistingFullPath(candidate);
            if (match is not null)
                return match;
        }

        return null;
    }

    private static List<string> GetCandidates(string exeName) =>
        GetCandidates(exeName, Volatile.Read(ref s_embeddedDirectory)?.Value);

    /// <summary>The search order for <paramref name="exeName"/>; <paramref name="embeddedDirectory"/> (RigControl's
    /// extracted copy) comes first when it is a fully qualified path.</summary>
    internal static List<string> GetCandidates(string exeName, string? embeddedDirectory)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(embeddedDirectory) && IsFullyQualified(embeddedDirectory))
            AddCombined(candidates, embeddedDirectory, exeName);

        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            AddCombined(candidates, baseDir, "ffmpeg", exeName);
            AddCombined(candidates, baseDir, exeName);

#if DEBUG
            // Development builds only — release builds never walk ancestors, so a planted
            // tools\ffmpeg\ffmpeg.exe outside the install directory can never be chosen.
            // Even in DEBUG, only trust tools\ffmpeg inside OUR development tree: the same
            // directory must also contain AudioController.sln.
            for (DirectoryInfo? dir = TryGetDirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
            {
                if (TryFileExists(Path.Combine(dir.FullName, "AudioController.sln")))
                    AddCombined(candidates, dir.FullName, "tools", "ffmpeg", exeName);
            }
#endif
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVariable))
        {
            foreach (string rawEntry in pathVariable.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string entry = rawEntry.Trim('"');
                if (entry.Length == 0 || !IsFullyQualified(entry))
                    continue;

                AddCombined(candidates, entry, exeName);
            }
        }

        return candidates;
    }

    private static void AddCombined(List<string> candidates, params string[] parts)
    {
        try
        {
            candidates.Add(Path.Combine(parts));
        }
        catch (ArgumentException)
        {
            // A malformed directory (for example an invalid PATH entry) is simply skipped.
        }
    }

    private static bool TryFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static DirectoryInfo? TryGetDirectoryInfo(string path)
    {
        try
        {
            return new DirectoryInfo(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsFullyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? TryGetExistingFullPath(string candidate)
    {
        try
        {
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
