using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RigControl.Presence;

/// <summary>
/// The Discord application ids the cards use, by name. No id is written in the source: the build reads them from
/// <c>private/discord-ids.json</c> (not part of the public source) and puts them, encrypted, inside this dll; they are
/// decrypted in memory when first needed. A build made without that file has no ids of its own, and the person who
/// built it brings their own through settings.json (section <c>presence</c>, entry <c>appIds</c>).
///
/// Honest limit: the key is worked out by this same dll, so the encryption only keeps the ids out of the source and
/// out of a plain text search of the exe. It is obfuscation, not a secret: anyone who inspects the running app, or
/// their own Discord, can see them. A Discord application id is not a password; knowing one gives no access.
///
/// Names: <c>game</c>, <c>music</c>, <c>app</c> (the three main cards); one per streaming service (see
/// <see cref="ServiceNames"/>); <c>custom1</c> to <c>custom9</c> (cards custom statuses can borrow).
/// Rule: an id is never logged and never sent back over RPC.
/// </summary>
public sealed class AppIds
{
    public const string GameName = "game";
    public const string MusicName = "music";
    public const string AppName = "app";

    /// <summary>Streaming services that can have their own card ("Watching Netflix").</summary>
    public static readonly IReadOnlyList<string> ServiceNames = new[]
    {
        "netflix", "hulu", "disney", "primeVideo", "max", "crunchyroll", "paramount", "peacock", "appleTv",
    };

    private static readonly string[] PoolNames = Enumerable.Range(1, 9).Select(i => "custom" + i).ToArray();

    /// <summary>Every name an id can be given for.</summary>
    public static readonly IReadOnlyList<string> Names =
        new[] { GameName, MusicName, AppName }.Concat(ServiceNames).Concat(PoolNames).ToArray();

    private static readonly Regex IdRx = new(@"^[0-9]{17,20}\z", RegexOptions.CultureInvariant);
    private static readonly Regex IdInText = new(@"(?<![0-9])[0-9]{17,20}(?![0-9])", RegexOptions.CultureInvariant);

    /// <summary>No ids at all (a build without the private file, before the user adds their own).</summary>
    public static readonly AppIds Empty = new(new Dictionary<string, string>());

    private readonly Dictionary<string, string> _map;

    private AppIds(Dictionary<string, string> map)
    {
        _map = map;
        Pool = PoolNames.Where(map.ContainsKey).Select(n => map[n]).ToArray();
    }

    /// <summary>A Discord application id is 17 to 20 digits (0 to 9 only, nothing before or after).</summary>
    public static bool IsId(string? v) => v is not null && IdRx.IsMatch(v);

    /// <summary>The name as this class spells it ("primevideo" -> "primeVideo"), or null when there is no such name.</summary>
    public static string? NameOf(string? v)
    {
        v = (v ?? "").Trim();
        return Names.FirstOrDefault(n => n.Equals(v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Takes ids out of a text before it is logged or shown (Discord's own error messages, a mistyped name).</summary>
    public static string Scrub(string? text) => IdInText.Replace(text ?? "", "(id)");

    public string? Game => Get(GameName);
    public string? Music => Get(MusicName);
    public string? App => Get(AppName);

    /// <summary>Ids custom statuses can borrow, one card each.</summary>
    public IReadOnlyList<string> Pool { get; }

    /// <summary>The id for a name, or null when this build has none.</summary>
    public string? Get(string? name) => name is not null && _map.TryGetValue(name, out var id) ? id : null;

    /// <summary>True when at least one of the three main cards has an id: the status can show something of its own.</summary>
    public bool SetUp => Game is not null || Music is not null || App is not null;

    /// <summary>The main cards that have no id.</summary>
    public IReadOnlyList<string> MissingMain => new[] { GameName, MusicName, AppName }.Where(n => !_map.ContainsKey(n)).ToArray();

    /// <summary>The names that have an id (never the ids themselves).</summary>
    public IReadOnlyList<string> Have => Names.Where(_map.ContainsKey).ToArray();

    /// <summary>True when the id is one of these.</summary>
    public bool Contains(string? id) => id is not null && _map.ContainsValue(id);

    /// <summary>
    /// Builds a set from name/id pairs, keeping only what is valid: a known name, an id of 17 to 20 digits, and no
    /// id used twice (two cards on one id would overwrite each other). What was left out is returned with the reason,
    /// in plain words and without the id.
    /// </summary>
    public static AppIds From(IEnumerable<KeyValuePair<string, string>>? pairs, out IReadOnlyList<(string Name, string Why)> refused)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var no = new List<(string, string)>();
        foreach (var (rawName, rawId) in pairs ?? Array.Empty<KeyValuePair<string, string>>())
        {
            var id = (rawId ?? "").Trim();
            if (id == "") continue; // an empty entry means "none"
            var shown = Scrub(rawName is { Length: > 32 } ? rawName[..32] : rawName ?? "");
            var name = NameOf(rawName);
            if (name is null) no.Add((shown, "there is no card with that name"));
            else if (!IsId(id)) no.Add((name, "a Discord application id is 17 to 20 digits"));
            else if (map.ContainsKey(name)) no.Add((name, "it is there twice"));
            else if (map.ContainsValue(id)) no.Add((name, "another card already uses the same id, and each card needs its own"));
            else map[name] = id;
        }
        refused = no;
        return new AppIds(map);
    }

    /// <summary>These ids, with the other set's laid over them (the user's own win over the built-in ones).</summary>
    public AppIds With(AppIds over)
    {
        var map = new Dictionary<string, string>(_map, StringComparer.Ordinal);
        foreach (var (name, id) in over._map)
        {
            // An id may sit under one name only: the user's choice of name wins.
            foreach (var other in map.Where(p => p.Value == id && p.Key != name).Select(p => p.Key).ToList()) map.Remove(other);
            map[name] = id;
        }
        return new AppIds(map);
    }

    // ---------------------------------------------------------------- the ids built into this dll

    /// <summary>The embedded resource the build writes when private/discord-ids.json exists (see DiscordIds.targets).</summary>
    internal const string ResourceName = "RigControl.Presence.Ids";

    private static readonly Lazy<AppIds> Embedded = new(LoadEmbedded);
    private static AppIds? _forTests;

    /// <summary>The ids this build carries; <see cref="Empty"/> when it was built without the private file.</summary>
    public static AppIds BuiltIn => _forTests ?? Embedded.Value;

    /// <summary>Tests only: made-up ids in place of the built-in ones, for the whole test run.</summary>
    internal static void UseForTests(AppIds ids) => _forTests = ids;

    /// <summary>Reads and decrypts the embedded ids. Never throws: anything wrong means "no ids in this build".</summary>
    internal static AppIds LoadEmbedded()
    {
        try
        {
            using var s = typeof(AppIds).Assembly.GetManifestResourceStream(ResourceName);
            if (s is null) return Empty;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var text = Open(ms.ToArray());
            return text is null ? Empty : Parse(text);
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>The text inside the blob: one "name=id" per line.</summary>
    internal static AppIds Parse(string text)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) pairs.Add(new(line[..eq], line[(eq + 1)..]));
        }
        return From(pairs, out _);
    }

    internal static string ToText(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Concat(pairs.Select(p => p.Key + "=" + p.Value + "\n"));

    // The blob: [1 byte version = 1][12 bytes nonce][16 bytes tag][ciphertext], AES-256-GCM.
    // The key is put together from the pieces below at run time, so neither the key nor an id sits in the file as
    // one searchable piece. That is all it does: see the note at the top of this class.
    // DiscordIds.targets holds the same recipe for the build step; change both together (a test checks they agree).

    private const byte Version = 1;
    private const int NonceLen = 12, TagLen = 16;

    private static byte[] Key()
    {
        byte[] a = { 0x4f, 0xc2, 0x19, 0x7a, 0xe3, 0x08, 0xb5, 0x6d, 0x91, 0x2e, 0xd7, 0x40, 0xa8, 0x73, 0x1c, 0xf6 };
        var b = 0x6A09E667F3BCC908UL ^ 0x1F83D9ABFB41BD6BUL;
        var c = "ecneserP.lortnoCgiR".ToCharArray();
        Array.Reverse(c);
        var seed = new List<byte>(a);
        for (var i = 7; i >= 0; i--) seed.Add((byte)(b >> (8 * i)));
        seed.AddRange(Encoding.ASCII.GetBytes(c));
        return SHA256.HashData(seed.ToArray());
    }

    /// <summary>Encrypts a text the way the build step does (used by the tests to check both ways agree).</summary>
    internal static byte[] Seal(string text)
    {
        var key = Key();
        var plain = Encoding.UTF8.GetBytes(text);
        // The nonce comes from the text itself, so the same ids always give the same bytes (repeatable builds) and
        // different ids never share a nonce.
        var nonce = SHA256.HashData(key.Concat(plain).ToArray())[..NonceLen];
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLen];
        using var gcm = new AesGcm(key, TagLen);
        gcm.Encrypt(nonce, plain, cipher, tag);
        var blob = new byte[1 + NonceLen + TagLen + cipher.Length];
        blob[0] = Version;
        nonce.CopyTo(blob, 1);
        tag.CopyTo(blob, 1 + NonceLen);
        cipher.CopyTo(blob, 1 + NonceLen + TagLen);
        return blob;
    }

    /// <summary>Decrypts a blob, or returns null when it is not one (wrong version, cut short, changed).</summary>
    internal static string? Open(byte[] blob)
    {
        if (blob.Length < 1 + NonceLen + TagLen || blob[0] != Version) return null;
        try
        {
            var plain = new byte[blob.Length - 1 - NonceLen - TagLen];
            using var gcm = new AesGcm(Key(), TagLen);
            gcm.Decrypt(blob.AsSpan(1, NonceLen), blob.AsSpan(1 + NonceLen + TagLen), blob.AsSpan(1 + NonceLen, TagLen), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
