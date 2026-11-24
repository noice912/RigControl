using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Drivers.Tests;

public class DriverPlanTests
{
    private static IReadOnlyList<AsusFile> RealAsusList()
    {
        // ASUS's real answer for ROG STRIX Z790-A GAMING WIFI II, saved on 2026-11-15.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "asus-z790a-wifi2.json")));
        return DriverPlan.ParseAsus(doc.RootElement);
    }

    // What this PC had installed on 2026-11-15.
    private static readonly InstalledDriver[] ThisPc =
    {
        new("BLUETOOTH", "Intel(R) Wireless Bluetooth(R)", "24.80.0.2", "Intel Corporation"),
        new("MEDIA", "Realtek USB Audio", "6.4.0.2414", "Realtek Semiconductor Corp."),
        new("NET", "Intel(R) Ethernet Controller I226-V", "2.1.5.10", "Intel"),
        new("NET", "Intel(R) Wi-Fi 7 BE202 160MHz", "24.40.0.4", "Intel"),
        new("SYSTEM", "Intel(R) Management Engine Interface #1", "2603.9.4.0", "Intel"),
        new("SYSTEM", "Intel(R) SMBus - 7A23", "10.1.46.5", "INTEL"),
        new("DISPLAY", "NVIDIA GeForce RTX 4070 Ti", "32.0.16.2124", "NVIDIA"),
    };

    [Fact]
    public void The_real_asus_list_parses_and_keeps_only_asus_links()
    {
        var files = RealAsusList();
        Assert.True(files.Count > 40);
        Assert.All(files, f => Assert.True(AsusDownloads.IsAllowedUrl(f.Url)));
        Assert.Contains(files, f => f.Sha256 is { Length: 64 });
    }

    [Fact]
    public void Each_part_is_compared_with_what_is_installed()
    {
        var rows = DriverPlan.Build(RealAsusList(), ThisPc).ToDictionary(r => r.Key);
        // Equal versions.
        Assert.Equal("current", rows["lan"].State);
        Assert.Equal("2.1.5.10", rows["lan"].AvailableVersion);
        Assert.Equal("current", rows["me"].State);
        // This PC already has newer drivers than ASUS offers: never suggest going backwards.
        Assert.Equal("newer", rows["wifi"].State);
        Assert.Equal("24.20.0.4", rows["wifi"].AvailableVersion);
        Assert.Equal("newer", rows["bluetooth"].State);
        Assert.Equal("newer", rows["audio"].State);
        // The chipset package uses its own numbering.
        Assert.Equal("unknown", rows["chipset"].State);
        Assert.Equal("10.1.20532.8842", rows["chipset"].AvailableVersion);
        // No RAID/VMD storage device here, so the storage driver is not listed.
        Assert.False(rows.ContainsKey("rst"));
        Assert.All(rows.Values, r => Assert.NotEqual("update", r.State));
    }

    [Fact]
    public void An_older_installed_driver_is_offered_the_newer_one()
    {
        var old = ThisPc.Select(d => d.DeviceName.Contains("Wi-Fi") ? d with { Version = "23.80.0.7" } : d).ToArray();
        var wifi = DriverPlan.Build(RealAsusList(), old).Single(r => r.Key == "wifi");
        Assert.Equal("update", wifi.State);
        Assert.Equal("24.20.0.4", wifi.AvailableVersion);
        Assert.NotNull(wifi.FileId);
        var missing = DriverPlan.Build(RealAsusList(), ThisPc.Where(d => !d.DeviceName.Contains("Bluetooth")).ToArray()).Single(r => r.Key == "bluetooth");
        Assert.Equal("missing", missing.State);
    }

    [Theory]
    [InlineData("2.1.5.10", "2.1.5.10", 0)]
    [InlineData("24.40.0.4", "24.20.0.4", 1)]
    [InlineData("6.3.9600.2393", "6.4.0.2414", -1)]
    [InlineData("10.1", "10.1.0.0", 0)]
    public void Versions_compare_by_number(string a, string b, int expected) => Assert.Equal(expected, DriverPlan.CompareVersions(a, b));

    [Fact]
    public void Odd_versions_are_not_compared() => Assert.Null(DriverPlan.CompareVersions("V2.1 beta", "2.1"));

    [Theory]
    [InlineData("https://dlcdnets.asus.com/pub/ASUS/mb/04LAN/x.zip", true)]
    [InlineData("http://dlcdnets.asus.com/pub/x.zip", false)]
    [InlineData("https://dlcdnets.asus.com.evil.example/x.zip", false)]
    [InlineData("https://user@dlcdnets.asus.com/x.zip", false)]
    [InlineData("https://example.com/x.zip", false)]
    public void Only_asus_download_servers_are_allowed(string url, bool ok) => Assert.Equal(ok, AsusDownloads.IsAllowedUrl(url));

    [Fact]
    public void The_installer_in_a_package_is_found_or_left_alone_when_unclear()
    {
        var dir = Directory.CreateTempSubdirectory("rigcontrol-pkg-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Driver"));
            File.WriteAllText(Path.Combine(dir, "Driver", "helper.exe"), "");
            File.WriteAllText(Path.Combine(dir, "AsusSetup.exe"), "");
            File.WriteAllText(Path.Combine(dir, "Setup.exe"), "");
            Assert.Equal("AsusSetup.exe", Path.GetFileName(AsusDownloads.FindInstaller(dir)));
            File.Delete(Path.Combine(dir, "AsusSetup.exe"));
            Assert.Equal("Setup.exe", Path.GetFileName(AsusDownloads.FindInstaller(dir)));
            File.Delete(Path.Combine(dir, "Setup.exe"));
            File.WriteAllText(Path.Combine(dir, "a.exe"), "");
            File.WriteAllText(Path.Combine(dir, "b.exe"), "");
            Assert.Null(AsusDownloads.FindInstaller(dir));                // two candidates: do not guess
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

internal sealed class FakeSources : IDriverSources
{
    public string Maker { get; set; } = "ASUSTeK COMPUTER INC.";
    public string Model { get; set; } = "ROG STRIX Z790-A GAMING WIFI II";
    public List<AsusFile> Asus { get; } = new();
    public List<InstalledDriver> Installed { get; } = new();
    public List<WindowsUpdateDriver> Wu { get; } = new();
    public bool AsusFails { get; set; }
    public byte[] PackageBytes { get; set; } = Array.Empty<byte>();
    public string? Signer { get; set; } = "Intel Corporation / CN=Intel Corporation, O=Intel Corporation";
    public List<string> Started { get; } = new();
    public List<string> Downloads { get; } = new();
    public List<IReadOnlyList<string>> WuInstalls { get; } = new();
    public (int, bool, string?) WuResult { get; set; } = (1, true, null);

    public (string? Maker, string? Model) Board() => (Maker, Model);

    public Task<IReadOnlyList<AsusFile>> GetAsusAsync(string model, CancellationToken ct)
        => AsusFails ? throw new HttpRequestException("offline") : Task.FromResult<IReadOnlyList<AsusFile>>(Asus.ToList());

    public IReadOnlyList<InstalledDriver> GetInstalled() => Installed.ToList();
    public Task<IReadOnlyList<WindowsUpdateDriver>> SearchWindowsUpdateAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WindowsUpdateDriver>>(Wu.ToList());

    public Task<(int Installed, bool RestartNeeded, string? Problem)> InstallWindowsUpdateAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        WuInstalls.Add(ids);
        Wu.RemoveAll(u => ids.Contains(u.Id));
        return Task.FromResult(WuResult);
    }

    public Task DownloadAsync(string url, string path, Action<long, long?> progress, CancellationToken ct)
    {
        Downloads.Add(url);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PackageBytes);
        progress(PackageBytes.Length, PackageBytes.Length);
        return Task.CompletedTask;
    }

    public string? TrustedSigner(string path) => Signer;
    public void Start(string path) => Started.Add(path);
}

public class DriversModuleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rigcontrol-drivers-tests-").FullName;
    private readonly FakeSources _src = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    private void OfferWifi(byte[] package, string? sha = "auto")
    {
        _src.Installed.Add(new InstalledDriver("NET", "Intel(R) Wi-Fi 7 BE202 160MHz", "23.80.0.7", "Intel"));
        var hash = sha == "auto" ? Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant() : sha;
        _src.Asus.Add(new AsusFile("wifi-1", "Wireless", "Intel Wi-Fi Driver V24.20.0.4 for Windows10, Windows11 64-bit.", "24.20.0.4",
            new DateOnly(2026, 5, 14), "88.73 MB", "https://dlcdnets.asus.com/pub/ASUS/mb/08WIRELESS/DRV_WiFi_Intel_24.20.0.4.zip", hash));
        _src.PackageBytes = package;
    }

    private async Task<DriversModule> Start(bool elevated = true)
    {
        var m = new DriversModule(_src);
        await m.StartAsync(new ModuleContext(new EventBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, elevated, "1.0.0", "x.exe")), CancellationToken.None);
        return m;
    }

    private static async Task<JsonElement> Call(DriversModule m, string method, object? args = null)
    {
        JsonElement? a = args is null ? null : JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
        return JsonSerializer.SerializeToElement(await m.InvokeAsync(method, a, CancellationToken.None), JsonDefaults.Options);
    }

    private static async Task<string> Code(DriversModule m, string method, object? args = null)
        => (await Assert.ThrowsAsync<RpcException>(() => Call(m, method, args))).Code;

    [Fact]
    public async Task Nothing_is_looked_up_until_checked()
    {
        await using var m = await Start();
        var s = await Call(m, "getStatus");
        Assert.Equal("ROG STRIX Z790-A GAMING WIFI II", s.GetProperty("board").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("checkedAt").ValueKind);
        Assert.Empty(_src.Downloads);
    }

    [Fact]
    public async Task Download_check_unpack_and_start_a_signed_installer()
    {
        OfferWifi(Zip(("AsusSetup.exe", "installer"), ("Driver/netwtw.inf", "inf")));
        _src.Wu.Add(new WindowsUpdateDriver("wu-1", "Realtek - Audio - 6.4.0.2500", "MEDIA", "2026-10-01", 12.5, false));
        await using var m = await Start();
        var s = await Call(m, "check");
        var wifi = s.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "wifi");
        Assert.Equal("update", wifi.GetProperty("state").GetString());
        Assert.Single(s.GetProperty("windowsUpdate").EnumerateArray());

        var r = await Call(m, "installAsus", new { fileId = "wifi-1" });
        Assert.True(r.GetProperty("started").GetBoolean());
        var started = Assert.Single(_src.Started);
        Assert.Equal("AsusSetup.exe", Path.GetFileName(started));
        Assert.StartsWith(Path.Combine(_dir, "drivers", "asus"), started);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("status").GetProperty("busy").ValueKind);

        // A second install of the same package reuses the verified download.
        await Call(m, "installAsus", new { fileId = "wifi-1" });
        Assert.Single(_src.Downloads);
    }

    [Fact]
    public async Task A_package_that_does_not_match_asus_checksum_is_deleted_and_never_run()
    {
        OfferWifi(Zip(("AsusSetup.exe", "installer")), sha: new string('0', 64));
        await using var m = await Start();
        await Call(m, "check");
        Assert.Equal("unsafe", await Code(m, "installAsus", new { fileId = "wifi-1" }));
        Assert.Empty(_src.Started);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "drivers", "asus"), "*.zip"));
    }

    [Fact]
    public async Task An_installer_not_signed_by_asus_intel_or_realtek_is_not_run()
    {
        OfferWifi(Zip(("AsusSetup.exe", "installer")));
        _src.Signer = "Someone Else / CN=Someone Else";
        await using var m = await Start();
        await Call(m, "check");
        Assert.Equal("unsafe", await Code(m, "installAsus", new { fileId = "wifi-1" }));
        _src.Signer = null;                                    // unsigned or not trusted by Windows
        Assert.Equal("unsafe", await Code(m, "installAsus", new { fileId = "wifi-1" }));
        Assert.Empty(_src.Started);
    }

    [Fact]
    public async Task Windows_update_drivers_install_and_the_list_refreshes()
    {
        _src.Wu.Add(new WindowsUpdateDriver("wu-1", "Intel - Bluetooth - 24.90.0.1", "BLUETOOTH", "2026-11-01", 6.1, false));
        await using var m = await Start();
        await Call(m, "check");
        var r = await Call(m, "installWindowsUpdate", new { ids = new[] { "wu-1" } });
        Assert.True(r.GetProperty("restartNeeded").GetBoolean());
        Assert.Equal(new[] { "wu-1" }, _src.WuInstalls.Single());
        Assert.Empty(r.GetProperty("status").GetProperty("windowsUpdate").EnumerateArray());
        Assert.Equal("invalidArgs", await Code(m, "installWindowsUpdate", new { ids = new[] { "not-offered" } }));
    }

    [Fact]
    public async Task Installing_needs_admin_and_a_listed_file()
    {
        OfferWifi(Zip(("AsusSetup.exe", "x")));
        await using var user = await Start(elevated: false);
        await Call(user, "check");
        Assert.Equal("needsAdmin", await Code(user, "installAsus", new { fileId = "wifi-1" }));
        Assert.Equal("needsAdmin", await Code(user, "installWindowsUpdate", new { ids = new[] { "x" } }));
        await using var admin = await Start();
        Assert.Equal("invalidArgs", await Code(admin, "installAsus", new { fileId = "wifi-1" }));   // not checked yet
        Assert.Empty(_src.Downloads);
    }

    [Fact]
    public async Task Asus_being_unreachable_or_another_board_still_gives_a_result()
    {
        _src.AsusFails = true;
        _src.Wu.Add(new WindowsUpdateDriver("wu-1", "Some driver", null, null, 1, false));
        await using var m = await Start();
        var s = await Call(m, "check");
        Assert.Contains("did not answer", s.GetProperty("asusProblem").GetString());
        Assert.Single(s.GetProperty("windowsUpdate").EnumerateArray());

        _src.Maker = "Micro-Star International";
        await using var msi = await Start();
        s = await Call(msi, "check");
        Assert.Contains("not an ASUS board", s.GetProperty("asusProblem").GetString());
    }
}
