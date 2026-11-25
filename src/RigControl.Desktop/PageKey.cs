using System.Security.Cryptography;
using System.Text;

namespace RigControl.Desktop;

/// <summary>
/// How RigControl tells its own page from any other page that ends up in the window.
/// The window library hands over every message from whatever page is showing and cannot say which page sent it.
/// So a random value is made for each run and written into RigControl's own page as it is handed to the window.
/// The page puts it in front of every message it sends; a message without it is dropped unread.
/// Another page cannot learn the value: it is in no file, it changes every run, and a page from somewhere else is
/// not allowed to read RigControl's page. The value is never written to the log.
/// </summary>
internal sealed class PageKey
{
    /// <summary>The name of the meta tag in index.html that carries the value. bridge.ts reads the same name.</summary>
    public const string MetaName = "rigcontrol-page-key";

    /// <summary>How many characters the value has (32 random bytes as hex).</summary>
    public const int Length = 64;

    private readonly byte[] _bytes;

    public PageKey()
    {
        Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(Length / 2)).ToLowerInvariant();
        _bytes = Encoding.ASCII.GetBytes(Value);
    }

    public string Value { get; }

    /// <summary>
    /// True when <paramref name="message"/> starts with this run's value; <paramref name="json"/> is then the rest.
    /// The comparison takes the same time whatever the message holds.
    /// </summary>
    public bool TryOpen(string? message, out string json)
    {
        json = "";
        if (message is null || message.Length <= Length) return false;
        Span<byte> given = stackalloc byte[Length];
        for (var i = 0; i < Length; i++)
        {
            var c = message[i];
            given[i] = c < 128 ? (byte)c : (byte)0;
        }
        if (!CryptographicOperations.FixedTimeEquals(given, _bytes)) return false;
        json = message[Length..];
        return true;
    }
}
