using System.Buffers.Binary;
using System.Text;

namespace RigControl.Lighting.Network;

/// <summary>
/// The small part of mDNS / DNS-SD (RFC 6762, 6763) needed to find lights: one question ("who offers this service?")
/// and reading the answers. Works on any <see cref="ILightNetwork"/> UDP port, so it is tested without a network.
/// </summary>
public static class Mdns
{
    public const string Group = "224.0.0.251";
    public const int Port = 5353;

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;

    /// <summary>A PTR question for a service type such as "_wled._tcp.local.", asking for an answer straight back to the sender.</summary>
    public static byte[] Query(string serviceType)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // id 0, a plain query, one question
        foreach (var label in serviceType.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Not a service name.", nameof(serviceType));
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        ms.Write(new byte[] { 0, (byte)TypePtr, 0x80, 0x01 }); // PTR, class IN with "answer me directly"
        return ms.ToArray();
    }

    /// <summary>The most answer packets one browse keeps; a home network sends a handful.</summary>
    public const int MostPackets = 256;

    /// <summary>
    /// The services of <paramref name="serviceType"/> named in these answer packets. A device speaks for itself
    /// only: a service's address is always the address an answer really came from. An address record that names
    /// some other address is ignored, so an announcement can't point RigControl at a device of its choosing.
    /// </summary>
    public static IReadOnlyList<MdnsService> Read(string serviceType, IEnumerable<UdpDatagram> packets)
    {
        // Names in DNS don't care about capitals; the instance name keeps the capitals the device gave it.
        var type = serviceType.TrimEnd('.');
        var same = StringComparer.OrdinalIgnoreCase;
        var instances = new List<string>();
        var srv = new Dictionary<string, (string Host, int Port)>(same);
        var txt = new Dictionary<string, Dictionary<string, string>>(same);
        var a = new Dictionary<string, string>(same);
        var from = new Dictionary<string, string>(same);
        foreach (var p in packets)
        {
            // Each packet is read on its own first, so a damaged one adds nothing at all.
            var pi = new List<string>();
            var ps = new Dictionary<string, (string Host, int Port)>(same);
            var pt = new Dictionary<string, Dictionary<string, string>>(same);
            var pa = new Dictionary<string, string>(same);
            var pf = new Dictionary<string, string>(same);
            try { ReadPacket(p, type, pi, ps, pt, pa, pf); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException)
            {
                continue; // not a DNS packet, or a damaged one
            }
            instances.AddRange(pi);
            foreach (var (k, v) in ps) srv[k] = v;
            foreach (var (k, v) in pt) txt[k] = v;
            // An address record only counts when it names the sender itself.
            foreach (var (k, v) in pa)
                if (LanAddress.Same(v, p.Host)) a[k] = LanAddress.Normalise(v);
            foreach (var (k, v) in pf) from.TryAdd(k, v);
        }
        var list = new List<MdnsService>();
        foreach (var inst in instances.Distinct(same))
        {
            var (host, port) = srv.TryGetValue(inst, out var s) ? s : ("", 0);
            var address = host != "" && a.TryGetValue(host, out var ip) ? ip : from.GetValueOrDefault(inst, "");
            if (address == "") continue;
            var name = inst.EndsWith("." + type, StringComparison.OrdinalIgnoreCase) ? inst[..^(type.Length + 1)] : inst;
            list.Add(new MdnsService(name, host, address, port, txt.TryGetValue(inst, out var t) ? t : new Dictionary<string, string>()));
        }
        return list.GroupBy(x => (x.Address, x.Instance)).Select(g => g.First()).ToList();
    }

    private static void ReadPacket(UdpDatagram p, string type, List<string> instances, Dictionary<string, (string, int)> srv,
        Dictionary<string, Dictionary<string, string>> txt, Dictionary<string, string> a, Dictionary<string, string> from)
    {
        var d = p.Data;
        if (d.Length < 12 || (d[2] & 0x80) == 0) return; // answers only
        int questions = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));
        var records = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8))
                      + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10));
        var i = 12;
        for (var q = 0; q < questions; q++)
        {
            Name(d, ref i);
            i += 4;
        }
        for (var r = 0; r < records && i < d.Length; r++)
        {
            var name = Name(d, ref i);
            if (i + 10 > d.Length) throw new InvalidDataException();
            var rtype = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i));
            var len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 8));
            i += 10;
            var end = i + len;
            if (end > d.Length) throw new InvalidDataException();
            switch (rtype)
            {
                case TypePtr when name.Equals(type, StringComparison.OrdinalIgnoreCase):
                {
                    var j = i;
                    var inst = Name(d, ref j);
                    instances.Add(inst);
                    from.TryAdd(inst, p.Host);
                    break;
                }
                case TypeSrv when len >= 7:
                {
                    var j = i + 6;
                    srv[name] = (Name(d, ref j), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 4)));
                    break;
                }
                case TypeTxt:
                {
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var j = i; j < end;)
                    {
                        int n = d[j++];
                        if (j + n > end) break;
                        var entry = Encoding.UTF8.GetString(d, j, n);
                        j += n;
                        var eq = entry.IndexOf('=');
                        if (eq > 0) map[entry[..eq]] = entry[(eq + 1)..];
                        else if (entry.Length > 0) map[entry] = "";
                    }
                    txt[name] = map;
                    break;
                }
                case TypeA when len == 4:
                    a[name] = $"{d[i]}.{d[i + 1]}.{d[i + 2]}.{d[i + 3]}";
                    break;
            }
            i = end;
        }
    }

    /// <summary>Reads a DNS name (with the "see earlier in this packet" shortcuts), without the last dot.</summary>
    private static string Name(byte[] d, ref int i)
    {
        var sb = new StringBuilder();
        var pos = i;
        var jumped = false;
        for (var hops = 0; hops < 32; hops++)
        {
            int n = d[pos];
            if (n == 0)
            {
                if (!jumped) i = pos + 1;
                return sb.ToString();
            }
            if ((n & 0xC0) == 0xC0)
            {
                if (!jumped) i = pos + 2;
                jumped = true;
                pos = ((n & 0x3F) << 8) | d[pos + 1];
                continue;
            }
            if (n > 63 || pos + 1 + n > d.Length) throw new InvalidDataException();
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(d, pos + 1, n));
            pos += 1 + n;
        }
        throw new InvalidDataException();
    }

    /// <summary>
    /// Asks on every local network that has a router and gathers the answers for <paramref name="wait"/>. Networks
    /// without a router (VPN adapters, virtual switches) are never asked.
    /// </summary>
    public static async Task<IReadOnlyList<MdnsService>> BrowseAsync(ILightNetwork net, string serviceType, TimeSpan wait, CancellationToken ct)
    {
        var query = Query(serviceType);
        var ports = new List<IUdpPort>();
        try
        {
            foreach (var n in LanAddress.Searchable(net))
            {
                try
                {
                    var port = net.OpenUdp(new UdpOptions { LocalAddress = n.Address });
                    port.Send(query, Group, Port);
                    ports.Add(port);
                }
                catch
                {
                    // that network can't be used: the others still can
                }
                try
                {
                    // Some devices only ever answer to the whole group; hearing that needs the shared mDNS port.
                    ports.Add(net.OpenUdp(new UdpOptions { LocalAddress = n.Address, LocalPort = Port, Share = true, JoinGroup = Group }));
                }
                catch
                {
                    // another program holds the port for itself: direct answers still arrive
                }
            }
            if (ports.Count == 0) return Array.Empty<MdnsService>();
            var packets = new System.Collections.Concurrent.ConcurrentBag<UdpDatagram>();
            var until = DateTime.UtcNow + wait;
            await Task.WhenAll(ports.Select(async port =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    UdpDatagram? got;
                    try { got = await port.ReceiveAsync(left, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch { break; }
                    if (got is null) break;
                    if (packets.Count >= MostPackets) break; // far more answers than a home has devices: enough
                    packets.Add(got);
                }
            })).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Read(serviceType, packets);
        }
        finally
        {
            foreach (var p in ports) p.Dispose();
        }
    }
}
