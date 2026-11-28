using System.IO.Pipes;
using System.Text.RegularExpressions;
using RigControl.Core.Logging;

namespace RigControl.Radio.Speech;

/// <summary>
/// The speech helper's side ("RigControl.exe --speech-helper &lt;pipe&gt; gpu|cpu", started WITHOUT administrator rights):
/// connects to RigControl's private pipe, loads the speech engine, then turns each piece of sound it is sent into words.
/// After a failed transcription it says so and exits (the engine may be broken); RigControl starts a new helper.
/// </summary>
public static class SpeechHelperHost
{
    public const string Switch = "--speech-helper";
    private static readonly Regex PipeName = new(@"\ARigControl-speech-[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

    public static bool Wants(string[] args) => args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Exit codes: 0 RigControl closed the pipe, 1 bad arguments, 2 the engine couldn't start, 3 a transcription failed.</summary>
    public static async Task<int> RunAsync(string pipe, bool cpu, Func<bool, ISpeech> load, ILog log, CancellationToken ct)
    {
        if (!PipeName.IsMatch(pipe)) return 1;
        await using var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await stream.ConnectAsync(30_000, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            log.Info("Speech helper: RigControl's pipe wasn't there.");
            return 1;
        }
        ISpeech speech;
        try { speech = load(cpu); }
        catch (Exception ex)
        {
            log.Warn("Speech helper: the speech engine couldn't start: " + ex.Message);
            await SpeechWire.WriteTextAsync(stream, SpeechWire.Error, "Speech recognition couldn't start: " + ex.Message, ct).ConfigureAwait(false);
            return 2;
        }
        await SpeechWire.WriteTextAsync(stream, SpeechWire.Ready, speech.Device, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            (byte Type, byte[] Payload) frame;
            try { frame = await SpeechWire.ReadAsync(stream, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException) { return 0; }
            if (frame.Type != SpeechWire.Audio) return 0;
            string text;
            try { text = await speech.TranscribeAsync(SpeechWire.ToSamples(frame.Payload), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warn("Speech helper: listening failed: " + ex.Message);
                try { await SpeechWire.WriteTextAsync(stream, SpeechWire.Error, ex.Message, ct).ConfigureAwait(false); }
                catch (IOException) { /* RigControl went away */ }
                return 3;
            }
            await SpeechWire.WriteTextAsync(stream, SpeechWire.Text, text, ct).ConfigureAwait(false);
        }
        return 0;
    }
}
