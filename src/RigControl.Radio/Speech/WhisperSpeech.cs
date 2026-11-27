using System.Runtime.InteropServices;
using System.Text;
using RigControl.Core.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace RigControl.Radio.Speech;

/// <summary>Turns 16 kHz mono sound into words. One call at a time.</summary>
public interface ISpeech : IDisposable
{
    /// <summary>"gpu" or "cpu": where the model runs.</summary>
    string Device { get; }

    Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct);
}

/// <summary>
/// Whisper (whisper.cpp through Whisper.net) on the graphics card through Vulkan, else on the processor. The native
/// library is only loaded from RigControl's own unpacked runtime files (never from a search path), and the model only
/// from checked bytes in memory.
/// </summary>
public sealed class WhisperSpeech : ISpeech
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _one = new(1, 1);

    public string Device { get; }

    private WhisperSpeech(WhisperFactory factory, WhisperProcessor processor, string device)
    {
        _factory = factory;
        _processor = processor;
        Device = device;
    }

    private static readonly object LoadGate = new();
    private static string? _chosenLibrary; // Whisper.net loads its native library once per process: choose once

    /// <summary>
    /// Loads the speech engine from <paramref name="model"/> (checked bytes). <paramref name="nativeFolders"/> are the
    /// folders RigControl's own native files are in (admin-only when installed). The graphics card (Vulkan) is used only
    /// when Windows has a Vulkan driver; otherwise the processor build. Throws when no engine could be loaded.
    /// </summary>
    public static WhisperSpeech Load(byte[] model, IEnumerable<string> nativeFolders, ILog log)
    {
        lock (LoadGate)
        {
            if (_chosenLibrary is null)
            {
                var choice = ChooseLibrary(nativeFolders, HasVulkan()) ?? ChooseLibrary(nativeFolders, vulkan: false)
                    ?? throw new InvalidOperationException("This copy of RigControl doesn't include the speech engine.");
                // Whisper.net looks under (folder of LibraryPath)/runtimes/... first; only the chosen build is allowed.
                RuntimeOptions.LibraryPath = choice.LibraryPath;
                RuntimeOptions.RuntimeLibraryOrder = [choice.OnGpu ? RuntimeLibrary.Vulkan : RuntimeLibrary.Cpu];
                _chosenLibrary = choice.LibraryPath;
                _onGpu = choice.OnGpu;
            }
            var factory = WhisperFactory.FromBuffer(model, new WhisperFactoryOptions { UseGpu = _onGpu });
            log.Info("In-game radio: speech recognition runs on the " + (_onGpu ? "graphics card." : "processor."));
            return new WhisperSpeech(factory, _onGpu ? Build(factory) : Build(factory, threads: Math.Clamp(Environment.ProcessorCount / 4, 2, 4)),
                _onGpu ? "gpu" : "cpu");
        }
    }

    private static bool _onGpu;

    /// <summary>
    /// The first of <paramref name="folders"/> holding the wanted build (runtimesulkan\win-x64 or runtimes\win-x64),
    /// as the LibraryPath Whisper.net wants: a file name inside that folder (it only uses the folder part).
    /// </summary>
    internal static (string LibraryPath, bool OnGpu)? ChooseLibrary(IEnumerable<string> folders, bool vulkan)
    {
        foreach (var f in folders.Where(Directory.Exists))
        {
            var dll = vulkan ? Path.Combine(f, "runtimes", "vulkan", "win-x64", "whisper.dll") : Path.Combine(f, "runtimes", "win-x64", "whisper.dll");
            if (File.Exists(dll)) return (Path.Combine(Path.TrimEndingDirectorySeparator(f), "whisper.dll"), vulkan);
        }
        return null;
    }

    /// <summary>Windows' Vulkan loader, by full path from System32 (never a search path), and whether it loads.</summary>
    private static bool HasVulkan()
    {
        var path = Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static WhisperProcessor Build(WhisperFactory factory, int? threads = null)
    {
        var b = factory.CreateBuilder().WithLanguageDetection().WithNoContext().WithSingleSegment();
        if (threads is { } t) b = b.WithThreads(t);
        return b.Build();
    }

    public async Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var text = new StringBuilder();
            await foreach (var segment in _processor.ProcessAsync(audio16k, ct).ConfigureAwait(false))
                text.Append(segment.Text).Append(' ');
            return text.ToString().Trim();
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose()
    {
        // Whisper.net refuses a plain Dispose while a transcription is still being wound down.
        try { _processor.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception) { /* closing anyway */ }
        _factory.Dispose();
    }
}
