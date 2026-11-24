using System.IO;
using System.Text;
using AudioController.Core.Equalizer;

namespace AudioController.Audio;

/// <summary>
/// Writes the app's own Equalizer APO file (<see cref="EqualizerApoConfig.FileName"/>) into Equalizer APO's config folder
/// and keeps exactly one <see cref="EqualizerApoConfig.IncludeLine"/> in its <c>config.txt</c>. Equalizer APO watches
/// the folder and reloads by itself.
/// </summary>
/// <remarks>
/// <para>Writes are atomic (<c>&lt;file&gt;.tmp</c>, then <see cref="File.Move(string, string, bool)"/> with overwrite,
/// retried for up to ~1 s while Equalizer APO is still reading the previous version) and skipped when the file already
/// has that content. <c>config.txt</c> keeps its bytes, encoding and line endings;
/// only the include line is appended when missing (or duplicates of it removed). While the Ambience effect is on the
/// app also writes its impulse response <c>ambience.wav</c> (<see cref="AmbienceImpulse"/>) next to its file. Nothing
/// else in the folder is touched (<c>config.original.txt</c>, <c>example.txt</c>, …).</para>
/// <para><see cref="Schedule"/> debounces (~150 ms) while sliders move; <see cref="Flush"/> writes what is pending.
/// Thread-safe; never throws from the timer (failures are raised through <see cref="WriteFailed"/>).</para>
/// </remarks>
public sealed class EqualizerApoWriter : IEqualizerApoWriter
{
    /// <summary>Debounce for <see cref="Schedule"/>.</summary>
    public const int DebounceMs = 150;

    /// <summary>Replace attempts while Equalizer APO briefly holds the file open (sharing violation).</summary>
    private const int MoveAttempts = 20;

    private const int MoveRetryDelayMs = 50;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly object _sync = new();
    private readonly Timer _timer;
    private PendingWrite? _pending;
    private AmbienceSpec? _lastAmbience;
    private bool _disposed;

    /// <param name="configDirectory">Equalizer APO's config folder (<see cref="EqualizerApoState.ConfigPath"/>).</param>
    public EqualizerApoWriter(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ConfigDirectory = configDirectory;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised (on the writing thread) with a readable message when a debounced write fails.</summary>
    public event EventHandler<string>? WriteFailed;

    /// <summary>Raised (on the writing thread) after a write changed a file.</summary>
    public event EventHandler? Written;

    /// <summary>Equalizer APO's config folder.</summary>
    public string ConfigDirectory { get; }

    /// <summary>The app's own file: <c>&lt;config&gt;\AudioController.txt</c>.</summary>
    public string FilePath => Path.Combine(ConfigDirectory, EqualizerApoConfig.FileName);

    /// <summary>Equalizer APO's main file: <c>&lt;config&gt;\config.txt</c>.</summary>
    public string MainConfigPath => Path.Combine(ConfigDirectory, EqualizerApoConfig.MainConfigFileName);

    /// <summary>The Ambience impulse response: <c>&lt;config&gt;\ambience.wav</c> (written while Ambience is on).</summary>
    public string AmbiencePath => Path.Combine(ConfigDirectory, AmbienceImpulse.FileName);

    /// <summary>True when the app's file exists (the app has written it before).</summary>
    public bool FileExists
    {
        get
        {
            try
            {
                return File.Exists(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Composes the right Equalizer APO file content for the current EQ state and schedules it (~150 ms debounce).
    /// APO attached to the chosen headset AND the user wants PC audio EQ'd there (<paramref name="apoAttachedToHeadset"/>)
    /// means Generate the EQ scoped to the headset's braced GUID; otherwise the neutral text when the app has already
    /// written a file (so stale filters never linger on a device Equalizer APO is no longer being used for), nothing
    /// when it has not (CONTRACTS: "nothing is created just to say nothing"). Never throws.
    /// </summary>
    public void WriteApoState(float[] gainsDb, bool eqEnabled, string? headsetDeviceId, bool apoAttachedToHeadset, bool autoPreamp)
    {
        ArgumentNullException.ThrowIfNull(gainsDb);

        if (apoAttachedToHeadset && EqualizerApoConfig.DeviceSelectorFor(headsetDeviceId) is { } selector)
        {
            Schedule(EqualizerApoConfig.Generate(gainsDb, eqEnabled, selector, autoPreamp), ensureInclude: true);
        }
        else if (FileExists)
        {
            // The app wrote the file before (filters on this or a previous headset): clear it so APO reloads flat.
            Schedule(EqualizerApoConfig.NeutralText, ensureInclude: false);
        }
    }

    /// <summary>
    /// The Effects panel's write: <see cref="EqualizerApoConfig.Generate(FxState, string, bool, int)"/> scoped to the
    /// headset (debounced like <see cref="Schedule"/>), and — while Ambience is on — <c>ambience.wav</c> at the
    /// headset's sample rate, written before the text that references it and regenerated only when the Ambience level
    /// or the rate changes (or the file is gone). Not attached / not wanted: the neutral text when the app wrote the file
    /// before, nothing otherwise. Never throws.
    /// </summary>
    public void WriteFxState(FxState fx, string? headsetDeviceId, bool apoAttachedToHeadset, bool autoPreamp,
        int channels = 2, int sampleRate = AmbienceImpulse.DefaultSampleRate)
    {
        ArgumentNullException.ThrowIfNull(fx);

        if (apoAttachedToHeadset && EqualizerApoConfig.DeviceSelectorFor(headsetDeviceId) is { } selector)
        {
            int rate = sampleRate is >= 8000 and <= 768000 ? sampleRate : AmbienceImpulse.DefaultSampleRate;
            var ambience = fx.AmbienceWet > 0 ? new AmbienceSpec(fx.AmbiencePercent, rate) : null;
            Schedule(new PendingWrite(EqualizerApoConfig.Generate(fx, selector, autoPreamp, channels), true, ambience));
        }
        else if (FileExists)
        {
            Schedule(EqualizerApoConfig.NeutralText, ensureInclude: false);
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to the app's file now (when different) and, when <paramref name="ensureInclude"/>,
    /// makes sure config.txt includes it. Returns null on success, else a readable error. Never throws. Cancels a pending
    /// <see cref="Schedule"/>d write.
    /// </summary>
    public string? WriteNow(string content, bool ensureInclude)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_sync)
        {
            if (_disposed)
            {
                return "the Equalizer APO writer is closed";
            }

            _pending = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return WriteLocked(new PendingWrite(content, ensureInclude, null));
        }
    }

    /// <summary>Queues a write; only the last content queued within ~150 ms is written.</summary>
    public void Schedule(string content, bool ensureInclude)
    {
        ArgumentNullException.ThrowIfNull(content);
        Schedule(new PendingWrite(content, ensureInclude, null));
    }

    private void Schedule(PendingWrite write)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            // Once any call asked for the include it stays asked for until written.
            bool include = write.EnsureInclude || (_pending?.EnsureInclude ?? false);
            _pending = write with { EnsureInclude = include };
            _timer.Change(DebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>Writes a pending <see cref="Schedule"/>d content now (no-op when nothing is pending). Never throws.</summary>
    public void Flush()
    {
        string? error;
        lock (_sync)
        {
            if (_disposed || _pending is not { } pending)
            {
                return;
            }

            _pending = null;
            error = WriteLocked(pending);
        }

        if (error is not null)
        {
            try
            {
                WriteFailed?.Invoke(this, error);
            }
            catch (Exception)
            {
                // A subscriber failure must not escape the timer thread.
            }
        }
    }

    /// <summary>Writes what is pending, then stops. Idempotent.</summary>
    public void Dispose()
    {
        Flush();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically (UTF-8, no BOM): <c>path.tmp</c> first,
    /// then moved over the original. Returns false (writing nothing) when the file already holds exactly that text.
    /// </summary>
    public static bool WriteAtomic(string path, string content) => WriteAtomic(path, Utf8NoBom.GetBytes(content));

    /// <summary>
    /// Makes config.txt (<paramref name="mainConfigPath"/>) include the app's file exactly once, keeping every other
    /// byte (created with just the include line when missing). Returns true when the file changed.
    /// </summary>
    public static bool EnsureIncluded(string mainConfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainConfigPath);
        byte[] original = File.Exists(mainConfigPath) ? ReadWithRetry(mainConfigPath) : Array.Empty<byte>();
        var (text, encoding, preamble) = Decode(original);
        string updated = EqualizerApoConfig.EnsureInclude(text);
        if (ReferenceEquals(updated, text) || updated == text)
        {
            return false;
        }

        byte[] body = encoding.GetBytes(updated);
        byte[] bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return WriteAtomic(mainConfigPath, bytes);
    }

    private string? WriteLocked(PendingWrite write)
    {
        try
        {
            if (!Directory.Exists(ConfigDirectory))
            {
                return $"Equalizer APO's config folder \"{ConfigDirectory}\" is missing (is Equalizer APO still installed?).";
            }

            // The impulse response first: the text that names it must never be read before the file exists.
            bool changed = false;
            if (write.Ambience is { } ambience && (ambience != _lastAmbience || !File.Exists(AmbiencePath)))
            {
                changed |= WriteAtomic(AmbiencePath, AmbienceImpulse.CreateWav(ambience.Percent, ambience.SampleRate));
                _lastAmbience = ambience;
            }

            changed |= WriteAtomic(FilePath, write.Content);
            if (write.EnsureInclude)
            {
                changed |= EnsureIncluded(MainConfigPath);
            }

            if (changed)
            {
                try
                {
                    Written?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                    // Subscriber failures are not write failures.
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return $"Could not write Equalizer APO's config ({ex.Message}).";
        }
    }

    private static bool WriteAtomic(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path) && ReadWithRetry(path).AsSpan().SequenceEqual(bytes))
        {
            return false;
        }

        string tmp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // Equalizer APO (audiodg / Benchmark) reads the file right after every change; while it has it open the
            // replace fails with a sharing violation ("access denied"). That lasts milliseconds: retry briefly.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
                {
                    Thread.Sleep(MoveRetryDelayMs);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stray .tmp is harmless; Equalizer APO ignores files that are not included.
            }
        }
    }

    /// <summary>Reads a file, retrying briefly while Equalizer APO has it open exclusively.</summary>
    private static byte[] ReadWithRetry(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (IOException) when (attempt < MoveAttempts && File.Exists(path))
            {
                Thread.Sleep(MoveRetryDelayMs);
            }
        }
    }

    /// <summary>
    /// Text + how to write it back byte-for-byte: a BOM (UTF-8 / UTF-16) decides the encoding; otherwise strict UTF-8,
    /// or Latin-1 (which round-trips every byte) when the file is not valid UTF-8 (an ANSI file).
    /// </summary>
    private static (string Text, Encoding Encoding, byte[] Preamble) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Utf8NoBom.GetString(bytes, 3, bytes.Length - 3), Utf8NoBom, bytes[..3]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            var le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            return (le.GetString(bytes, 2, bytes.Length - 2), le, bytes[..2]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            var be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
            return (be.GetString(bytes, 2, bytes.Length - 2), be, bytes[..2]);
        }

        try
        {
            return (Utf8NoBom.GetString(bytes), Utf8NoBom, Array.Empty<byte>());
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), Encoding.Latin1, Array.Empty<byte>());
        }
    }

    /// <summary>The Ambience impulse response wanted next to the text (level and the device's sample rate).</summary>
    private sealed record AmbienceSpec(int Percent, int SampleRate);

    /// <summary>One queued write: the text, whether config.txt must include it, and the impulse response it names.</summary>
    private sealed record PendingWrite(string Content, bool EnsureInclude, AmbienceSpec? Ambience);
}
