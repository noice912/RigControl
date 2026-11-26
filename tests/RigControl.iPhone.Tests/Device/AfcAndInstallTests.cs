using System.Buffers.Binary;
using RigControl.iPhone.Device;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests.Device;

public class AfcAndInstallTests
{
    private static async Task<AfcClient> OpenAfcAsync(FakeUsbMux mux) =>
        new(await mux.Client().ConnectAsync(7, FakeDevice.AfcPort)) { ChunkSize = 64 * 1024 };

    [Fact]
    public void Afc_packets_carry_magic_lengths_number_and_operation()
    {
        var packet = AfcClient.BuildPacket(5, AfcClient.Op.FileWrite, [1, 2, 3, 4, 5, 6, 7, 8], [9, 9, 9]);
        Assert.Equal("CFA6LPAA"u8.ToArray(), packet[..8]);
        Assert.Equal(51UL, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(8)));
        Assert.Equal(48UL, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(16)));
        Assert.Equal(5UL, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(24)));
        Assert.Equal(0x10UL, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(32)));
    }

    [Fact]
    public async Task Afc_refuses_a_packet_without_the_magic_or_with_a_huge_length()
    {
        var bad = AfcClient.BuildPacket(0, AfcClient.Op.Status, new byte[8], []);
        bad[0] = (byte)'X';
        var e = await Assert.ThrowsAsync<DeviceException>(() => AfcClient.ReadPacketAsync(new MemoryStream(bad), TimeSpan.FromSeconds(1), default));
        Assert.Equal(DeviceErrors.BadReply, e.Message);

        var huge = AfcClient.BuildPacket(0, AfcClient.Op.Status, new byte[8], []);
        BinaryPrimitives.WriteUInt64LittleEndian(huge.AsSpan(8), 1UL << 40);
        await Assert.ThrowsAsync<DeviceException>(() => AfcClient.ReadPacketAsync(new MemoryStream(huge), TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task Upload_sends_the_file_in_chunks_with_progress()
    {
        await using var mux = new FakeUsbMux();
        var device = new FakeDevice();
        mux.Devices.Add(device);
        var data = new byte[150 * 1024];
        new Random(1).NextBytes(data);
        var progress = new List<long>();

        await using (var afc = await OpenAfcAsync(mux))
        {
            await afc.MakeDirectoryAsync("PublicStaging");
            await afc.UploadAsync(new MemoryStream(data), "PublicStaging/app.ipa", new Inline<long>(progress.Add));
            var info = await afc.GetFileInfoAsync("PublicStaging/app.ipa");
            Assert.Equal(data.Length.ToString(), info!["st_size"]);
            Assert.Null(await afc.GetFileInfoAsync("PublicStaging/none.ipa"));
            Assert.False(await afc.RemovePathAsync("PublicStaging/none.ipa"));
        }

        Assert.Equal(data, device.Afc.Files["PublicStaging/app.ipa"]);
        Assert.Equal([65536, 65536, 22528], device.Afc.WriteSizes);
        Assert.Equal([65536L, 131072L, 153600L], progress);
    }

    [Fact]
    public async Task A_full_iPhone_is_reported_plainly()
    {
        await using var mux = new FakeUsbMux();
        var device = new FakeDevice();
        device.Afc.NoSpaceAfter = 100_000;
        mux.Devices.Add(device);

        await using var afc = await OpenAfcAsync(mux);
        var e = await Assert.ThrowsAsync<DeviceException>(() => afc.UploadAsync(new MemoryStream(new byte[200_000]), "PublicStaging/a.ipa"));
        Assert.Equal(DeviceErrors.OutOfSpace, e.Message);
        Assert.Equal(AfcClient.NoSpaceLeft, AfcError.Of(e));
    }

    [Fact]
    public async Task Install_uploads_installs_reports_progress_and_removes_the_staged_copy()
    {
        await using var mux = new FakeUsbMux();
        var device = new FakeDevice();
        mux.Devices.Add(device);
        var ipa = Path.GetTempFileName();
        try
        {
            var bytes = new byte[100_000];
            new Random(2).NextBytes(bytes);
            await File.WriteAllBytesAsync(ipa, bytes);
            var progress = new List<InstallProgress>();

            await new DeviceService(mux.Client()).InstallAsync(device.Udid, ipa, new Inline<InstallProgress>(progress.Add));

            Assert.Equal(bytes, device.Proxy.InstalledPackage);
            var path = device.Proxy.LastRequest!.String("PackagePath")!;
            Assert.StartsWith("PublicStaging/", path);
            Assert.EndsWith(".ipa", path);
            Assert.Contains(path, device.Afc.Removed);
            Assert.Empty(device.Afc.Files);
            Assert.Equal(("Connecting", 0), (progress[0].Stage, progress[0].Percent));
            Assert.Equal(("Done", 100), (progress[^1].Stage, progress[^1].Percent));
            Assert.Contains(progress, p => p.Stage == "Copying to iPhone" && p.Percent == 50);
            Assert.Contains(progress, p => p.Stage.StartsWith("Installing") && p.Percent == 80);
            for (var i = 1; i < progress.Count; i++)
                Assert.True(progress[i].Percent >= progress[i - 1].Percent);
        }
        finally
        {
            File.Delete(ipa);
        }
    }

    [Fact]
    public async Task A_signing_failure_from_installd_is_explained_and_the_copy_is_removed()
    {
        await using var mux = new FakeUsbMux();
        var device = new FakeDevice();
        device.Proxy.Error = new PlistDict
        {
            ["Error"] = "ApplicationVerificationFailed",
            ["ErrorDescription"] = "Failed to verify code signature of /private/var/installd/x.app : 0xe8008015 (A valid provisioning profile for this executable was not found.)",
            ["ErrorDetail"] = -402620395L,
        };
        mux.Devices.Add(device);
        var ipa = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(ipa, new byte[10]);
            var e = await Assert.ThrowsAsync<DeviceException>(() => new DeviceService(mux.Client()).InstallAsync(device.Udid, ipa));
            Assert.Contains("provisioning profile", e.Message);
            Assert.Empty(device.Afc.Files);
        }
        finally
        {
            File.Delete(ipa);
        }
    }

    [Fact]
    public async Task Install_on_an_iPhone_that_is_not_plugged_in_says_so()
    {
        await using var mux = new FakeUsbMux();
        var e = await Assert.ThrowsAsync<DeviceException>(() => new DeviceService(mux.Client()).InstallAsync("NOPE", "x.ipa"));
        Assert.Equal(DeviceErrors.NotConnected, e.Message);
    }

    [Fact]
    public async Task List_merges_usb_and_wifi_and_names_untrusted_iPhones_by_model()
    {
        await using var mux = new FakeUsbMux();
        mux.Devices.Add(new FakeDevice { DeviceId = 1, ConnectionType = "Network" });
        mux.Devices.Add(new FakeDevice { DeviceId = 2 });
        var untrusted = new FakeDevice { DeviceId = 3, Udid = "UNTRUSTED", PairRecord = null };
        untrusted.Values["ProductType"] = "iPhone17,1";
        mux.Devices.Add(untrusted);

        var list = await new DeviceService(mux.Client()).ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal(new DeviceInfo("00008110-000A1B2C3D4E5F60", "Test iPhone", "iPhone14,2", "18.1", true), list[0]);
        Assert.Equal(new DeviceInfo("UNTRUSTED", "iPhone", "iPhone17,1", "18.1", true), list[1]);
    }

    [Fact]
    public async Task Developer_mode_and_installed_version_are_read()
    {
        await using var mux = new FakeUsbMux();
        var device = new FakeDevice();
        device.Domains["com.apple.security.mac.amfi"] = new PlistDict { ["DeveloperModeStatus"] = false };
        device.Proxy.Installed["com.example.app"] = new PlistDict { ["CFBundleShortVersionString"] = "1.2.3" };
        mux.Devices.Add(device);
        var service = new DeviceService(mux.Client());

        Assert.False(await service.DeveloperModeEnabledAsync(device.Udid));
        Assert.Equal("1.2.3", await service.InstalledVersionAsync(device.Udid, "com.example.app"));
        Assert.Null(await service.InstalledVersionAsync(device.Udid, "com.example.other"));

        device.Domains.Clear();
        Assert.Null(await service.DeveloperModeEnabledAsync(device.Udid));
    }

    [Theory]
    [InlineData(@"C:\x\My App (signed).ipa", "MyAppsigned.ipa")]
    [InlineData("../../etc/passwd", "passwd.ipa")]
    [InlineData("....ipa", "app.ipa")]
    public void Staged_file_names_are_safe(string input, string expected) =>
        Assert.Equal(expected, DeviceService.StagingName(input));

    [Fact]
    public void Installd_errors_map_to_plain_messages()
    {
        Assert.Equal(DeviceErrors.DeveloperModeOff, DeviceErrors.FromInstall("DeveloperModeNotEnabled", null, null).Message);
        Assert.Equal(DeviceErrors.DeveloperModeOff,
            DeviceErrors.FromInstall("ApplicationVerificationFailed", "Developer Mode is disabled", null).Message);
        Assert.Contains("3", DeviceErrors.FromInstall("ApplicationVerificationFailed",
            "This device has reached the maximum number of installed apps using a free developer profile", null).Message);
        Assert.Contains("no longer valid", DeviceErrors.FromInstall("ApplicationVerificationFailed", "0xe8008018 (The identity used to sign the executable is no longer valid.)", null).Message);
        Assert.Contains("signed by someone else", DeviceErrors.FromInstall("MismatchedApplicationIdentifierEntitlement", null, null).Message);
        Assert.Contains("newer iOS", DeviceErrors.FromInstall("DeviceOSVersionTooLow", null, null).Message);
        var unknown = DeviceErrors.FromInstall("SomethingNew", new string('x', 1000), null).Message;
        Assert.Contains("SomethingNew", unknown);
        Assert.True(unknown.Length < 500);
    }

    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
