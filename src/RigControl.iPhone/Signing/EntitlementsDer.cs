using System.Formats.Asn1;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Signing;

/// <summary>
/// The DER form of an entitlements property list (slot 7, blob magic 0xfade7172), which iOS 15 and later read
/// instead of the XML. Apple's layout:
///   [APPLICATION 16] { INTEGER 1, value }
///   dict   = [CONTEXT 16] { SEQUENCE { UTF8String key, value } ... } with keys sorted
///   array  = SEQUENCE { value ... }
///   string = UTF8String, integer = INTEGER, bool = BOOLEAN
/// Data, dates and reals have no DER form and are refused.
/// </summary>
internal static class EntitlementsDer
{
    private static readonly Asn1Tag Wrapper = new(TagClass.Application, 16, isConstructed: true);
    private static readonly Asn1Tag Dictionary = new(TagClass.ContextSpecific, 16, isConstructed: true);

    public static byte[] Encode(PlistDict entitlements)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        w.PushSequence(Wrapper);
        w.WriteInteger(1);
        WriteValue(w, entitlements, 0);
        w.PopSequence(Wrapper);
        return w.Encode();
    }

    public static byte[] Blob(PlistDict entitlements) => CsMagic.Wrap(CsMagic.EntitlementsDer, Encode(entitlements));

    private static void WriteValue(AsnWriter w, object value, int depth)
    {
        if (depth > 32)
            throw new InvalidDataException("The entitlements are nested too deeply.");
        switch (value)
        {
            case PlistDict dict:
                w.PushSequence(Dictionary);
                foreach (var key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    w.PushSequence();
                    w.WriteCharacterString(UniversalTagNumber.UTF8String, key);
                    WriteValue(w, dict[key], depth + 1);
                    w.PopSequence();
                }
                w.PopSequence(Dictionary);
                break;
            case List<object> list:
                w.PushSequence();
                foreach (var item in list)
                    WriteValue(w, item, depth + 1);
                w.PopSequence();
                break;
            case string s:
                w.WriteCharacterString(UniversalTagNumber.UTF8String, s);
                break;
            case bool b:
                w.WriteBoolean(b);
                break;
            case long l:
                w.WriteInteger(l);
                break;
            default:
                throw new InvalidDataException($"An entitlement value of type {value.GetType().Name} has no DER form.");
        }
    }
}
