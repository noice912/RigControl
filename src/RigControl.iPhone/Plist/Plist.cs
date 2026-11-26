using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;

namespace RigControl.iPhone.Plist;

/// <summary>
/// Apple property lists: XML read/write and binary ("bplist00") read. Values map to .NET types:
/// dict = <see cref="PlistDict"/> (keeps key order), array = <see cref="List{Object}"/>, string, integer = long,
/// real = double, true/false = bool, data = byte[], date = DateTime (UTC).
/// </summary>
public static class PlistFile
{
    private const int MaxDepth = 64;

    /// <summary>Reads XML or binary, whichever the bytes are.</summary>
    public static object Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("bplist00"u8))
            return new BinaryReader(bytes.ToArray()).Read();
        return ParseXml(bytes);
    }

    public static PlistDict ParseDict(ReadOnlySpan<byte> bytes) =>
        Parse(bytes) as PlistDict ?? throw new FormatException("The property list is not a dictionary.");

    public static object ParseXml(ReadOnlySpan<byte> bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersFromEntities = 0,
        };
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = XmlReader.Create(stream, settings);
        reader.MoveToContent();
        if (reader.Name != "plist")
            throw new FormatException("Not a property list.");
        reader.Read();
        reader.MoveToContent();
        return ReadXmlValue(reader, 0);
    }

    private static object ReadXmlValue(XmlReader r, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("The property list is nested too deeply.");
        if (r.NodeType != XmlNodeType.Element)
            throw new FormatException("Expected a value in the property list.");
        var name = r.Name;
        switch (name)
        {
            case "dict":
            {
                var dict = new PlistDict();
                if (r.IsEmptyElement) { r.Read(); return dict; }
                r.Read();
                while (r.NodeType != XmlNodeType.EndElement)
                {
                    if (r.Name != "key")
                        throw new FormatException("Expected <key> in a dict.");
                    var key = r.ReadElementContentAsString();
                    r.MoveToContent();
                    dict[key] = ReadXmlValue(r, depth + 1);
                    r.MoveToContent();
                }
                r.Read();
                return dict;
            }
            case "array":
            {
                var list = new List<object>();
                if (r.IsEmptyElement) { r.Read(); return list; }
                r.Read();
                while (r.NodeType != XmlNodeType.EndElement)
                {
                    list.Add(ReadXmlValue(r, depth + 1));
                    r.MoveToContent();
                }
                r.Read();
                return list;
            }
            case "true": r.Read(); return true;
            case "false": r.Read(); return false;
            case "string": return r.IsEmptyElement ? Skip(r, "") : r.ReadElementContentAsString();
            case "integer":
            {
                var s = r.ReadElementContentAsString().Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return long.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                // Unsigned 64-bit values above long.MaxValue (rare) keep their bits.
                return s.StartsWith('-') ? long.Parse(s, CultureInfo.InvariantCulture)
                    : unchecked((long)ulong.Parse(s, CultureInfo.InvariantCulture));
            }
            case "real": return double.Parse(r.ReadElementContentAsString(), CultureInfo.InvariantCulture);
            case "data":
            {
                if (r.IsEmptyElement) return Skip(r, Array.Empty<byte>());
                var text = r.ReadElementContentAsString();
                return Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
            }
            case "date":
                return DateTime.Parse(r.ReadElementContentAsString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            default:
                throw new FormatException($"Unknown property list element <{name}>.");
        }
    }

    private static T Skip<T>(XmlReader r, T value)
    {
        r.Read();
        return value;
    }

    /// <summary>Writes the value as an XML property list (UTF-8, Apple's DOCTYPE, tab indents).</summary>
    public static byte[] ToXml(object value)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        WriteXml(sb, value, 0);
        sb.Append("</plist>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void WriteXml(StringBuilder sb, object value, int indent)
    {
        void Pad() => sb.Append('\t', indent);
        Pad();
        switch (value)
        {
            case PlistDict d:
                if (d.Count == 0) { sb.Append("<dict/>\n"); return; }
                sb.Append("<dict>\n");
                foreach (var (k, v) in d)
                {
                    sb.Append('\t', indent + 1).Append("<key>").Append(Escape(k)).Append("</key>\n");
                    WriteXml(sb, v, indent + 1);
                }
                Pad(); sb.Append("</dict>\n");
                return;
            case IEnumerable<object> list when value is not string:
            {
                var items = list.ToList();
                if (items.Count == 0) { sb.Append("<array/>\n"); return; }
                sb.Append("<array>\n");
                foreach (var v in items)
                    WriteXml(sb, v, indent + 1);
                Pad(); sb.Append("</array>\n");
                return;
            }
            case string s: sb.Append("<string>").Append(Escape(s)).Append("</string>\n"); return;
            case bool b: sb.Append(b ? "<true/>\n" : "<false/>\n"); return;
            case byte[] bytes: sb.Append("<data>").Append(Convert.ToBase64String(bytes)).Append("</data>\n"); return;
            case DateTime dt:
                sb.Append("<date>").Append(dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("</date>\n");
                return;
            case double dbl: sb.Append("<real>").Append(dbl.ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n"); return;
            case float f: sb.Append("<real>").Append(((double)f).ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n"); return;
            case long or int or short or byte or uint or ushort or sbyte:
                sb.Append("<integer>").Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)).Append("</integer>\n");
                return;
            case ulong ul:
                sb.Append("<integer>").Append(ul.ToString(CultureInfo.InvariantCulture)).Append("</integer>\n");
                return;
            default:
                throw new ArgumentException($"A {value.GetType().Name} can't go in a property list.");
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Binary plist reader (bplist00). Offsets and counts are bounds-checked; cycles are refused. Each object is read
    /// once and reused wherever it is referenced again, and the total number of references followed is capped, so a
    /// small file can't make the reader do huge amounts of work (shared-reference expansion).
    /// </summary>
    private sealed class BinaryReader(byte[] data)
    {
        private int _offsetSize, _refSize;
        private long[] _offsets = [];
        private readonly HashSet<long> _active = [];
        private readonly Dictionary<long, object> _done = [];
        private long _references;
        private const long MaxReferences = 1_000_000;

        public object Read()
        {
            if (data.Length < 8 + 32)
                throw new FormatException("The binary property list is too short.");
            var t = data.AsSpan(data.Length - 32);
            _offsetSize = t[6];
            _refSize = t[7];
            var count = BinaryPrimitives.ReadUInt64BigEndian(t[8..]);
            var top = BinaryPrimitives.ReadUInt64BigEndian(t[16..]);
            var tableAt = BinaryPrimitives.ReadUInt64BigEndian(t[24..]);
            if (_offsetSize is < 1 or > 8 || _refSize is < 1 or > 8 || count == 0 || count > (ulong)data.Length
                || top >= count || tableAt + count * (ulong)_offsetSize > (ulong)data.Length - 32)
                throw new FormatException("The binary property list is damaged.");
            _offsets = new long[count];
            for (var i = 0; i < (int)count; i++)
                _offsets[i] = (long)ReadSized((int)tableAt + i * _offsetSize, _offsetSize);
            return ReadObject((long)top, 0);
        }

        private ulong ReadSized(int at, int size)
        {
            ulong v = 0;
            for (var i = 0; i < size; i++)
                v = (v << 8) | data[at + i];
            return v;
        }

        private object ReadObject(long index, int depth)
        {
            if (index < 0 || index >= _offsets.Length || depth > MaxDepth)
                throw new FormatException("The binary property list is damaged.");
            if (++_references > MaxReferences)
                throw new FormatException("The binary property list is too complex.");
            if (_done.TryGetValue(index, out var seen))
                return seen;
            if (!_active.Add(index))
                throw new FormatException("The binary property list refers to itself.");
            try
            {
                var value = ReadAt(checked((int)_offsets[index]), depth);
                _done[index] = value;
                return value;
            }
            finally
            {
                _active.Remove(index);
            }
        }

        private (long Count, int Start) Length(int at, int low)
        {
            if (low != 0xF)
                return (low, at + 1);
            var marker = data[at + 1];
            if (marker >> 4 != 1)
                throw new FormatException("The binary property list is damaged.");
            var size = 1 << (marker & 0xF);
            return ((long)ReadSized(at + 2, size), at + 2 + size);
        }

        private object ReadAt(int at, int depth)
        {
            if (at < 8 || at >= data.Length - 32)
                throw new FormatException("The binary property list is damaged.");
            var marker = data[at];
            int high = marker >> 4, low = marker & 0xF;
            switch (high)
            {
                case 0x0:
                    return low switch { 0x8 => false, 0x9 => true, _ => throw new FormatException("Unsupported binary plist value.") };
                case 0x1:
                {
                    var size = 1 << low;
                    if (size > 16) throw new FormatException("The binary property list is damaged.");
                    if (size == 16) return (long)ReadSized(at + 9, 8);
                    var v = ReadSized(at + 1, size);
                    return size == 8 ? unchecked((long)v) : (long)v;
                }
                case 0x2:
                    return low == 2 ? BitConverter.Int32BitsToSingle((int)ReadSized(at + 1, 4))
                        : BitConverter.Int64BitsToDouble((long)ReadSized(at + 1, 8));
                case 0x3:
                {
                    var secs = BitConverter.Int64BitsToDouble((long)ReadSized(at + 1, 8));
                    return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs);
                }
                case 0x4:
                {
                    var (n, start) = Length(at, low);
                    Check(start, n);
                    return data.AsSpan(start, (int)n).ToArray();
                }
                case 0x5:
                {
                    var (n, start) = Length(at, low);
                    Check(start, n);
                    return Encoding.ASCII.GetString(data, start, (int)n);
                }
                case 0x6:
                {
                    var (n, start) = Length(at, low);
                    Check(start, n * 2);
                    return Encoding.BigEndianUnicode.GetString(data, start, (int)n * 2);
                }
                case 0x8:
                    return (long)ReadSized(at + 1, low + 1);
                case 0xA:
                {
                    var (n, start) = Length(at, low);
                    Check(start, n * _refSize);
                    var list = new List<object>((int)n);
                    for (var i = 0; i < n; i++)
                        list.Add(ReadObject((long)ReadSized(start + i * _refSize, _refSize), depth + 1));
                    return list;
                }
                case 0xD:
                {
                    var (n, start) = Length(at, low);
                    Check(start, n * 2 * _refSize);
                    var dict = new PlistDict();
                    for (var i = 0; i < n; i++)
                    {
                        var key = ReadObject((long)ReadSized(start + i * _refSize, _refSize), depth + 1) as string
                            ?? throw new FormatException("A binary plist dict key is not a string.");
                        dict[key] = ReadObject((long)ReadSized(start + (int)(n + i) * _refSize, _refSize), depth + 1);
                    }
                    return dict;
                }
                default:
                    throw new FormatException($"Unsupported binary plist value type {high:X}.");
            }
        }

        private void Check(int start, long length)
        {
            if (length < 0 || start + length > data.Length - 32)
                throw new FormatException("The binary property list is damaged.");
        }
    }
}

/// <summary>A property list dictionary: string keys, insertion order kept, typed getters.</summary>
public sealed class PlistDict : Dictionary<string, object>
{
    public PlistDict() : base(StringComparer.Ordinal) { }

    public string? String(string key) => TryGetValue(key, out var v) ? v as string : null;
    public long? Long(string key) => TryGetValue(key, out var v) && v is long l ? l : null;
    public bool? Bool(string key) => TryGetValue(key, out var v) && v is bool b ? b : null;
    public byte[]? Data(string key) => TryGetValue(key, out var v) ? v as byte[] : null;
    public PlistDict? Dict(string key) => TryGetValue(key, out var v) ? v as PlistDict : null;
    public List<object>? Array(string key) => TryGetValue(key, out var v) ? v as List<object> : null;
    public DateTime? Date(string key) => TryGetValue(key, out var v) && v is DateTime d ? d : null;
}
