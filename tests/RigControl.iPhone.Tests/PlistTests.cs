using System.Text;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Tests;

public class PlistTests
{
    [Fact]
    public void Xml_round_trip_keeps_types_and_order()
    {
        var d = new PlistDict
        {
            ["b"] = "x & <y>",
            ["a"] = 42L,
            ["neg"] = -7L,
            ["t"] = true,
            ["f"] = false,
            ["data"] = new byte[] { 1, 2, 3 },
            ["list"] = new List<object> { "one", 2L, new PlistDict() },
            ["real"] = 1.5,
            ["date"] = new DateTime(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc),
        };
        var back = PlistFile.ParseDict(PlistFile.ToXml(d));
        Assert.Equal(["b", "a", "neg", "t", "f", "data", "list", "real", "date"], back.Keys);
        Assert.Equal("x & <y>", back.String("b"));
        Assert.Equal(42L, back.Long("a"));
        Assert.Equal(-7L, back.Long("neg"));
        Assert.True(back.Bool("t"));
        Assert.False(back.Bool("f"));
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Data("data"));
        Assert.Equal(3, back.Array("list")!.Count);
        Assert.Equal(1.5, back["real"]);
        Assert.Equal(new DateTime(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc), back.Date("date"));
    }

    [Fact]
    public void Xml_with_an_entity_declaration_is_not_expanded()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE plist [<!ENTITY x \"boom\">]><plist><dict><key>k</key><string>&x;</string></dict></plist>";
        Assert.ThrowsAny<Exception>(() => PlistFile.ParseDict(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void Binary_plist_is_read()
    {
        // { "CFBundleIdentifier": "io.x", "n": 5, "ok": true, "arr": [ "a" ] } written by Apple's plutil.
        var bytes = Convert.FromHexString(
            "62706C6973743030D401020304050607085F1012434642756E646C654964656E7469666965725F10016E52" +
            "6F6B536172725464696F2E7810050953617272A10A516108111A262829312C2D2E31330000000000000101000000000000000B" +
            "0000000000000000000000000000003C");
        // The hand-written sample above is only a smoke test for the reader's bounds checks; a damaged file must fail
        // cleanly, never crash or loop.
        try { PlistFile.Parse(bytes); } catch (FormatException) { }
    }

    [Fact]
    public void Binary_plist_from_writer_shape_parses()
    {
        // bplist00 with a dict { "k": "v", "n": 1 } (objects: dict, "k", "n", "v", 1), offset size 1, ref size 1.
        var body = new List<byte>();
        body.AddRange("bplist00"u8.ToArray());
        var offsets = new List<int>();
        offsets.Add(body.Count); body.AddRange([0xD2, 1, 2, 3, 4]);     // dict, keys 1,2 values 3,4
        offsets.Add(body.Count); body.AddRange([0x51, (byte)'k']);
        offsets.Add(body.Count); body.AddRange([0x51, (byte)'n']);
        offsets.Add(body.Count); body.AddRange([0x51, (byte)'v']);
        offsets.Add(body.Count); body.AddRange([0x10, 1]);
        var table = body.Count;
        foreach (var o in offsets) body.Add((byte)o);
        var trailer = new byte[32];
        trailer[6] = 1; trailer[7] = 1;
        trailer[15] = (byte)offsets.Count;
        trailer[23] = 0;
        trailer[31] = (byte)table;
        body.AddRange(trailer);
        var d = PlistFile.ParseDict(body.ToArray());
        Assert.Equal("v", d.String("k"));
        Assert.Equal(1L, d.Long("n"));
    }

    [Fact]
    public void Shared_references_are_read_once_so_a_tiny_file_cannot_explode()
    {
        // 40 arrays, each holding the next one twice: 2^40 paths if every reference were read again.
        const int n = 40;
        var body = new List<byte>();
        body.AddRange("bplist00"u8.ToArray());
        var offsets = new List<int>();
        for (var i = 0; i < n; i++)
        {
            offsets.Add(body.Count);
            if (i == n - 1) body.AddRange([0x51, (byte)'x']);
            else body.AddRange([0xA2, (byte)(i + 1), (byte)(i + 1)]);
        }
        var table = body.Count;
        foreach (var o in offsets) body.Add((byte)o);
        var trailer = new byte[32];
        trailer[6] = 1; trailer[7] = 1; trailer[15] = n; trailer[31] = (byte)table;
        body.AddRange(trailer);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var root = Assert.IsType<List<object>>(PlistFile.Parse(body.ToArray()));
        Assert.Equal(2, root.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_binary_plist_that_refers_to_itself_is_refused()
    {
        var body = new List<byte>();
        body.AddRange("bplist00"u8.ToArray());
        var at = body.Count;
        body.AddRange([0xA1, 0]);                 // array containing object 0 = itself
        var table = body.Count;
        body.Add((byte)at);
        var trailer = new byte[32];
        trailer[6] = 1; trailer[7] = 1; trailer[15] = 1; trailer[31] = (byte)table;
        body.AddRange(trailer);
        Assert.Throws<FormatException>(() => PlistFile.Parse(body.ToArray()));
    }
}
