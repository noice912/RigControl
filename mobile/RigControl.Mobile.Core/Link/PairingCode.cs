using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RigControl.Mobile.Core.Link;

/// <summary>
/// The code a PC shows for pairing: 20 characters of Crockford base32 (100 bits), shown as 5 groups of 4
/// (docs/protocols/link.md). Typing is forgiving the way Crockford base32 intends: lower case is fine, dashes and
/// spaces are ignored, O reads as 0 and I / L as 1.
/// </summary>
public sealed class PairingCode : IEquatable<PairingCode>
{
    public const int Length = 20;
    public const int GroupSize = 4;
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The 20 characters, upper case, no dashes: the form the key derivation uses.</summary>
    public string Value { get; }

    private PairingCode(string value) => Value = value;

    /// <summary>Reads a code as typed or scanned. False when it is not exactly 20 valid characters.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PairingCode? code)
    {
        code = null;
        if (text is null)
            return false;
        var clean = Normalize(text, out var bad);
        if (bad || clean.Length != Length)
            return false;
        code = new PairingCode(clean);
        return true;
    }

    /// <summary>
    /// The characters typed so far in the normal form (for showing progress while typing); characters that can never
    /// be in a code are dropped and reported in <paramref name="hadInvalid"/>.
    /// </summary>
    public static string Normalize(string text, out bool hadInvalid)
    {
        hadInvalid = false;
        var sb = new StringBuilder(Length);
        foreach (var raw in text)
        {
            if (raw is '-' or ' ' or '\t' or ' ')
                continue;
            var c = char.ToUpperInvariant(raw);
            c = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (Alphabet.Contains(c))
                sb.Append(c);
            else
                hadInvalid = true;
        }
        return sb.ToString();
    }

    /// <summary>Groups of 4 joined by dashes, as the PC shows it (also works for a partly typed code).</summary>
    public static string Group(string normalized)
    {
        var sb = new StringBuilder(normalized.Length + normalized.Length / GroupSize);
        for (var i = 0; i < normalized.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0)
                sb.Append('-');
            sb.Append(normalized[i]);
        }
        return sb.ToString();
    }

    public override string ToString() => Group(Value);
    public bool Equals(PairingCode? other) => other is not null && other.Value == Value;
    public override bool Equals(object? obj) => Equals(obj as PairingCode);
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
}
