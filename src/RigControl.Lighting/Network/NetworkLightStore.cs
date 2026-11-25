using System.Text;
using CoreSecrets = RigControl.Core.Platform.ISecretProtector;

namespace RigControl.Lighting.Network;

/// <summary>A paired network light as it is saved (settings section "lighting", list "networkLights").</summary>
public sealed class StoredNetworkLight
{
    public string Brand { get; set; } = "";
    /// <summary>The device's own lasting id (a serial number or MAC address), never its network address.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Address { get; set; } = "";
    public string PairedAt { get; set; } = "";
    /// <summary>Plain facts the provider asked to remember. Never a secret.</summary>
    public Dictionary<string, string> Data { get; set; } = new();
    /// <summary>Pairing keys by name, each protected for this Windows user (base64). Useless on another account or PC.</summary>
    public Dictionary<string, string> ProtectedSecrets { get; set; } = new();
    /// <summary>
    /// What the light was doing before RigControl first changed it by hand (the provider's own words, never a
    /// secret), so "Give back" can put it back, even after a restart. Null = RigControl hasn't taken it over.
    /// </summary>
    public string? Before { get; set; }
}

/// <summary>Protects a pairing key for the user who is signed in, as text that fits the settings file.</summary>
public interface ISecretProtector
{
    string Protect(string secret, string purpose);
    /// <summary>The secret, or null when it can't be read (another account, another PC, damaged).</summary>
    string? Unprotect(string stored, string purpose);
}

/// <summary>
/// Pairing keys kept by the system's own means (<c>ModuleContext.Platform.Secrets</c>), stored as base64 text.
/// On Windows that is data protection for the current user (DPAPI) with the purpose's UTF-8 bytes as the extra
/// bytes: exactly the call this code always made, so keys saved by older versions still read. On Linux and macOS
/// it is the key file RigControl keeps for its user.
/// </summary>
public sealed class PlatformProtector : ISecretProtector
{
    private readonly CoreSecrets _inner;

    public PlatformProtector(CoreSecrets inner) => _inner = inner;

    public string Protect(string secret, string purpose) =>
        Convert.ToBase64String(_inner.Protect(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(purpose)));

    public string? Unprotect(string stored, string purpose)
    {
        if (stored == "") return null;
        try
        {
            return Encoding.UTF8.GetString(_inner.Unprotect(Convert.FromBase64String(stored), Encoding.UTF8.GetBytes(purpose)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What a store uses until the module has started and has the system's own protector. On Windows that is the
    /// same DPAPI call. Elsewhere nothing can be read or kept yet (the key file's folder isn't known before the start).
    /// </summary>
    internal static ISecretProtector BeforeStart() =>
        OperatingSystem.IsWindows() ? new PlatformProtector(new RigControl.Core.Platform.DpapiSecretProtector()) : new NotYet();

    private sealed class NotYet : ISecretProtector
    {
        public string Protect(string secret, string purpose) =>
            throw new InvalidOperationException("Lighting hasn't started yet, so a pairing key can't be kept.");

        public string? Unprotect(string stored, string purpose) => null;
    }
}

/// <summary>
/// The paired network lights of the user who runs RigControl. They live in that user's own settings file;
/// pairing keys are protected for that user and are never logged or shown. A Nanoleaf paired before this store
/// existed keeps its old place in the settings (<see cref="LightingSettings.Nanoleaf"/>) and its old layout key.
/// </summary>
public sealed class NetworkLightStore
{
    public const string NanoleafBrand = "nanoleaf";
    /// <summary>Layout keys of network lights start with this.</summary>
    public const string KeyPrefix = "net|";

    private readonly Func<LightingSettings> _settings;
    private readonly Action _save;
    private readonly object _gate;
    private ISecretProtector _protector;
    private IReadOnlyList<PairedLight>? _cache;
    private int _version;

    /// <param name="gate">The lock that guards the settings object (the lighting module's own).</param>
    public NetworkLightStore(Func<LightingSettings> settings, Action save, object gate, ISecretProtector? protector = null)
    {
        _settings = settings;
        _save = save;
        _gate = gate;
        _protector = protector ?? PlatformProtector.BeforeStart();
    }

    /// <summary>Hands the store the system's protector once the module has it. Keys are read afresh with it.</summary>
    internal void UseProtector(ISecretProtector protector)
    {
        lock (_gate)
        {
            _protector = protector;
            _cache = null;
        }
    }

    /// <summary>Goes up whenever a light is paired, forgotten or moved.</summary>
    public int Version => Volatile.Read(ref _version);

    public static string KeyFor(string brand, string id) => $"{KeyPrefix}{brand}|{id}";

    private static string Purpose(string brand, string name) => $"RigControl.NetworkLight.{brand}.{name}";

    /// <summary>An id that is safe inside a layout key (at most 200 characters).</summary>
    internal static string TidyId(string id)
    {
        // No control characters either: the id ends up in the settings file, on the page and in log lines.
        return DeviceText.Tidy(id, 200).Replace('|', '_');
    }

    public IReadOnlyList<PairedLight> All()
    {
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            var s = _settings();
            var list = new List<PairedLight>();
            if (s.Nanoleaf is { } n)
            {
                var secrets = new Dictionary<string, string>();
                if (n.Token(_protector) is { } token) secrets["token"] = token;
                list.Add(new PairedLight(NanoleafBrand, n.Serial, n.Name, "", n.Ip, $"nanoleaf|{n.Name}|{n.Serial}", null, secrets));
            }
            foreach (var l in s.NetworkLights)
            {
                if (l.Brand == "" || l.Id == "" || l.Brand == NanoleafBrand) continue;
                var secrets = new Dictionary<string, string>();
                foreach (var (name, stored) in l.ProtectedSecrets)
                    if (_protector.Unprotect(stored, Purpose(l.Brand, name)) is { } plain) secrets[name] = plain;
                list.Add(new PairedLight(l.Brand, l.Id, l.Name, l.Model, l.Address, KeyFor(l.Brand, l.Id), new Dictionary<string, string>(l.Data), secrets));
            }
            return _cache = list;
        }
    }

    public PairedLight? Find(string key) => All().FirstOrDefault(l => l.LayoutKey == key);

    public PairedLight? Find(string brand, string id) => All().FirstOrDefault(l => l.Brand == brand && l.Id == id);

    /// <summary>Saves a pairing (replacing an earlier one of the same light) and returns it as providers see it.</summary>
    public PairedLight Save(string brand, PairResult r)
    {
        var id = TidyId(r.Id);
        if (id == "") throw new ArgumentException("A paired light needs an id.");
        lock (_gate)
        {
            var s = _settings();
            if (brand == NanoleafBrand)
            {
                // The format a Nanoleaf has always been saved in; one Nanoleaf at a time, as before.
                var n = new NanoleafSettings { Ip = r.Address, Name = DeviceText.Tidy(r.Name), Serial = id };
                n.SetToken(r.Secrets.TryGetValue("token", out var token) ? token : "", _protector);
                s.Nanoleaf = n;
            }
            else
            {
                s.NetworkLights.RemoveAll(l => l.Brand == brand && l.Id == id);
                s.NetworkLights.Add(new StoredNetworkLight
                {
                    Brand = brand, Id = id, Name = DeviceText.Tidy(r.Name), Model = DeviceText.Tidy(r.Model), Address = LanAddress.Normalise(r.Address),
                    PairedAt = DateTimeOffset.Now.ToString("o"),
                    Data = new Dictionary<string, string>(r.Data),
                    ProtectedSecrets = r.Secrets.Where(kv => kv.Value.Length > 0)
                        .ToDictionary(kv => kv.Key, kv => _protector.Protect(kv.Value, Purpose(brand, kv.Key))),
                });
            }
            Changed();
            return Find(brand, id)!;
        }
    }

    /// <summary>Forgets a light and its keys. Its box stays on the layout, so pairing it again puts it back in place.</summary>
    public bool Forget(string key)
    {
        lock (_gate)
        {
            var light = Find(key);
            if (light is null) return false;
            var s = _settings();
            if (light.Brand == NanoleafBrand) s.Nanoleaf = null;
            else s.NetworkLights.RemoveAll(l => l.Brand == light.Brand && l.Id == light.Id);
            Changed();
            return true;
        }
    }

    /// <summary>What the light did before RigControl took it over (see <see cref="StoredNetworkLight.Before"/>); null when it hasn't.</summary>
    public string? Before(string key)
    {
        lock (_gate)
        {
            var s = _settings();
            if (s.Nanoleaf is { } n && $"nanoleaf|{n.Name}|{n.Serial}" == key) return n.Before;
            return s.NetworkLights.FirstOrDefault(l => l.Brand != NanoleafBrand && KeyFor(l.Brand, l.Id) == key)?.Before;
        }
    }

    /// <summary>Keeps (or, with null, drops) what the light did before RigControl took it over. False when it isn't paired.</summary>
    public bool SetBefore(string key, string? before)
    {
        lock (_gate)
        {
            var s = _settings();
            if (s.Nanoleaf is { } n && $"nanoleaf|{n.Name}|{n.Serial}" == key) n.Before = before;
            else if (s.NetworkLights.FirstOrDefault(l => l.Brand != NanoleafBrand && KeyFor(l.Brand, l.Id) == key) is { } stored) stored.Before = before;
            else return false;
            // Not a change to which lights are paired: the cache and the version stay as they are.
            _save();
            return true;
        }
    }

    /// <summary>
    /// Notes that a light now answers at another address. Refused for a light that holds a pairing key: its key stays
    /// tied to the address it was paired at, so that light is paired again instead.
    /// </summary>
    public bool Readdress(string key, string address)
    {
        if (!LanAddress.IsPrivate(address)) return false;
        lock (_gate)
        {
            var stored = _settings().NetworkLights.FirstOrDefault(l => KeyFor(l.Brand, l.Id) == key);
            if (stored is null || stored.ProtectedSecrets.Count > 0 || LanAddress.Same(stored.Address, address)) return false;
            stored.Address = LanAddress.Normalise(address);
            Changed();
            return true;
        }
    }

    private void Changed()
    {
        _cache = null;
        Interlocked.Increment(ref _version);
        _save();
    }
}
