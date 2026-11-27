using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Anisette;

/// <summary>
/// Anisette from Apple's own CoreADI, on this PC, with no outside service. Before the first sign-in on a PC that no
/// Apple app has set up yet, the machine is provisioned with Apple (GrandSlam midStartProvisioning /
/// midFinishProvisioning, the same exchange Apple's apps make). After that every request gets a fresh one-time code.
/// </summary>
public sealed class CoreAdiAnisette : IAnisetteProvider, IAnisetteRepair
{
    /// <summary>The account id Apple's apps use for the machine itself.</summary>
    public const long MachineDsId = -2;

    /// <summary>Routing value Apple's servers give a new machine, used when CoreADI has none stored.</summary>
    private const ulong DefaultRouting = 17106176;

    private const string LookupUrl = "https://gsa.apple.com/grandslam/GsService2/lookup";

    private readonly ICoreAdi _adi;
    private readonly HttpClient _http;
    private readonly string _deviceId;
    private readonly string _localUser;
    private readonly string _clientInfo;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _provisioning = new(1, 1);

    /// <param name="deviceId">The machine identifier (<see cref="WindowsMachine.DeviceIdentifier"/> on Windows).</param>
    /// <param name="userKey">Something stable for this Windows user (their SID): the local-user header is a hash of it.</param>
    /// <param name="osDescription">The OS text for X-Mme-Client-Info (<see cref="WindowsMachine.WindowsVersion"/>).</param>
    public CoreAdiAnisette(ICoreAdi adi, HttpClient http, string deviceId, string userKey, string osDescription,
        Func<DateTimeOffset>? now = null)
    {
        _adi = adi;
        _http = http;
        _deviceId = deviceId;
        _localUser = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId + "|" + userKey)));
        _clientInfo = $"<PC> <Windows;{osDescription}> <com.apple.AuthKitWin/1 (com.apple.iCloud/7.21)>";
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string ClientInfo => _clientInfo;

    public async Task<IReadOnlyDictionary<string, string>> GetHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (!_adi.IsMachineProvisioned(MachineDsId))
            await ProvisionAsync(cancellationToken).ConfigureAwait(false);
        var (mid, otp) = _adi.RequestOtp(MachineDsId);
        var routing = _adi.GetRouting(MachineDsId);
        return Build(mid, otp, routing == 0 ? DefaultRouting : routing);
    }

    private Dictionary<string, string> Build(byte[] mid, byte[] otp, ulong routing)
    {
        var tz = TimeZoneInfo.Local;
        var iana = TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var id) ? id : "UTC";
        var locale = CultureInfo.CurrentCulture.Name.Replace('-', '_');
        if (string.IsNullOrEmpty(locale)) locale = "en_US";
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Apple-I-MD"] = Convert.ToBase64String(otp),
            ["X-Apple-I-MD-M"] = Convert.ToBase64String(mid),
            ["X-Apple-I-MD-RINFO"] = routing.ToString(CultureInfo.InvariantCulture),
            ["X-Apple-I-MD-LU"] = _localUser,
            ["X-Apple-I-SRL-NO"] = "0",
            ["X-Mme-Device-Id"] = _deviceId,
            ["X-Mme-Client-Info"] = _clientInfo,
            ["X-Apple-I-Client-Time"] = _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["X-Apple-I-TimeZone"] = iana,
            ["X-Apple-Locale"] = locale,
            ["X-Apple-I-Locale"] = locale,
        };
    }

    /// <summary>Sets this machine up with Apple, once. Safe to call when two requests race.</summary>
    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        await _provisioning.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_adi.IsMachineProvisioned(MachineDsId))
                return;
            var bag = await LookupAsync(cancellationToken).ConfigureAwait(false);
            var startUrl = AppleUrl(bag.String("midStartProvisioning"));
            var finishUrl = AppleUrl(bag.String("midFinishProvisioning"));

            using var startRequest = Request(HttpMethod.Get, startUrl);
            var start = await SendAsync(startRequest, cancellationToken).ConfigureAwait(false);
            var spim = Convert.FromBase64String(start.String("spim")
                ?? throw new AnisetteException("Apple's setup answer was incomplete (no spim)."));

            var (cpim, session) = _adi.StartProvisioning(MachineDsId, spim);
            try
            {
                var body = new PlistDict { ["Header"] = new PlistDict(), ["Request"] = new PlistDict { ["cpim"] = Convert.ToBase64String(cpim) } };
                using var finishRequest = Request(HttpMethod.Post, finishUrl);
                finishRequest.Content = new ByteArrayContent(PlistFile.ToXml(body));
                finishRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                var finish = await SendAsync(finishRequest, cancellationToken).ConfigureAwait(false);
                var ptm = Convert.FromBase64String(finish.String("ptm") ?? throw new AnisetteException("Apple's setup answer was incomplete (no ptm)."));
                var tk = Convert.FromBase64String(finish.String("tk") ?? throw new AnisetteException("Apple's setup answer was incomplete (no tk)."));
                var rinfoText = finish.String("X-Apple-I-MD-RINFO") ?? finish.Long("X-Apple-I-MD-RINFO")?.ToString(CultureInfo.InvariantCulture);
                if (!ulong.TryParse(rinfoText, NumberStyles.None, CultureInfo.InvariantCulture, out var routing))
                    throw new AnisetteException("Apple's setup answer was incomplete (no routing).");
                _adi.EndProvisioning(session, ptm, tk);
                _adi.SetRouting(MachineDsId, routing);
            }
            catch
            {
                try { _adi.DestroyProvisioningSession(session); } catch (AnisetteException) { /* already ended */ }
                throw;
            }
        }
        finally
        {
            _provisioning.Release();
        }
    }

    /// <summary>
    /// Apple's status 434: CoreADI answers Apple's re-sync data with (mid, srm), which go to midSyncMachine; Apple may
    /// send a new routing value back.
    /// </summary>
    public async Task ResyncAsync(byte[] sim, CancellationToken cancellationToken)
    {
        await _provisioning.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bag = await LookupAsync(cancellationToken).ConfigureAwait(false);
            var url = AppleUrl(bag.String("midSyncMachine"));
            var (mid, srm) = _adi.Synchronize(MachineDsId, sim);
            var body = new PlistDict
            {
                ["Header"] = new PlistDict(),
                ["Request"] = new PlistDict
                {
                    ["X-Apple-I-MD-M"] = Convert.ToBase64String(mid),
                    ["srm"] = Convert.ToBase64String(srm),
                },
            };
            using var request = Request(HttpMethod.Post, url);
            request.Content = new ByteArrayContent(PlistFile.ToXml(body));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/x-xml-plist");
            var reply = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var rinfo = reply.String("X-Apple-I-MD-RINFO") ?? reply.Long("X-Apple-I-MD-RINFO")?.ToString(CultureInfo.InvariantCulture);
            if (ulong.TryParse(rinfo, NumberStyles.None, CultureInfo.InvariantCulture, out var routing))
                _adi.SetRouting(MachineDsId, routing);
        }
        finally
        {
            _provisioning.Release();
        }
    }

    /// <summary>Apple's status 433: forget this machine's setup and set it up with Apple again.</summary>
    public async Task ReprovisionAsync(CancellationToken cancellationToken)
    {
        await _provisioning.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _adi.EraseProvisioning(MachineDsId);
        }
        finally
        {
            _provisioning.Release();
        }
        await ProvisionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlistDict> LookupAsync(CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, new Uri(LookupUrl));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var root = PlistFile.ParseDict(bytes);
        return root.Dict("urls") ?? throw new AnisetteException("Apple's server list had no addresses.");
    }

    private HttpRequestMessage Request(HttpMethod method, Uri url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "akd/1.0 CFNetwork/808.1.4");
        request.Headers.TryAddWithoutValidation("X-Mme-Client-Info", _clientInfo);
        request.Headers.TryAddWithoutValidation("X-Mme-Device-Id", _deviceId);
        request.Headers.TryAddWithoutValidation("X-Apple-I-MD-LU", _localUser);
        request.Headers.TryAddWithoutValidation("X-Apple-I-SRL-NO", "0");
        request.Headers.TryAddWithoutValidation("X-Apple-I-Client-Time",
            _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        return request;
    }

    private async Task<PlistDict> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var root = PlistFile.ParseDict(bytes);
        var body = root.Dict("Response") ?? throw new AnisetteException("Apple's setup answer had no Response.");
        if (body.Dict("Status") is { } status && (status.Long("ec") ?? 0) != 0)
            throw new AnisetteException($"Apple refused to set up this PC: {status.String("em") ?? "unknown reason"}",
                (int)(status.Long("ec") ?? 0));
        return body;
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new AnisetteException($"Apple's server answered {(int)response.StatusCode} while setting up this PC.");
        if (response.Content.Headers.ContentLength > 1 << 20)
            throw new AnisetteException("Apple's server sent an unexpectedly large answer.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > 1 << 20)
                throw new AnisetteException("Apple's server sent an unexpectedly large answer.");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    /// <summary>Only https addresses on Apple's own domain are followed from the server list.</summary>
    private static Uri AppleUrl(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !(url.Host == "apple.com" || url.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase)))
            throw new AnisetteException("Apple's server list had an address RigControl won't use.");
        return url;
    }
}
