using System.Buffers.Binary;
using System.Text;

namespace RigControl.Lighting;

public sealed class RgbMode
{
    public string Name { get; set; } = "";
    public int Value { get; set; }                 // opaque; echoed back unchanged
    public uint Flags { get; set; }
    public uint SpeedMin { get; set; }
    public uint SpeedMax { get; set; }
    public uint BrightnessMin { get; set; }
    public uint BrightnessMax { get; set; }
    public uint ColorsMin { get; set; }
    public uint ColorsMax { get; set; }
    public uint Speed { get; set; }
    public uint Brightness { get; set; }
    public uint Direction { get; set; }
    public uint ColorMode { get; set; }            // 0 none, 1 per-LED, 2 mode-specific, 3 random
    public List<uint> Colors { get; set; } = new();

    public const uint HasSpeed = 1 << 0;
    public const uint DirectionMask = (1 << 1) | (1 << 2) | (1 << 3);
    public const uint HasBrightness = 1 << 4;
    public const uint PerLedColor = 1 << 5;
    public const uint ModeSpecificColor = 1 << 6;
    public const uint RandomColor = 1 << 7;

    public bool Has(uint flag) => (Flags & flag) != 0;

    public RgbMode Clone() => new()
    {
        Name = Name, Value = Value, Flags = Flags, SpeedMin = SpeedMin, SpeedMax = SpeedMax,
        BrightnessMin = BrightnessMin, BrightnessMax = BrightnessMax, ColorsMin = ColorsMin, ColorsMax = ColorsMax,
        Speed = Speed, Brightness = Brightness, Direction = Direction, ColorMode = ColorMode, Colors = new List<uint>(Colors),
    };
}

public sealed class RgbZone
{
    public string Name { get; set; } = "";
    public uint Type { get; set; }
    public uint LedsMin { get; set; }
    public uint LedsMax { get; set; }
    public uint LedsCount { get; set; }
    public uint Flags { get; set; }
    /// <summary>A keyboard-style grid (row by row): each cell holds the LED number within the zone, or 0xFFFFFFFF for none.</summary>
    public uint[]? Matrix { get; set; }
    public int MatrixWidth { get; set; }
    public int MatrixHeight { get; set; }

    /// <summary>How many colour slots the zone really owns (an "effects-only" zone owns at most one).</summary>
    public int ColorSlots => (Flags & 1) != 0 ? (int)Math.Min(LedsCount, 1) : (int)LedsCount;
}

public sealed class RgbController
{
    public int Index { get; set; }
    public int Type { get; set; }
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Location { get; set; } = "";
    public int ActiveMode { get; set; }
    public List<RgbMode> Modes { get; set; } = new();
    public List<RgbZone> Zones { get; set; } = new();
    public int LedCount { get; set; }
    public List<uint> Colors { get; set; } = new();

    /// <summary>Stable identity across restarts (indices change; two identical controllers differ by location/serial).</summary>
    public string Key => $"{Type}|{Name}|{Serial}|{Location}";
}

public readonly record struct OpenRgbPacket(uint DeviceId, uint Id, byte[] Payload);

/// <summary>
/// The OpenRGB SDK wire format, protocol versions 3 to 5. See docs/protocols/openrgb-integration.md section 3.
/// Colours are u32 values R | G&lt;&lt;8 | B&lt;&lt;16 (bytes R, G, B, 0 on the wire). Strings are u16 length (including the
/// terminating NUL) followed by the bytes. SAVEMODE (writes to device flash) and RESIZEZONE are deliberately absent.
/// </summary>
public static class OpenRgbProtocol
{
    public const int HeaderSize = 16;
    public const int MaxPacketSize = 8 * 1024 * 1024;
    public const int MinVersion = 3;
    public const int MaxVersion = 5;

    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData = 1;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName = 50;
    public const uint DeviceListUpdated = 100;
    public const uint UpdateLeds = 1050;
    public const uint UpdateZoneLeds = 1051;
    public const uint UpdateMode = 1101;

    private static readonly byte[] Magic = "ORGB"u8.ToArray();

    public static byte[] Packet(uint deviceId, uint id, ReadOnlySpan<byte> payload = default)
    {
        var p = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(p, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(p.AsSpan(HeaderSize));
        return p;
    }

    /// <summary>Reads a header. Returns false if the magic is wrong or the size is implausible.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> header, out uint deviceId, out uint id, out int size)
    {
        deviceId = id = 0;
        size = 0;
        if (header.Length < HeaderSize || !header[..4].SequenceEqual(Magic)) return false;
        deviceId = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        id = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var s = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (s > MaxPacketSize) return false;
        size = (int)s;
        return true;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    public static byte[] ProtocolVersionRequest(int clientMax) => Packet(0, RequestProtocolVersion, U32((uint)clientMax));

    public static byte[] ClientName(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        return Packet(0, SetClientName, bytes);
    }

    public static byte[] ControllerCountRequest() => Packet(0, RequestControllerCount);

    public static byte[] ControllerDataRequest(int index, int version) => Packet((uint)index, RequestControllerData, U32((uint)version));

    public static byte[] UpdateLedsPacket(int device, IReadOnlyList<uint> colors)
    {
        var w = new Writer();
        w.U32(0); // data_size placeholder (must equal the payload size, itself included)
        w.U16((ushort)colors.Count);
        foreach (var c in colors) w.U32(c & 0x00FFFFFF);
        return Packet((uint)device, UpdateLeds, w.Finish());
    }

    public static byte[] UpdateZoneLedsPacket(int device, int zone, IReadOnlyList<uint> colors)
    {
        var w = new Writer();
        w.U32(0);
        w.U32((uint)zone);
        w.U16((ushort)colors.Count);
        foreach (var c in colors) w.U32(c & 0x00FFFFFF);
        return Packet((uint)device, UpdateZoneLeds, w.Finish());
    }

    public static byte[] UpdateModePacket(int device, int modeIndex, RgbMode mode, int version)
    {
        var w = new Writer();
        w.U32(0);
        w.I32(modeIndex);
        WriteMode(w, mode, version);
        return Packet((uint)device, UpdateMode, w.Finish());
    }

    private static void WriteMode(Writer w, RgbMode m, int version)
    {
        w.String(m.Name);
        w.I32(m.Value);
        w.U32(m.Flags);
        w.U32(m.SpeedMin);
        w.U32(m.SpeedMax);
        if (version >= 3)
        {
            w.U32(m.BrightnessMin);
            w.U32(m.BrightnessMax);
        }
        w.U32(m.ColorsMin);
        w.U32(m.ColorsMax);
        w.U32(m.Speed);
        if (version >= 3) w.U32(m.Brightness);
        w.U32(m.Direction);
        w.U32(m.ColorMode);
        w.U16((ushort)m.Colors.Count);
        foreach (var c in m.Colors) w.U32(c & 0x00FFFFFF);
    }

    /// <summary>Parses the reply to REQUEST_CONTROLLER_DATA for protocol 3..5. Throws <see cref="InvalidDataException"/> on a malformed block.</summary>
    public static RgbController ParseController(ReadOnlySpan<byte> payload, int version, int index)
    {
        if (version is < MinVersion or > MaxVersion) throw new ArgumentOutOfRangeException(nameof(version));
        var r = new Reader(payload);
        r.U32(); // data_size
        var c = new RgbController { Index = index, Type = r.I32(), Name = r.String(), Vendor = r.String(), Description = r.String(),
            Version = r.String(), Serial = r.String(), Location = r.String() };
        var modeCount = r.U16();
        c.ActiveMode = r.I32();
        for (var i = 0; i < modeCount; i++)
        {
            var m = new RgbMode { Name = r.String(), Value = r.I32(), Flags = r.U32(), SpeedMin = r.U32(), SpeedMax = r.U32(),
                BrightnessMin = r.U32(), BrightnessMax = r.U32(), ColorsMin = r.U32(), ColorsMax = r.U32(), Speed = r.U32(),
                Brightness = r.U32(), Direction = r.U32(), ColorMode = r.U32() };
            var n = r.U16();
            for (var k = 0; k < n; k++) m.Colors.Add(r.U32() & 0x00FFFFFF);
            c.Modes.Add(m);
        }
        var zoneCount = r.U16();
        for (var i = 0; i < zoneCount; i++)
        {
            var z = new RgbZone { Name = r.String(), Type = r.U32(), LedsMin = r.U32(), LedsMax = r.U32(), LedsCount = r.U32() };
            var matrixBytes = r.U16(); // matrix block: height, width, then a LED number (or 0xFFFFFFFF) per cell
            if (matrixBytes >= 8)
            {
                var h = r.U32();
                var w = r.U32();
                var cells = (long)h * w;
                if (cells > 0 && cells <= 4096 && 8 + 4 * cells <= matrixBytes)
                {
                    z.MatrixHeight = (int)h;
                    z.MatrixWidth = (int)w;
                    z.Matrix = new uint[cells];
                    for (var k = 0; k < cells; k++) z.Matrix[k] = r.U32();
                    r.Skip((int)(matrixBytes - 8 - 4 * cells));
                }
                else r.Skip(matrixBytes - 8);
            }
            else r.Skip(matrixBytes);
            if (version >= 4)
            {
                var segments = r.U16();
                for (var s = 0; s < segments; s++)
                {
                    r.String();
                    r.Skip(12); // type, start index, led count
                }
            }
            if (version >= 5) z.Flags = r.U32();
            c.Zones.Add(z);
        }
        c.LedCount = r.U16();
        for (var i = 0; i < c.LedCount; i++)
        {
            r.String();
            r.U32(); // value (ignored)
        }
        var colorCount = r.U16();
        for (var i = 0; i < colorCount; i++) c.Colors.Add(r.U32() & 0x00FFFFFF);
        // Protocol 5 appends LED display names and controller flags; nothing we need.
        return c;
    }

    public static uint ColorFromHex(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6 || !uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            throw new FormatException("Colours look like #RRGGBB.");
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return r | (g << 8) | (b << 16);
    }

    public static string ColorToHex(uint color) => $"#{color & 0xFF:X2}{(color >> 8) & 0xFF:X2}{(color >> 16) & 0xFF:X2}";

    private sealed class Writer
    {
        private readonly MemoryStream _s = new();
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _s.Write(b); }
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _s.Write(b); }
        public void I32(int v) => U32(unchecked((uint)v));

        public void String(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            U16((ushort)(bytes.Length + 1));
            _s.Write(bytes);
            _s.WriteByte(0);
        }

        /// <summary>Returns the payload with its first u32 set to the total payload size (the server requires that).</summary>
        public byte[] Finish()
        {
            var bytes = _s.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
            return bytes;
        }
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _b;
        private int _i;

        public Reader(ReadOnlySpan<byte> b)
        {
            _b = b;
            _i = 0;
        }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _i + n > _b.Length) throw new InvalidDataException("OpenRGB sent a truncated or malformed device description.");
            var s = _b.Slice(_i, n);
            _i += n;
            return s;
        }

        public void Skip(int n) => Take(n);
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public string String()
        {
            var s = Take(U16());
            var end = s.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end >= 0 ? s[..end] : s);
        }
    }
}
