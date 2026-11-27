using System.Security.Cryptography;
using RigControl.Radio.Store;

namespace RigControl.Radio.Speech;

/// <summary>A published Whisper model file: where it comes from, its exact size and SHA-256 (pinned in RigControl).</summary>
public sealed record SpeechModelSpec(string FileName, Uri Url, long Size, string Sha256)
{
    /// <summary>Whisper "small", all languages, 5-bit (190 MB): good on a graphics card. whisper.cpp's own model files (MIT).</summary>
    public static readonly SpeechModelSpec SmallQ5 = new("ggml-small-q5_1.bin",
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin"), 190085487,
        "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb");

    /// <summary>Whisper "base", all languages, 5-bit (60 MB): for PCs without a usable graphics card.</summary>
    public static readonly SpeechModelSpec BaseQ5 = new("ggml-base-q5_1.bin",
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin"), 59707625,
        "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898");
}

/// <summary>
/// Downloads a speech model once (checked against its pinned size and SHA-256 before it is kept) and hands it out
/// only as checked bytes in memory, so the file on disk can never be swapped between the check and the use.
/// </summary>
public sealed class SpeechModel(HttpClient http, string folder, SpeechModelSpec spec)
{
    public SpeechModelSpec Spec { get; } = spec;
    private string FilePath => Path.Combine(folder, "models", Spec.FileName);

    /// <summary>The file is there with the right size (the full check happens in <see cref="LoadVerified"/>).</summary>
    public bool IsDownloaded => File.Exists(FilePath) && new FileInfo(FilePath).Length == Spec.Size && !SafeFolder.ReachedThroughLink(FilePath);

    /// <param name="force">Download even when a file of the right size is there (it failed the check).</param>
    public async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct, bool force = false)
    {
        if (!force && IsDownloaded) return;
        using var res = await http.GetAsync(Spec.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength is { } len && len != Spec.Size)
            throw new InvalidDataException("The speech model on the server isn't the one RigControl expects.");
        await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = new byte[Spec.Size];
        var got = 0L;
        var buf = new byte[1 << 16];
        int n;
        while (true)
        {
            // A download that stops sending gives up after a minute instead of waiting forever.
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                n = await s.ReadAsync(buf, idle.Token).ConfigureAwait(false);
            }
            if (n <= 0) break;
            if (got + n > Spec.Size) throw new InvalidDataException("The speech model download is bigger than it should be.");
            Buffer.BlockCopy(buf, 0, bytes, (int)got, n);
            got += n;
            progress?.Report((double)got / Spec.Size);
        }
        if (got != Spec.Size || !Matches(bytes))
            throw new InvalidDataException("The downloaded speech model didn't match the published one, so it wasn't kept.");
        SafeFolder.WriteFile(FilePath, bytes);
    }

    /// <summary>
    /// The model's bytes, read and checked now. A file that no longer matches is refused (not deleted: the elevated app
    /// doesn't delete in a folder others can change); a forced download replaces it.
    /// </summary>
    public byte[] LoadVerified()
    {
        if (!File.Exists(FilePath) || SafeFolder.ReachedThroughLink(FilePath) || new FileInfo(FilePath).Length != Spec.Size)
            throw new InvalidDataException("The speech model isn't downloaded.");
        var bytes = File.ReadAllBytes(FilePath);
        if (bytes.Length == Spec.Size && Matches(bytes)) return bytes;
        throw new InvalidDataException("The kept speech model was changed, so it wasn't used. It will be downloaded again.");
    }

    private bool Matches(byte[] bytes) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), Spec.Sha256, StringComparison.OrdinalIgnoreCase);
}
