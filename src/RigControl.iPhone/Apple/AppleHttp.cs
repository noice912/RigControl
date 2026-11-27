using System.Net;
using RigControl.iPhone.Anisette;

namespace RigControl.iPhone.Apple;

/// <summary>What the Apple clients share: per-request timeouts, size-capped reads and the anisette headers.</summary>
internal static class AppleHttp
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    // Pretends to be Xcode 11.2 like the reference clients; Apple's servers accept it for free accounts.
    public const string XcodeVersion = "27.0 (27A5218g)";
    public const string XcodeApp = "com.apple.gs.xcode.auth";

    public sealed record Reply(HttpStatusCode Status, byte[] Body, string? ContentType);

    /// <summary>Sends the request with its own timeout and reads at most <see cref="MaxResponseBytes"/>.</summary>
    public static async Task<Reply> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new AppleException("Apple's server sent a reply that is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                    throw new AppleException("Apple's server sent a reply that is too large.");
                buffer.Write(chunk, 0, read);
            }
            return new Reply(response.StatusCode, buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppleException("Apple's server didn't answer in time. Check the internet connection and try again.");
        }
        catch (HttpRequestException e)
        {
            // The reason (DNS, refused, TLS) goes along: it names no secret and says what went wrong.
            var why = e.InnerException?.Message ?? e.Message;
            throw new AppleException("Couldn't reach Apple's server (" + why + "). Check the internet connection and try again.", 0, e);
        }
    }

    public static async Task<IReadOnlyDictionary<string, string>> AnisetteAsync(IAnisetteProvider anisette, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> headers;
        try
        {
            headers = await anisette.GetHeadersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not AppleException)
        {
            throw new AppleException("Couldn't make this PC's Apple device identification.", 0, e);
        }
        foreach (var (k, v) in headers)
        {
            if (string.IsNullOrEmpty(k) || k.Length > 64 || v is null || v.Length > 4096
                || k.Any(c => c <= ' ' || c > '~' || c == ':') || v.Any(c => c < ' ' || c > '~'))
                throw new AppleException("This PC's Apple device identification is malformed.");
        }
        return headers;
    }

    public static string? Find(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var (k, v) in headers)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    /// <summary>X-Mme-Client-Info with its third &lt;...&gt; part set to AuthKit, as Apple's own akd sends it.</summary>
    public static string? AuthKitClientInfo(string? clientInfo)
    {
        if (clientInfo is null)
            return null;
        var parts = new List<(int Start, int End)>();
        var at = 0;
        while (parts.Count < 3)
        {
            var open = clientInfo.IndexOf('<', at);
            if (open < 0) break;
            var close = clientInfo.IndexOf('>', open + 1);
            if (close < 0) break;
            parts.Add((open, close));
            at = close + 1;
        }
        if (parts.Count < 3)
            return clientInfo;
        var (s, e) = parts[2];
        return clientInfo[..(s + 1)] + "com.apple.AuthKit/1 (com.apple.akd/1.0)" + clientInfo[e..];
    }

    public static void AddHeaders(HttpRequestMessage request, IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var (k, v) in headers)
        {
            request.Headers.Remove(k);
            request.Headers.TryAddWithoutValidation(k, v);
        }
    }
}
