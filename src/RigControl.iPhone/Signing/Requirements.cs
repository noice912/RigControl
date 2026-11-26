using System.Text;

namespace RigControl.iPhone.Signing;

/// <summary>
/// The requirements blob (a RequirementSet, magic 0xfade0c01) holding the designated requirement in Apple's
/// compiled expression form. For a certificate issued by Apple's Worldwide Developer Relations CA, codesign writes:
///   identifier "&lt;id&gt;" and anchor apple generic and certificate leaf[subject.CN] = "&lt;leaf CN&gt;"
///   and certificate 1[field.1.2.840.113635.100.6.2.1] /* exists */
/// iOS checks this slot by its hash in the CodeDirectory; the expression itself matters to tools that compare
/// designated requirements (for example when a later version of the app replaces this one).
/// </summary>
internal static class Requirements
{
    private const uint KindExpression = 1;
    private const uint TypeDesignated = 3;

    // Opcodes and match types from Apple's Security framework (requirement.h).
    private const uint OpIdent = 2;
    private const uint OpAnd = 6;
    private const uint OpCertField = 11;
    private const uint OpCertGeneric = 14;
    private const uint OpAppleGenericAnchor = 15;
    private const uint MatchExists = 0;
    private const uint MatchEqual = 1;

    /// <summary>1.2.840.113635.100.6.2.1, the marker extension of the WWDR intermediate, as DER content bytes.</summary>
    private static readonly byte[] WwdrMarkerOid = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x63, 0x64, 0x06, 0x02, 0x01];

    /// <summary>An empty set: what codesign writes when there is no designated requirement.</summary>
    public static byte[] Empty()
    {
        var w = new BlobWriter();
        w.U32(CsMagic.RequirementSet);
        w.U32(12);
        w.U32(0);
        return w.ToArray();
    }

    public static byte[] Designated(string identifier, string? leafCommonName)
    {
        if (string.IsNullOrEmpty(leafCommonName))
            return Empty();

        var expr = new BlobWriter();
        expr.U32(KindExpression);
        expr.U32(OpAnd);
        expr.U32(OpIdent);
        Data(expr, Encoding.UTF8.GetBytes(identifier));
        expr.U32(OpAnd);
        expr.U32(OpAppleGenericAnchor);
        expr.U32(OpAnd);
        expr.U32(OpCertField);
        expr.I32(0);                                  // leaf
        Data(expr, "subject.CN"u8);
        expr.U32(MatchEqual);
        Data(expr, Encoding.UTF8.GetBytes(leafCommonName));
        expr.U32(OpCertGeneric);
        expr.I32(1);                                  // the certificate that issued the leaf
        Data(expr, WwdrMarkerOid);
        expr.U32(MatchExists);
        var requirement = CsMagic.Wrap(CsMagic.Requirement, expr.ToArray());

        var w = new BlobWriter();
        w.U32(CsMagic.RequirementSet);
        w.U32((uint)(20 + requirement.Length));
        w.U32(1);
        w.U32(TypeDesignated);
        w.U32(20);
        w.Bytes(requirement);
        return w.ToArray();
    }

    /// <summary>Length-prefixed bytes, padded to a multiple of four.</summary>
    private static void Data(BlobWriter w, ReadOnlySpan<byte> bytes)
    {
        w.U32((uint)bytes.Length);
        w.Bytes(bytes);
        w.Zeros((4 - bytes.Length % 4) % 4);
    }
}
