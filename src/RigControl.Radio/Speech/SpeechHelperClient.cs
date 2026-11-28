using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using RigControl.Core.Logging;

namespace RigControl.Radio.Speech;

/// <summary>
/// Speech recognition in a separate helper process without administrator rights. RigControl (the admin app) only
/// sends sound and reads back plain text over a private pipe that only this Windows user can open (never from the
/// network). A helper that crashes takes nothing else down: the next request starts a new one, on the processor after
/// a crash on the graphics card, at most three times per run.
/// </summary>
public sealed class SpeechHelperClient : ISpeech
{
    public const int MaxStarts = 3;
    private readonly Func<string, bool, bool> _start;
    private readonly ILog _log;
    private readonly SemaphoreSlim _one = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private int _starts;
    private bool _useCpu;
    private bool _disposed;

    private readonly string? _cpuMarker;

    /// <param name="start">Starts the helper with (pipe name, use the processor); false when it couldn't be started.</param>
    /// <param name="cpuMarker">A file noting that the graphics card crashed the speech engine on this PC: while it
    /// exists, the helper always runs on the processor (also after a restart). Null: not remembered.</param>
    public SpeechHelperClient(Func<string, bool, bool> start, ILog log, string? cpuMarker = null)
    {
        _start = start;
        _log = log;
        _cpuMarker = cpuMarker;
        _useCpu = cpuMarker is not null && File.Exists(cpuMarker);
    }

    /// <summary>The graphics card failed: use the processor from now on, and remember it for next time.</summary>
    private void UseProcessorFromNowOn()
    {
        if (_useCpu) return;
        _useCpu = true;
        if (_cpuMarker is null) return;
        try
        {
            Store.SafeFolder.WriteFile(_cpuMarker, "The graphics card crashed speech recognition on this PC; it runs on the processor. Delete this file to try the graphics card again."u8.ToArray());
            _log.Info("In-game radio: speech recognition will use the processor from now on (the graphics card crashed it).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not remembered, only this run */ }
    }

    /// <summary>How long a new helper may take to connect and load its engine.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How long one transcription may take.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public string Device { get; private set; } = "starting";

    /// <summary>Starts the helper now (so the first listen doesn't wait for it). Throws when it couldn't start.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureAsync(ct).ConfigureAwait(false); }
        finally { _one.Release(); }
    }

    public async Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pipe = await EnsureAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReplyTimeout);
            try
            {
                await SpeechWire.WriteAudioAsync(pipe, audio16k, timeout.Token).ConfigureAwait(false);
                var (type, payload) = await SpeechWire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                if (type == SpeechWire.Text) return SpeechWire.ToText(payload);
                var message = type == SpeechWire.Error ? SpeechWire.ToText(payload) : "the speech helper answered nonsense";
                Lost(message);
                throw new IOException("Listening failed: " + message);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (_pipe is not null) Lost(ex.Message);
                throw new IOException("The speech helper stopped answering (" + ex.Message + ").", ex);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<NamedPipeServerStream> EnsureAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipe is { IsConnected: true } ready) return ready;
        if (_starts >= MaxStarts)
            throw new InvalidOperationException($"Speech recognition stopped working (it failed {MaxStarts} times). Please restart RigControl.");
        _starts++;
        var name = "RigControl-speech-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var pipe = CreatePipe(name);
        try
        {
            if (!_start(name, _useCpu)) throw new IOException("The speech helper couldn't be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            var (type, payload) = await SpeechWire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (type != SpeechWire.Ready)
                throw new IOException(type == SpeechWire.Error ? SpeechWire.ToText(payload) : "The speech helper answered nonsense.");
            Device = SpeechWire.ToText(payload) == "gpu" ? "gpu" : "cpu";
            _pipe = pipe;
            _log.Info($"In-game radio: speech helper started ({(Device == "gpu" ? "graphics card" : "processor")}).");
            return pipe;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            UseProcessorFromNowOn();
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new IOException("The speech helper didn't start in time.", ex);
            throw;
        }
    }

    /// <summary>The helper crashed or failed: drop it; the next start uses the processor.</summary>
    private void Lost(string why)
    {
        _log.Info("In-game radio: the speech helper stopped (" + why + ").");
        try { _pipe?.Dispose(); } catch (Exception) { /* gone */ }
        _pipe = null;
        UseProcessorFromNowOn();
    }

    /// <summary>Only this Windows user may open the pipe, and never over the network.</summary>
    private static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No Windows user.");
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            1 << 16, 1 << 16, security);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _pipe?.Dispose(); } catch (Exception) { /* gone */ }
        _pipe = null; // the helper sees the pipe close and exits
    }
}
