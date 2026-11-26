using RigControl.iPhone.Device;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests.Device;

public class LockdownTests
{
    private static async Task<PairRecord> PairAsync(FakeUsbMux mux) =>
        await mux.Client().ReadPairRecordAsync("00008110-000A1B2C3D4E5F60");

    [Fact]
    public async Task GetValue_reads_values_and_a_missing_one_is_null()
    {
        await using var mux = new FakeUsbMux();
        var device = new FakeDevice();
        device.Domains["com.apple.security.mac.amfi"] = new PlistDict { ["DeveloperModeStatus"] = true };
        mux.Devices.Add(device);

        await using var lockdown = await LockdownClient.ConnectAsync(mux.Client(), 7);
        Assert.Equal("iPhone14,2", await lockdown.GetValueAsync(null, "ProductType"));
        Assert.Null(await lockdown.GetValueAsync(null, "NoSuchKey"));

        await lockdown.StartSessionAsync(await PairAsync(mux));
        Assert.True(lockdown.InSession);
        Assert.Equal(true, await lockdown.GetValueAsync("com.apple.security.mac.amfi", "DeveloperModeStatus"));
        var all = Assert.IsType<PlistDict>(await lockdown.GetValueAsync());
        Assert.Equal("Test iPhone", all.String("DeviceName"));

        var service = await lockdown.StartServiceAsync(AfcClient.ServiceName);
        Assert.Equal(new LockdownService(AfcClient.ServiceName, FakeDevice.AfcPort, false), service);
    }

    [Fact]
    public async Task A_record_the_iPhone_does_not_know_means_not_trusted()
    {
        await using var mux = new FakeUsbMux();
        mux.Devices.Add(new FakeDevice { PairRecord = FakeDevice.MakePairRecord(FakeDevice.HostId) });
        var stale = PairRecord.FromPlist(FakeDevice.MakePairRecord("SOME-OTHER-HOST"));

        await using var lockdown = await LockdownClient.ConnectAsync(mux.Client(), 7);
        var e = await Assert.ThrowsAsync<DeviceException>(() => lockdown.StartSessionAsync(stale));
        Assert.Equal(DeviceErrors.NotTrusted, e.Message);
        Assert.Equal("InvalidHostID", LockdownError.Of(e));
    }

    [Fact]
    public async Task A_locked_iPhone_says_so()
    {
        await using var mux = new FakeUsbMux();
        mux.Devices.Add(new FakeDevice { SessionError = "PasswordProtected" });

        await using var lockdown = await LockdownClient.ConnectAsync(mux.Client(), 7);
        var e = await Assert.ThrowsAsync<DeviceException>(async () => await lockdown.StartSessionAsync(await PairAsync(mux)));
        Assert.Equal(DeviceErrors.Locked, e.Message);
    }

    [Fact]
    public async Task Session_switches_to_TLS_with_the_pair_record_certificates()
    {
        using var host = TestCert.Create("host");
        using var deviceCert = TestCert.Create("device");
        var sawHost = false;
        await using var mux = new FakeUsbMux();
        var device = new FakeDevice
        {
            PairRecord = FakeDevice.MakePairRecord(FakeDevice.HostId, host.CertPem, host.KeyPem, deviceCert.CertPem),
            ServerTls = TestCert.Server(deviceCert, host, ok => sawHost = ok),
        };
        mux.Devices.Add(device);

        await using var lockdown = await LockdownClient.ConnectAsync(mux.Client(), 7);
        await lockdown.StartSessionAsync(await PairAsync(mux));
        // With TLS 1.3 the client can finish before the device has checked the client certificate; once the device
        // has answered a request over TLS, that check has certainly run.
        Assert.Equal("18.1", await lockdown.GetValueAsync(null, "ProductVersion"));
        Assert.True(Volatile.Read(ref sawHost));
        await lockdown.StopSessionAsync();
        Assert.Contains("StopSession", device.LockdownRequests);
    }

    [Fact]
    public async Task TLS_refuses_an_iPhone_certificate_that_is_not_the_paired_one()
    {
        using var host = TestCert.Create("host");
        using var paired = TestCert.Create("device");
        using var impostor = TestCert.Create("impostor");
        await using var mux = new FakeUsbMux();
        mux.Devices.Add(new FakeDevice
        {
            PairRecord = FakeDevice.MakePairRecord(FakeDevice.HostId, host.CertPem, host.KeyPem, paired.CertPem),
            ServerTls = TestCert.Server(impostor, host),
        });

        await using var lockdown = await LockdownClient.ConnectAsync(mux.Client(), 7);
        var e = await Assert.ThrowsAsync<DeviceException>(async () => await lockdown.StartSessionAsync(await PairAsync(mux)));
        Assert.Equal(DeviceErrors.SecureConnectionFailed, e.Message);
    }

    [Fact]
    public void A_pair_record_without_keys_counts_as_not_trusted()
    {
        var d = FakeDevice.MakePairRecord(FakeDevice.HostId);
        d.Remove("HostPrivateKey");
        var e = Assert.Throws<DeviceException>(() => PairRecord.FromPlist(d));
        Assert.Equal(DeviceErrors.NotTrusted, e.Message);
    }

    [Theory]
    [InlineData("PairingDialogResponsePending", DeviceErrors.TrustPending)]
    [InlineData("UserDeniedPairing", DeviceErrors.TrustDenied)]
    [InlineData("InvalidHostID", DeviceErrors.NotTrusted)]
    public void Lockdown_errors_map_to_plain_messages(string error, string message) =>
        Assert.Equal(message, DeviceErrors.FromLockdown("StartSession", error).Message);
}
