using System.Net;
using System.Security.Cryptography;
using RigControl.Radio.Speech;

namespace RigControl.Radio.Tests;

public class SpeechModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-model-" + Guid.NewGuid().ToString("N"));
    public SpeechModelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private sealed class Server(byte[] body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static readonly byte[] Good = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
    private static SpeechModelSpec Spec(byte[] body) => new("test.bin", new Uri("https://huggingface.co/x/test.bin"), body.Length, Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());

    [Fact]
    public async Task Downloads_checks_and_keeps_the_model_then_loads_it_from_disk()
    {
        var server = new Server(Good);
        var m = new SpeechModel(new HttpClient(server), _dir, Spec(Good));
        Assert.False(m.IsDownloaded);
        await m.DownloadAsync(null, default);
        Assert.True(m.IsDownloaded);
        Assert.Equal(Good, m.LoadVerified());
        await m.DownloadAsync(null, default); // already there: not fetched again
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task A_file_that_isnt_the_published_model_is_refused()
    {
        var bad = Good.ToArray();
        bad[10] ^= 1;
        var m = new SpeechModel(new HttpClient(new Server(bad)), _dir, Spec(Good));
        await Assert.ThrowsAsync<InvalidDataException>(() => m.DownloadAsync(null, default));
        Assert.False(m.IsDownloaded);
        Assert.Empty(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_oversized_download_is_stopped()
    {
        var m = new SpeechModel(new HttpClient(new Server(new byte[Good.Length + 10])), _dir, Spec(Good));
        await Assert.ThrowsAsync<InvalidDataException>(() => m.DownloadAsync(null, default));
    }

    [Fact]
    public async Task A_kept_model_changed_afterwards_is_never_loaded()
    {
        var m = new SpeechModel(new HttpClient(new Server(Good)), _dir, Spec(Good));
        await m.DownloadAsync(null, default);
        var file = Directory.GetFiles(_dir, "test.bin", SearchOption.AllDirectories).Single();
        var changed = File.ReadAllBytes(file);
        changed[0] ^= 1;
        File.WriteAllBytes(file, changed);
        Assert.Throws<InvalidDataException>(() => m.LoadVerified());
        Assert.True(File.Exists(file)); // never deleted by the elevated app: replaced by a fresh download instead
        await m.DownloadAsync(null, default, force: true);
        Assert.Equal(Good, m.LoadVerified());
    }

    [Fact]
    public void The_published_models_are_pinned()
    {
        Assert.Equal("ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", SpeechModelSpec.SmallQ5.Sha256);
        Assert.Equal(190085487, SpeechModelSpec.SmallQ5.Size);
        Assert.Equal("huggingface.co", SpeechModelSpec.SmallQ5.Url.Host);
    }
}
