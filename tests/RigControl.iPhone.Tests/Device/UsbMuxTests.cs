using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RigControl.iPhone.Device;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests.Device;

public class UsbMuxTests
{
    [Fact]
    public async Task ListDevices_sends_a_version_1_plist_header_and_reads_usb_and_network_devices()
    {
        await using var mux = new FakeUsbMux();
        mux.Devices.Add(new FakeDevice { DeviceId = 3, Udid = "AAA" });
        mux.Devices.Add(new FakeDevice { DeviceId = 9, Udid = "BBB", ConnectionType = "Network" });

        var list = await mux.Client().ListDevicesAsync();

        Assert.Equal([new UsbMuxDevice(3, "AAA", true), new UsbMuxDevice(9, "BBB", false)], list);
        Assert.True(mux.Received.TryDequeue(out var sent));
        Assert.Equal((1u, 8u, 1u), (sent.Version, sent.Type, sent.Tag));
        Assert.Equal("ListDevices", sent.Message.String("MessageType"));
    }

    [Fact]
    public async Task Nothing_listening_means_Apple_Devices_is_missing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endPoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();

        var client = new UsbMuxClient(UsbMuxEndpoint.Tcp(endPoint), TimeSpan.FromSeconds(5));
        var e = await Assert.ThrowsAsync<DeviceException>(() => client.ListDevicesAsync());
        Assert.Equal(DeviceErrors.ServiceMissing, e.Message);
    }

    [Fact]
    public void Port_numbers_go_in_network_byte_order()
    {
        Assert.Equal(0x7EF2, UsbMuxClient.NetworkOrder(62078));
        Assert.Equal(0x5000, UsbMuxClient.NetworkOrder(0x50));
    }

    [Fact]
    public async Task Connect_reaches_the_requested_port_and_a_closed_port_is_refused()
    {
        await using var mux = new FakeUsbMux();
        mux.Devices.Add(new FakeDevice());
        var client = mux.Client();

        await using (var lockdown = await LockdownClient.ConnectAsync(client, 7))
            Assert.Equal("com.apple.mobile.lockdown", await lockdown.QueryTypeAsync());

        var connect = mux.Received.Select(r => r.Message).Last(m => m.String("MessageType") == "Connect");
        Assert.Equal(0x7EF2L, connect.Long("PortNumber"));

        var refused = await Assert.ThrowsAsync<DeviceException>(() => client.ConnectAsync(7, 1234));
        Assert.Contains("refused", refused.Message);
        var gone = await Assert.ThrowsAsync<DeviceException>(() => client.ConnectAsync(99, 62078));
        Assert.Equal(DeviceErrors.NotConnected, gone.Message);
    }

    [Fact]
    public async Task A_missing_pair_record_means_not_trusted()
    {
        await using var mux = new FakeUsbMux();
        mux.Devices.Add(new FakeDevice { PairRecord = null });

        var e = await Assert.ThrowsAsync<DeviceException>(() => mux.Client().ReadPairRecordAsync("00008110-000A1B2C3D4E5F60"));
        Assert.Equal(DeviceErrors.NotTrusted, e.Message);
        Assert.Contains("tap Trust", e.Message);
    }

    [Fact]
    public async Task Pair_record_and_BUID_are_read_and_keys_never_show_in_ToString()
    {
        await using var mux = new FakeUsbMux();
        mux.Devices.Add(new FakeDevice());
        var client = mux.Client();

        var record = await client.ReadPairRecordAsync("00008110-000A1B2C3D4E5F60");
        Assert.Equal(FakeDevice.HostId, record.HostId);
        Assert.Equal("FAKE-BUID", record.SystemBuid);
        Assert.DoesNotContain("not used", record.ToString());
        Assert.Equal("FAKE-BUID", await client.ReadBuidAsync());
    }

    [Fact]
    public async Task A_silent_service_times_out_with_a_plain_message()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            var client = new UsbMuxClient(UsbMuxEndpoint.Tcp((IPEndPoint)listener.LocalEndpoint), TimeSpan.FromMilliseconds(300));
            var e = await Assert.ThrowsAsync<DeviceException>(() => client.ListDevicesAsync());
            Assert.Equal(DeviceErrors.NoAnswer, e.Message);
            (await accept).Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task An_oversized_reply_is_refused_before_reading_it()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serve = Task.Run(async () =>
            {
                using var c = await listener.AcceptTcpClientAsync();
                var s = c.GetStream();
                await s.ReadAsync(new byte[4096]);
                var header = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(header, 64u * 1024 * 1024);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
                await s.WriteAsync(header);
                await Task.Delay(500);
            });
            var client = new UsbMuxClient(UsbMuxEndpoint.Tcp((IPEndPoint)listener.LocalEndpoint), TimeSpan.FromSeconds(5));
            var e = await Assert.ThrowsAsync<DeviceException>(() => client.ListDevicesAsync());
            Assert.Equal(DeviceErrors.BadReply, e.Message);
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Watch_reports_plug_and_unplug_by_udid()
    {
        await using var mux = new FakeUsbMux();
        var device = new FakeDevice { DeviceId = 4, Udid = "UDID-4" };
        await mux.Events.Writer.WriteAsync(device.Entry());
        await mux.Events.Writer.WriteAsync(new PlistDict { ["MessageType"] = "Detached", ["DeviceID"] = 4L });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<DeviceEvent>();
        await foreach (var e in new DeviceService(mux.Client()).WatchAsync(cts.Token))
        {
            events.Add(e);
            if (events.Count == 2)
                break;
        }

        Assert.Equal([new DeviceEvent(DeviceEventKind.Connected, "UDID-4", true), new DeviceEvent(DeviceEventKind.Disconnected, "UDID-4", true)], events);
    }

    [Fact]
    public async Task Lockdown_framing_is_a_big_endian_length_then_the_plist()
    {
        var ms = new MemoryStream();
        await PlistMessages.WriteAsync(ms, new PlistDict { ["Request"] = "QueryType" }, TimeSpan.FromSeconds(1), default);
        var bytes = ms.ToArray();
        Assert.Equal(bytes.Length - 4, (int)BinaryPrimitives.ReadUInt32BigEndian(bytes));
        Assert.StartsWith("<?xml", System.Text.Encoding.UTF8.GetString(bytes, 4, 5));

        ms.Position = 0;
        Assert.Equal("QueryType", (await PlistMessages.ReadAsync(ms, TimeSpan.FromSeconds(1), default)).String("Request"));

        var huge = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(huge, 17u * 1024 * 1024);
        var e = await Assert.ThrowsAsync<DeviceException>(() => PlistMessages.ReadAsync(new MemoryStream(huge), TimeSpan.FromSeconds(1), default));
        Assert.Equal(DeviceErrors.BadReply, e.Message);

        var cut = await Assert.ThrowsAsync<DeviceException>(() => PlistMessages.ReadAsync(new MemoryStream(bytes[..10]), TimeSpan.FromSeconds(1), default));
        Assert.Equal(DeviceErrors.ConnectionLost, cut.Message);
    }
}
