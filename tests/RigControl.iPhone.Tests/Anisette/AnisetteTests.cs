using System.Net;
using System.Text;
using RigControl.iPhone.Anisette;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests.Anisette;

public class AnisetteTests
{
    private sealed class FakeAdi : ICoreAdi
    {
        public bool Provisioned;
        public ulong Routing;
        public List<string> Calls { get; } = [];

        public bool IsMachineProvisioned(long dsId) { Calls.Add($"check {dsId}"); return Provisioned; }
        public (byte[] Mid, byte[] Otp) RequestOtp(long dsId) { Calls.Add("otp"); return ([1, 2, 3], [4, 5]); }
        public ulong GetRouting(long dsId) => Routing;
        public void SetRouting(long dsId, ulong routing) { Calls.Add($"routing {routing}"); Routing = routing; }
        public (byte[] Cpim, uint Session) StartProvisioning(long dsId, byte[] spim)
        {
            Calls.Add("start " + Encoding.ASCII.GetString(spim));
            return ("CPIM"u8.ToArray(), 7);
        }
        public void EndProvisioning(uint session, byte[] ptm, byte[] tk)
        {
            Calls.Add($"end {session} {Encoding.ASCII.GetString(ptm)} {Encoding.ASCII.GetString(tk)}");
            Provisioned = true;
        }
        public void DestroyProvisioningSession(uint session) => Calls.Add($"destroy {session}");
        public (byte[] Mid, byte[] Srm) Synchronize(long dsId, byte[] sim)
        {
            Calls.Add("sync " + Encoding.ASCII.GetString(sim));
            return ("MID"u8.ToArray(), "SRM"u8.ToArray());
        }
        public void EraseProvisioning(long dsId) { Calls.Add("erase"); Provisioned = false; }
    }

    private sealed class FakeApple(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request)) };
        }
    }

    private static string Plist(PlistDict d) => Encoding.UTF8.GetString(PlistFile.ToXml(d));

    private static string Answer(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/grandslam/GsService2/lookup" => Plist(new PlistDict
        {
            ["urls"] = new PlistDict
            {
                ["midStartProvisioning"] = "https://gsa.apple.com/grandslam/MidService/startMachineProvisioning",
                ["midFinishProvisioning"] = "https://gsa.apple.com/grandslam/MidService/finishMachineProvisioning",
                ["midSyncMachine"] = "https://gsa.apple.com/grandslam/MidService/syncMachine",
            },
        }),
        "/grandslam/MidService/startMachineProvisioning" => Plist(new PlistDict
        {
            ["Response"] = new PlistDict { ["spim"] = Convert.ToBase64String("SPIM"u8.ToArray()), ["Status"] = new PlistDict { ["ec"] = 0L } },
        }),
        "/grandslam/MidService/syncMachine" => Plist(new PlistDict
        {
            ["Response"] = new PlistDict { ["X-Apple-I-MD-RINFO"] = "999", ["Status"] = new PlistDict { ["ec"] = 0L } },
        }),
        "/grandslam/MidService/finishMachineProvisioning" => Plist(new PlistDict
        {
            ["Response"] = new PlistDict
            {
                ["ptm"] = Convert.ToBase64String("PTM"u8.ToArray()),
                ["tk"] = Convert.ToBase64String("TK"u8.ToArray()),
                ["X-Apple-I-MD-RINFO"] = "17106176",
                ["Status"] = new PlistDict { ["ec"] = 0L },
            },
        }),
        _ => throw new InvalidOperationException(r.RequestUri.ToString()),
    };

    private static CoreAdiAnisette Make(FakeAdi adi, FakeApple apple) =>
        new(adi, new HttpClient(apple), "AAAAAAAA.BBBBBBBB.CCCCCCCC.DDDDDDDD.EEEEEEEE.FFFFFFFF.00000000", "S-1-5-21-test",
            "10.0.26200/SP0.0.26200; Win11 Pro; x64", () => new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_provisioned_machine_gets_headers_without_asking_Apple()
    {
        var adi = new FakeAdi { Provisioned = true, Routing = 0 };
        var apple = new FakeApple(_ => throw new InvalidOperationException("no network expected"));
        var h = await Make(adi, apple).GetHeadersAsync();
        Assert.Equal(Convert.ToBase64String([4, 5]), h["X-Apple-I-MD"]);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), h["X-Apple-I-MD-M"]);
        Assert.Equal("17106176", h["X-Apple-I-MD-RINFO"]);
        Assert.Equal("2026-11-26T09:00:00Z", h["X-Apple-I-Client-Time"]);
        Assert.Equal(64, h["X-Apple-I-MD-LU"].Length);
        Assert.Equal(CoreAdiAnisette.WindowsITunesClientInfo, h["X-Mme-Client-Info"]);
        Assert.Equal(CoreAdiAnisette.WindowsITunesUserAgent, h["User-Agent"]);
        Assert.Empty(apple.Requests);
    }

    [Fact]
    public async Task An_unprovisioned_machine_is_set_up_with_Apple_first()
    {
        var adi = new FakeAdi { Provisioned = false };
        var apple = new FakeApple(Answer);
        var h = await Make(adi, apple).GetHeadersAsync();
        Assert.Contains("start SPIM", adi.Calls);
        Assert.Contains("end 7 PTM TK", adi.Calls);
        Assert.Contains("routing 17106176", adi.Calls);
        Assert.Contains(Convert.ToBase64String("CPIM"u8.ToArray()), apple.Bodies[2]);
        Assert.Equal(3, apple.Requests.Count);
        Assert.NotNull(h["X-Apple-I-MD"]);
    }

    [Fact]
    public async Task Resync_sends_CoreADIs_answer_to_Apple_and_keeps_the_new_routing()
    {
        var adi = new FakeAdi { Provisioned = true };
        var apple = new FakeApple(Answer);
        await Make(adi, apple).ResyncAsync("SIM"u8.ToArray(), CancellationToken.None);
        Assert.Contains("sync SIM", adi.Calls);
        Assert.Contains("routing 999", adi.Calls);
        Assert.Contains(Convert.ToBase64String("SRM"u8.ToArray()), apple.Bodies[^1]);
    }

    [Fact]
    public async Task Reprovision_erases_then_sets_the_machine_up_again()
    {
        var adi = new FakeAdi { Provisioned = true };
        var apple = new FakeApple(Answer);
        await Make(adi, apple).ReprovisionAsync(CancellationToken.None);
        Assert.Equal("erase", adi.Calls.First(c => c is "erase" or "start SPIM"));
        Assert.Contains("end 7 PTM TK", adi.Calls);
    }

    [Fact]
    public async Task The_anisette_identity_is_kept_and_a_reprovision_replaces_it()
    {
        var file = Path.Combine(Path.GetTempPath(), "rc-identity-" + Guid.NewGuid().ToString("N"), "identity.bin");
        var adi = new FakeAdi { Provisioned = true };
        var apple = new FakeApple(Answer);
        var first = new CoreAdiAnisette(adi, new HttpClient(apple), "HW", "S-1", "x", identityFile: file);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$", first.DeviceId);
        var again = new CoreAdiAnisette(adi, new HttpClient(apple), "HW", "S-1", "x", identityFile: file);
        Assert.Equal(first.DeviceId, again.DeviceId);
        var h = await again.GetHeadersAsync();
        Assert.Equal(again.DeviceId, h["X-Mme-Device-Id"]);
        await again.ReprovisionAsync(CancellationToken.None);
        Assert.NotEqual(first.DeviceId, again.DeviceId);
        Assert.Equal(again.DeviceId, new CoreAdiAnisette(adi, new HttpClient(apple), "HW", "S-1", "x", identityFile: file).DeviceId);
    }

    [Fact]
    public async Task A_server_list_pointing_outside_Apple_is_refused()
    {
        var adi = new FakeAdi { Provisioned = false };
        var apple = new FakeApple(r => Plist(new PlistDict
        {
            ["urls"] = new PlistDict
            {
                ["midStartProvisioning"] = "https://evil.example/start",
                ["midFinishProvisioning"] = "https://gsa.apple.com/finish",
            },
        }));
        await Assert.ThrowsAsync<AnisetteException>(() => Make(adi, apple).GetHeadersAsync());
        Assert.Single(apple.Requests);
    }

    [Fact]
    public void Machine_identifier_format_matches_iTunes()
    {
        Assert.Equal("90015098.00000000.D41D8CD9.147A664A.00000000.00000000.00000000",
            WindowsMachine.Format(["abc"u8.ToArray(), null, [], "abc\0"u8.ToArray(), null, null, null]));
    }

    /// <summary>Runs Apple's real CoreADI from this PC's Apple Devices app. Opt-in: set RIGCONTROL_LIVE_ADI=1.</summary>
    [Fact]
    public void Live_CoreADI_gives_a_one_time_code()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("RIGCONTROL_LIVE_ADI") != "1")
            return;
        var folder = Path.Combine(Path.GetTempPath(), "rigcontrol-adi-test");
        var path = CoreAdiLibrary.Prepare(folder);
        using var adi = CoreAdi.Load(path);
        Assert.True(adi.IsMachineProvisioned(CoreAdiAnisette.MachineDsId));
        var (mid, otp) = adi.RequestOtp(CoreAdiAnisette.MachineDsId);
        Assert.NotEmpty(mid);
        Assert.NotEmpty(otp);
        Assert.Matches(@"^[0-9A-F]{8}(\.[0-9A-F]{8}){6}$", WindowsMachine.DeviceIdentifier());
    }
}
