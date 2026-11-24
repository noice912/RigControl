using System.Net;
using System.Net.Sockets;
using System.Text;
using RigControl.Lighting.Network;

namespace RigControl.Lighting.Tests.Network;

public class LanAddressTests
{
    [Theory]
    [InlineData("192.168.1.100", true)]
    [InlineData("172.16.0.50", true)]
    [InlineData("10.0.0.2", true)]
    [InlineData("172.16.4.4", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.3.3", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("wled.local", false)]
    [InlineData("fe80::1", false)]
    [InlineData(null, false)]
    public void Only_private_addresses_can_be_paired(string? host, bool ok) => Assert.Equal(ok, LanAddress.IsPrivate(host));

    [Theory]
    [InlineData("172.16.0.255", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("224.0.0.251", true)]
    [InlineData("239.255.255.250", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("1.1.1.1", false)]
    [InlineData("224.1.2.3", false)]
    [InlineData("discovery.example.com", false)]
    public void Packets_only_go_to_the_local_network(string host, bool ok)
    {
        Assert.Equal(ok, LanAddress.MaySendTo(host));
        if (!ok) Assert.Throws<InvalidOperationException>(() => LanAddress.RequireLocal(host));
    }

    [Fact]
    public void A_local_network_knows_its_broadcast_address_and_its_other_hosts()
    {
        var n = new LocalNetwork("172.16.0.10", 24, true);
        Assert.Equal("172.16.0.255", n.Broadcast);
        var hosts = n.OtherHosts().ToList();
        Assert.Equal(253, hosts.Count);
        Assert.DoesNotContain("172.16.0.10", hosts);
        Assert.Equal("172.16.0.1", hosts[0]);
        Assert.Equal("172.16.0.254", hosts[^1]);
        Assert.Empty(new LocalNetwork("10.0.0.2", 8, true).OtherHosts()); // far too big to try one by one
    }
}

public class SecretRuleTests
{
    private static readonly BoundSecret Token = BoundSecret.For("s3cr3t", "172.16.0.50");

    [Fact]
    public void A_secret_comes_out_only_for_the_address_it_was_paired_with()
    {
        Assert.Equal("s3cr3t", Token.RevealFor("172.16.0.50"));
        Assert.Throws<SecretRuleException>(() => Token.RevealFor("172.16.0.51"));
        Assert.Throws<SecretRuleException>(() => Token.RevealFor("evil.example.com"));
    }

    [Fact]
    public void Printing_a_secret_never_shows_it()
    {
        Assert.DoesNotContain("s3cr3t", Token.ToString());
        Assert.DoesNotContain("s3cr3t", $"{Token}");
    }

    [Fact]
    public void The_mark_is_filled_in_the_address_headers_and_body_for_the_paired_address()
    {
        var ask = new HttpAsk("PUT", "http://172.16.0.50:16021/api/v1/{secret}/state")
        {
            Body = "{\"key\":\"{secret}\"}",
            Headers = new Dictionary<string, string> { ["hue-application-key"] = "{secret}" },
            Secret = Token,
        };
        var filled = SecretRule.Fill(ask);
        Assert.Equal("http://172.16.0.50:16021/api/v1/s3cr3t/state", filled.Url);
        Assert.Equal("{\"key\":\"s3cr3t\"}", filled.Body);
        Assert.Equal("s3cr3t", filled.Headers!["hue-application-key"]);
        Assert.Null(filled.Secret);
    }

    [Fact]
    public void A_request_with_a_secret_to_any_other_address_is_refused()
    {
        var elsewhere = new HttpAsk("GET", "http://172.16.0.99:16021/api/v1/{secret}/") { Secret = Token };
        Assert.Throws<SecretRuleException>(() => SecretRule.Fill(elsewhere));
        // Even when the mark isn't used: a request that carries a secret must go to its device.
        Assert.Throws<SecretRuleException>(() => SecretRule.Fill(new HttpAsk("GET", "http://172.16.0.99/") { Secret = Token }));
    }

    [Fact]
    public void A_mark_without_a_secret_is_refused_and_a_plain_request_passes_untouched()
    {
        Assert.Throws<SecretRuleException>(() => SecretRule.Fill(HttpAsk.Get("http://172.16.0.50/api/{secret}/x")));
        var plain = HttpAsk.Get("http://172.16.0.50/json/info");
        Assert.Same(plain, SecretRule.Fill(plain));
    }

    [Fact]
    public async Task The_fake_network_applies_the_same_two_rules()
    {
        var net = new FakeLightNetwork { OnHttp = _ => FakeLightNetwork.Json("{}") };
        await Assert.ThrowsAsync<SecretRuleException>(() => net.HttpAsync(new HttpAsk("GET", "http://172.16.0.99/{secret}") { Secret = Token }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => net.HttpAsync(HttpAsk.Get("http://93.184.216.34/"), default));
        Assert.Empty(net.Asks); // neither was sent
        using var udp = net.OpenUdp();
        Assert.Throws<InvalidOperationException>(() => udp.Send(new byte[] { 1 }, "8.8.8.8", 53));
        Assert.Empty(net.Sent);
    }
}

public class MdnsTests
{
    [Fact]
    public void The_question_asks_for_the_service_and_for_a_direct_answer()
    {
        var q = Mdns.Query("_wled._tcp.local.");
        Assert.Equal(Hex.Parse("00 00 00 00 00 01 00 00 00 00 00 00 05 5F 77 6C 65 64 04 5F 74 63 70 05 6C 6F 63 61 6C 00 00 0C 80 01"), q);
    }

    /// <summary>An answer as a device sends it: PTR, SRV, TXT and A records, with names shortened by pointers.</summary>
    private static byte[] Answer(string instance, string host, byte[] ip, int port, params string[] txt)
    {
        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void Label(string s) { var b = Encoding.UTF8.GetBytes(s); ms.WriteByte((byte)b.Length); ms.Write(b); }
        void Pointer(int offset) { ms.WriteByte((byte)(0xC0 | (offset >> 8))); ms.WriteByte((byte)offset); }
        U16(0); U16(0x8400); U16(0); U16(4); U16(0); U16(0);
        const int service = 12;                    // "_wled._tcp.local" starts here
        Label("_wled"); Label("_tcp"); Label("local"); ms.WriteByte(0);
        U16(12); U16(1); U16(0); U16(120);          // PTR, IN, ttl
        var inst = (int)ms.Position + 2;
        U16(Encoding.UTF8.GetByteCount(instance) + 1 + 2);
        Label(instance); Pointer(service);
        // SRV for the instance
        Pointer(inst); U16(33); U16(0x8001); U16(0); U16(120);
        var hostBytes = Encoding.UTF8.GetByteCount(host);
        U16(6 + 1 + hostBytes + 2);
        U16(0); U16(0); U16(port);
        var hostAt = (int)ms.Position;
        Label(host); Pointer(service + 6 + 5);       // host + ".local"
        // TXT
        Pointer(inst); U16(16); U16(0x8001); U16(0); U16(120);
        U16(txt.Sum(t => 1 + Encoding.UTF8.GetByteCount(t)));
        foreach (var t in txt) Label(t);
        // A
        Pointer(hostAt); U16(1); U16(0x8001); U16(0); U16(120); U16(4); ms.Write(ip);
        return ms.ToArray();
    }

    [Fact]
    public void An_answer_gives_name_host_address_port_and_text_records()
    {
        var packet = Answer("Desk strip", "wled-a1b2c3", new byte[] { 172, 16, 0, 50 }, 80, "mac=aabbccddeeff", "flag");
        var found = Assert.Single(Mdns.Read("_wled._tcp.local.", new[] { new UdpDatagram("172.16.0.50", 5353, packet) }));
        Assert.Equal("Desk strip", found.Instance);
        Assert.Equal("wled-a1b2c3.local", found.Host);
        Assert.Equal("172.16.0.50", found.Address);
        Assert.Equal(80, found.Port);
        Assert.Equal("aabbccddeeff", found.Txt["mac"]);
        Assert.Equal("", found.Txt["flag"]);
    }

    [Fact]
    public void Other_services_questions_and_rubbish_are_ignored()
    {
        var packet = Answer("Desk strip", "wled-a1b2c3", new byte[] { 172, 16, 0, 50 }, 80);
        Assert.Empty(Mdns.Read("_hue._tcp.local.", new[] { new UdpDatagram("172.16.0.50", 5353, packet) }));
        Assert.Empty(Mdns.Read("_wled._tcp.local.", new[]
        {
            new UdpDatagram("172.16.0.9", 5353, Mdns.Query("_wled._tcp.local.")),       // somebody else's question
            new UdpDatagram("172.16.0.9", 5353, new byte[] { 1, 2, 3 }),
            new UdpDatagram("172.16.0.9", 5353, packet[..(packet.Length / 2)]),          // cut off
            new UdpDatagram("172.16.0.9", 5353, Enumerable.Repeat((byte)0xC0, 40).ToArray()),
        }));
    }

    [Fact]
    public async Task Browsing_asks_on_home_networks_only_and_reads_what_comes_back()
    {
        var net = new FakeLightNetwork();
        net.Networks.Add(new LocalNetwork("10.0.0.2", 24, false)); // a VPN adapter: no router, never searched
        var packet = Answer("Shelf", "wled-shelf", new byte[] { 172, 16, 0, 77 }, 80, "mac=001122334455");
        net.OnUdp = sent => sent.Host == Mdns.Group && sent.LocalPort == 50001 ? new[] { new UdpDatagram("172.16.0.77", 5353, packet) } : null;
        var found = await Mdns.BrowseAsync(net, "_wled._tcp.local.", TimeSpan.FromMilliseconds(150), default);
        Assert.Equal("172.16.0.77", Assert.Single(found).Address);
        var question = Assert.Single(net.Sent); // one question, on the home network only
        Assert.Equal((Mdns.Group, Mdns.Port), (question.Host, question.Port));
    }

    [Fact]
    public void An_answer_cannot_point_at_another_address_than_the_one_it_came_from()
    {
        // The address record names 10.0.0.1 (somewhere the sender wants RigControl to go); the packet came from 172.16.0.66.
        var packet = Answer("Desk strip", "wled-a1b2c3", new byte[] { 10, 0, 0, 1 }, 8500, "mac=aabbccddeeff");
        var found = Assert.Single(Mdns.Read("_wled._tcp.local.", new[] { new UdpDatagram("172.16.0.66", 5353, packet) }));
        Assert.Equal("172.16.0.66", found.Address);

        // Not even with the help of a second packet from somewhere else.
        var helper = Answer("Other", "wled-a1b2c3", new byte[] { 10, 0, 0, 1 }, 80);
        var all = Mdns.Read("_wled._tcp.local.", new[] { new UdpDatagram("172.16.0.66", 5353, packet), new UdpDatagram("172.16.0.67", 5353, helper) });
        Assert.DoesNotContain(all, s => s.Address == "10.0.0.1");
    }

    [Fact]
    public async Task A_flood_of_answers_is_cut_off()
    {
        var net = new FakeLightNetwork();
        var packet = Answer("Shelf", "wled-shelf", new byte[] { 172, 16, 0, 77 }, 80);
        net.OnUdp = sent => sent.Host == Mdns.Group ? Enumerable.Range(0, 5000).Select(i => new UdpDatagram("172.16.0." + (i % 250 + 1), 5353, packet)) : null;
        var found = await Mdns.BrowseAsync(net, "_wled._tcp.local.", TimeSpan.FromMilliseconds(150), default);
        Assert.InRange(found.Count, 1, Mdns.MostPackets);
    }
}

/// <summary>The real network class, tried against this PC only (127.0.0.1).</summary>
public class RealLightNetworkTests
{
    [Fact]
    public async Task Udp_packets_go_out_and_come_back_on_this_pc()
    {
        using var net = new RealLightNetwork();
        using var a = net.OpenUdp(new UdpOptions { LocalAddress = "127.0.0.1" });
        using var b = net.OpenUdp(new UdpOptions { LocalAddress = "127.0.0.1" });
        a.Send(new byte[] { 1, 2, 3 }, "127.0.0.1", b.LocalPort);
        var got = await b.ReceiveAsync(TimeSpan.FromSeconds(2), default);
        Assert.NotNull(got);
        Assert.Equal(new byte[] { 1, 2, 3 }, got!.Data);
        Assert.Equal(a.LocalPort, got.Port);
        Assert.Null(await b.ReceiveAsync(TimeSpan.FromMilliseconds(50), default)); // nothing more: a quiet "no", not an error
    }

    [Fact]
    public async Task Nothing_is_sent_to_an_address_outside_the_local_network()
    {
        using var net = new RealLightNetwork();
        using var udp = net.OpenUdp(new UdpOptions { LocalAddress = "127.0.0.1" });
        Assert.Throws<InvalidOperationException>(() => udp.Send(new byte[] { 1 }, "8.8.8.8", 9));
        await Assert.ThrowsAsync<InvalidOperationException>(() => net.ConnectTcpAsync("8.8.8.8", 9, TimeSpan.FromSeconds(1), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => net.HttpAsync(HttpAsk.Get("http://example.com/"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => net.HttpAsync(HttpAsk.Get("http://8.8.8.8/"), default));
        Assert.Throws<InvalidOperationException>(() => net.ListenTcp("8.8.8.8"));
    }

    [Fact]
    public async Task Tcp_connects_listens_reads_and_writes_on_this_pc()
    {
        using var net = new RealLightNetwork();
        using var listener = net.ListenTcp("127.0.0.1");
        var accepting = listener.AcceptAsync(TimeSpan.FromSeconds(2), default);
        using var client = await net.ConnectTcpAsync("127.0.0.1", listener.Port, TimeSpan.FromSeconds(2), default);
        using var server = await accepting;
        Assert.NotNull(server);
        Assert.Equal("127.0.0.1", client.RemoteHost);
        await client.WriteAsync("hello\r\n"u8.ToArray(), default);
        var buffer = new byte[32];
        var n = await server!.ReadAsync(buffer, TimeSpan.FromSeconds(2), default);
        Assert.Equal("hello\r\n", Encoding.UTF8.GetString(buffer, 0, n));
        Assert.Equal(-1, await server.ReadAsync(buffer, TimeSpan.FromMilliseconds(50), default)); // nothing in time
        client.Dispose();
        Assert.Equal(0, await server.ReadAsync(buffer, TimeSpan.FromSeconds(2), default));          // the other end closed
        Assert.Null(await listener.AcceptAsync(TimeSpan.FromMilliseconds(50), default));
    }

    /// <summary>A one-request web server on 127.0.0.1 that remembers what it was asked.</summary>
    private static (int Port, Task<string> Request) OneShotServer(string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            try
            {
                using var tcp = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var stream = tcp.GetStream();
                var sb = new StringBuilder();
                var buffer = new byte[4096];
                // Headers, then as many body bytes as Content-Length says.
                while (true)
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n <= 0) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    var text = sb.ToString();
                    var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end < 0) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(text, @"Content-Length: (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var want = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                    if (text.Length - end - 4 >= want) break;
                }
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                return sb.ToString();
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, task);
    }

    [Fact]
    public async Task Http_fills_the_secret_in_for_the_paired_address_and_reads_the_answer()
    {
        var (port, request) = OneShotServer("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nX-Device: lamp\r\nContent-Length: 11\r\nConnection: close\r\n\r\n{\"ok\":true}");
        using var net = new RealLightNetwork();
        var reply = await net.HttpAsync(new HttpAsk("PUT", $"http://127.0.0.1:{port}/api/{{secret}}/state")
        {
            Body = "{\"on\":true}",
            Headers = new Dictionary<string, string> { ["x-key"] = "{secret}" },
            Secret = BoundSecret.For("tok123", "127.0.0.1"),
        }, default);
        Assert.Equal(200, reply.Status);
        Assert.True(reply.Ok);
        Assert.Equal("{\"ok\":true}", reply.Body);
        Assert.Equal("lamp", reply.Header("x-device"));
        var asked = await request;
        Assert.StartsWith("PUT /api/tok123/state HTTP/1.1", asked);
        Assert.Contains("x-key: tok123", asked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Type: application/json", asked, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("{\"on\":true}", asked);
    }

    [Fact]
    public async Task Http_never_sends_a_secret_to_another_address_and_never_follows_a_redirect()
    {
        using var net = new RealLightNetwork();
        // The secret belongs to 172.16.0.50; this request would go to this PC: refused before anything is sent.
        await Assert.ThrowsAsync<SecretRuleException>(() => net.HttpAsync(
            new HttpAsk("GET", "http://127.0.0.1:9/api/{secret}") { Secret = BoundSecret.For("tok123", "172.16.0.50") }, default));

        var (port, request) = OneShotServer("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var reply = await net.HttpAsync(HttpAsk.Get($"http://127.0.0.1:{port}/x"), default);
        Assert.Equal(302, reply.Status); // handed back as it is, not followed
        Assert.False(reply.Ok);
        Assert.Throws<IOException>(() => reply.EnsureOk());
        await request;
    }

    [Fact]
    public async Task A_device_that_does_not_answer_in_time_is_a_plain_timeout()
    {
        using var net = new RealLightNetwork();
        using var listener = net.ListenTcp("127.0.0.1"); // accepts the connection, never answers
        await Assert.ThrowsAsync<TimeoutException>(() => net.HttpAsync(
            HttpAsk.Get($"http://127.0.0.1:{listener.Port}/") with { Timeout = TimeSpan.FromMilliseconds(200) }, default));
    }

    [Fact]
    public async Task A_secure_request_must_say_which_certificate_to_expect()
    {
        using var net = new RealLightNetwork();
        await Assert.ThrowsAsync<InvalidOperationException>(() => net.HttpAsync(HttpAsk.Get("https://127.0.0.1:9/"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => net.HttpAsync(HttpAsk.Get("https://127.0.0.1:9/") with { Tls = new TlsRule(null, null) }, default));
    }

    [Fact]
    public void A_device_certificate_must_match_the_expected_name_and_the_remembered_fingerprint()
    {
        Assert.True(RealLightNetwork.CertMatches(new TlsRule("001788fffe123456", null), "001788FFFE123456", "AA"));
        Assert.False(RealLightNetwork.CertMatches(new TlsRule("001788fffe123456", null), "someone-else", "AA"));
        Assert.True(RealLightNetwork.CertMatches(new TlsRule(null, "aa11"), "anything", "AA11"));
        Assert.False(RealLightNetwork.CertMatches(new TlsRule("001788fffe123456", "AA11"), "001788fffe123456", "BB22")); // a different certificate than at pairing
        Assert.False(RealLightNetwork.CertMatches(new TlsRule(null, null), "x", "AA"));                               // "any certificate" is never accepted
    }

    [Fact]
    public void Local_networks_are_private_addresses_only()
    {
        using var net = new RealLightNetwork();
        Assert.All(net.LocalNetworks(), n => Assert.True(LanAddress.IsPrivate(n.Address)));
    }

    /// <summary>A web server on 127.0.0.1 that answers one request with headers and then keeps sending.</summary>
    private static (int Port, Task Done) FloodServer(string headers, int megabytes)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            try
            {
                using var tcp = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var stream = tcp.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
                var chunk = Encoding.ASCII.GetBytes(new string('x', 64 * 1024));
                for (var i = 0; i < megabytes * 16; i++) await stream.WriteAsync(chunk);
            }
            catch
            {
                // the other end hung up, as it should
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, task);
    }

    [Fact]
    public async Task An_answer_that_never_ends_is_dropped_after_one_megabyte()
    {
        using var net = new RealLightNetwork();
        var (port, done) = FloodServer("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n", 64);
        var ex = await Assert.ThrowsAsync<IOException>(() => net.HttpAsync(HttpAsk.Get($"http://127.0.0.1:{port}/json/info"), default));
        Assert.Equal("The device sent an answer that was too long, so it was ignored.", ex.Message);
        await done.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_answer_that_says_it_is_too_long_is_dropped_before_any_of_it_is_read()
    {
        using var net = new RealLightNetwork();
        var (port, done) = FloodServer("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 50000000\r\n\r\n", 1);
        var ex = await Assert.ThrowsAsync<IOException>(() => net.HttpAsync(HttpAsk.Get($"http://127.0.0.1:{port}/json/info"), default));
        Assert.Contains("too long", ex.Message);
        await done.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
