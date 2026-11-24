using System.IO;
using AudioController.Core.Abstractions;
using AudioController.Core.Models;

namespace AudioController.App.Services;

/// <summary>Outcome of importing one file.</summary>
public sealed class ImportItemResult
{
    public required string FilePath { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    public bool Success { get; init; }

    /// <summary>The decoder's (or vault's) message on failure — e.g. the 30 s rejection with the actual length.</summary>
    public string? Error { get; init; }

    /// <summary>The new slot on success (not yet added to any board).</summary>
    public SoundSlot? Slot { get; init; }
}

public readonly record struct ImportProgress(int Done, int Total, string CurrentFile);

/// <summary>
/// Decoder + vault + the 30-second rule. Decodes each file to the canonical WAV in memory
/// (FfmpegDecoder enforces the length limit — reject, never trim), encrypts it into the vault and
/// returns a ready <see cref="SoundSlot"/>. Designed to run off the UI thread.
/// </summary>
public sealed class ImportService
{
    /// <summary>Formats from SPEC 4.2 (any FFmpeg-decodable file is accepted; these drive the picker filter and folder drops).</summary>
    public static IReadOnlyList<string> KnownExtensions { get; } = new[]
    {
        ".mp3", ".wav", ".flac", ".ogg", ".oga", ".opus", ".aac", ".m4a", ".wma", ".aiff", ".aif", ".aifc",
        ".ape", ".alac", ".caf", ".amr", ".ac3", ".eac3", ".wv", ".mp2", ".tta", ".mka", ".mkv", ".mp4",
        ".webm", ".weba", ".dts", ".au", ".mpc",
    };

    public static string FileDialogFilter { get; } =
        "Audio files|" + string.Join(";", KnownExtensions.Select(e => "*" + e)) + "|All files (*.*)|*.*";

    private readonly IAudioDecoder _decoder;
    private readonly IVault _vault;

    public ImportService(IAudioDecoder decoder, IVault vault)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    /// <summary>
    /// Expands dropped/picked paths: files are kept as-is (the decoder decides), folders contribute
    /// their files with a known audio extension (recursively).
    /// </summary>
    public static IReadOnlyList<string> ExpandPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            try
            {
                if (Directory.Exists(raw))
                {
                    foreach (var file in Directory.EnumerateFiles(raw, "*", new EnumerationOptions
                             {
                                 RecurseSubdirectories = true,
                                 IgnoreInaccessible = true,
                                 AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                             }))
                    {
                        if (KnownExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && seen.Add(file))
                            result.Add(file);
                    }
                }
                else if (seen.Add(raw))
                {
                    result.Add(raw);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (seen.Add(raw)) result.Add(raw); // reported as a per-file failure below
            }
        }

        return result;
    }

    /// <summary>Imports every file, never stopping at the first failure. Colors cycle from <paramref name="firstColorIndex"/>.</summary>
    public IReadOnlyList<ImportItemResult> ImportFiles(
        IReadOnlyList<string> filePaths,
        int firstColorIndex,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var results = new List<ImportItemResult>(filePaths.Count);
        int colorIndex = firstColorIndex;

        for (int i = 0; i < filePaths.Count; i++)
        {
            var path = filePaths[i];
            progress?.Report(new ImportProgress(i, filePaths.Count, Path.GetFileName(path)));

            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new ImportItemResult { FilePath = path, Success = false, Error = "Import was cancelled." });
                continue;
            }

            var result = ImportFile(path, Palette.At(colorIndex));
            if (result.Success) colorIndex++;
            results.Add(result);
        }

        progress?.Report(new ImportProgress(filePaths.Count, filePaths.Count, ""));
        return results;
    }

    /// <summary>Imports one file. Never throws: every failure becomes an <see cref="ImportItemResult"/> with an error.</summary>
    public ImportItemResult ImportFile(string filePath, string colorHex)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return Fail(filePath, "The file does not exist (it may have been moved or deleted).");

            if (info.Length == 0)
                return Fail(filePath, "The file is empty (0 bytes).");

            DecodeResult decoded;
            try
            {
                decoded = _decoder.Decode(info.FullName);
            }
            catch (Exception ex)
            {
                return Fail(filePath, "Could not decode the file: " + ex.Message);
            }

            if (!decoded.Success || decoded.WavData is null || decoded.WavData.Length == 0)
                return Fail(filePath, decoded.Error ?? "The file is unsupported or corrupt.");

            Guid soundId;
            try
            {
                soundId = _vault.AddSound(decoded.WavData);
            }
            catch (Exception ex)
            {
                return Fail(filePath, "Could not store the sound in the encrypted library: " + ex.Message);
            }

            var name = Path.GetFileNameWithoutExtension(info.Name);
            var slot = new SoundSlot
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Sound" : name.Trim(),
                ColorHex = colorHex,
                Volume = 1.0f,
                PlayMode = PlayMode.OneShot,
                SoundId = soundId,
                DurationSeconds = decoded.DurationSeconds,
                OriginalFileName = info.Name,
                OriginalSizeBytes = info.Length,
            };

            return new ImportItemResult { FilePath = filePath, Success = true, Slot = slot };
        }
        catch (Exception ex)
        {
            return Fail(filePath, ex.Message);
        }
    }

    private static ImportItemResult Fail(string path, string error) =>
        new() { FilePath = path, Success = false, Error = error };
}
