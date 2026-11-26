using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using RigControl.iPhone.Anisette;
using RigControl.iPhone.Apple;

namespace RigControl.iPhone.Tests.Apple;

internal sealed class FakeAnisette : IAnisetteProvider
{
    public int Calls;

    public Task<IReadOnlyDictionary<string, string>> GetHeadersAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        IReadOnlyDictionary<string, string> headers = new Dictionary<string, string>
        {
            ["X-Apple-I-MD"] = "b3RwLQ==" + Calls,
            ["X-Apple-I-MD-M"] = "bWFjaGluZQ==",
            ["X-Apple-I-MD-RINFO"] = "17106176",
            ["X-Apple-I-MD-LU"] = "TFVJRA==",
            ["X-Mme-Device-Id"] = "00000000-1111-2222-3333-444444444444",
            ["X-Apple-I-SRL-NO"] = "0",
            ["X-Mme-Client-Info"] = "<MacBookPro13,2> <macOS;13.1;22C65> <com.apple.AOSKit/282 (com.apple.dt.Xcode/3594.4.19)>",
            ["X-Apple-I-Client-Time"] = "2026-11-26T08:00:00Z",
            ["X-Apple-I-TimeZone"] = "UTC",
            ["X-Apple-Locale"] = "en_US",
            ["X-Apple-I-Locale"] = "en_US",
        };
        return Task.FromResult(headers);
    }
}

/// <summary>Not real protection: reversible scrambling plus a marker, so tests can see it was used.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    private static readonly byte[] Marker = "FAKEPROT"u8.ToArray();
    public int Protects;

    public byte[] Protect(byte[] plain)
    {
        Protects++;
        return [.. Marker, .. plain.Select(b => (byte)(b ^ 0x5A))];
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        if (protectedBytes.Length < Marker.Length || !protectedBytes.AsSpan(0, Marker.Length).SequenceEqual(Marker))
            throw new CryptographicException("Not protected by this protector.");
        return protectedBytes.Skip(Marker.Length).Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}

internal sealed class FakePrompt(params Func<SecondFactorRequest, SecondFactorAnswer?>[] answers) : ISecondFactorPrompt
{
    private int _next;
    public readonly List<SecondFactorRequest> Requests = [];

    public Task<SecondFactorAnswer?> AskAsync(SecondFactorRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_next >= answers.Length)
            throw new InvalidOperationException("The prompt was asked more often than the test expected.");
        return Task.FromResult(answers[_next++](request));
    }
}

/// <summary>A recorded request: everything that went over the wire.</summary>
internal sealed record Recorded(string Method, Uri Uri, string Headers, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
    public string Header(string name)
    {
        foreach (var line in Headers.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && string.Equals(line[..colon], name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return "";
    }
}

/// <summary>Routes requests to a function and records them.</summary>
internal sealed class RecordingHandler(Func<Recorded, HttpResponseMessage> route) : HttpMessageHandler
{
    public readonly List<Recorded> Requests = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new StringBuilder();
        foreach (var h in request.Headers)
            headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append('\n');
        var body = Array.Empty<byte>();
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
                headers.Append(h.Key).Append(": ").Append(string.Join(",", h.Value)).Append('\n');
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        var recorded = new Recorded(request.Method.Method, request.RequestUri!, headers.ToString(), body);
        Requests.Add(recorded);
        return route(recorded);
    }
}

/// <summary>
/// The server side of Apple's SRP, written separately from the client (other byte conversions, the classic
/// verifier formulas) so the two check each other.
/// </summary>
internal sealed class SrpServerReference
{
    // RFC 5054 appendix A, 2048-bit group, typed in again here on purpose.
    public static readonly BigInteger N = BigInteger.Parse("0" +
        "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4A099ED8193E0757767A13D" +
        "D52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B8" +
        "55F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481" +
        "F1D2B9078717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6" +
        "AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB694B5C803D89F7AE435DE236D525F5475" +
        "9B65E372FCD68EF20FA7111F9E4AFF73", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    public static readonly BigInteger G = 2;

    private readonly string _user;
    private readonly byte[] _salt;
    private readonly BigInteger _v, _b;
    public byte[] B { get; }
    public byte[]? SessionKey { get; private set; }
    public byte[]? Premaster { get; private set; }

    public SrpServerReference(string user, byte[] passwordKey, byte[] salt, byte[]? privateB = null)
    {
        _user = user;
        _salt = salt;
        var x = FromBytes(Sha(Concat(salt, Sha(Concat(Encoding.ASCII.GetBytes(":"), passwordKey)))));
        _v = BigInteger.ModPow(G, x, N);
        _b = FromBytes(privateB ?? RandomNumberGenerator.GetBytes(32));
        var k = FromBytes(Sha(Concat(ToBytes(N, 0), ToBytes(G, 256))));
        B = ToBytes((k * _v + BigInteger.ModPow(G, _b, N)) % N, 0);
    }

    /// <summary>Checks the client's M1; returns M2 when it is right, null otherwise.</summary>
    public byte[]? Verify(byte[] a, byte[] m1)
    {
        var aValue = FromBytes(a);
        if (aValue % N == 0)
            return null;
        var u = FromBytes(Sha(Concat(ToBytes(aValue, 256), ToBytes(FromBytes(B), 256))));
        var s = BigInteger.ModPow(aValue * BigInteger.ModPow(_v, u, N) % N, _b, N);
        Premaster = ToBytes(s, 0);
        SessionKey = Sha(Premaster);
        var hn = Sha(ToBytes(N, 0));
        var hg = Sha(ToBytes(G, 256));
        var xor = hn.Zip(hg, (p, q) => (byte)(p ^ q)).ToArray();
        var expected = Sha(Concat(xor, Sha(Encoding.UTF8.GetBytes(_user)), _salt, a, B, SessionKey));
        if (!expected.SequenceEqual(m1))
            return null;
        return Sha(Concat(a, m1, SessionKey));
    }

    public static byte[] Sha(byte[] data) => SHA256.HashData(data);

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // Little-endian BigInteger constructor with an extra zero byte, the old-fashioned way.
    public static BigInteger FromBytes(byte[] bigEndian) => new(Enumerable.Reverse(bigEndian).Append((byte)0).ToArray());

    /// <summary>Big-endian, left-padded to <paramref name="width"/> bytes (0 = no padding).</summary>
    public static byte[] ToBytes(BigInteger value, int width)
    {
        var little = value.ToByteArray().ToList();
        while (little.Count > 1 && little[^1] == 0)
            little.RemoveAt(little.Count - 1);
        var big = Enumerable.Reverse(little).ToArray();
        return big.Length >= width ? big : new byte[width - big.Length].Concat(big).ToArray();
    }
}
