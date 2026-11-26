using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigControl.Mobile.Core.Link;

/// <summary>
/// A linked PC as the phone stores it (no secrets: the phone's own private keys for it are kept apart, in the
/// protected store). Field names are the stored JSON; keep them stable.
/// </summary>
internal sealed class StoredPc
{
    /// <summary>The PC's id (its room on the relay).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The PC's signing public key, base64url SPKI.</summary>
    public string SigningKey { get; set; } = "";
    /// <summary>The PC's agreement public key, base64url SPKI.</summary>
    public string AgreementKey { get; set; } = "";
    /// <summary>The relay base address it was paired through ("wss://host/").</summary>
    public string Relay { get; set; } = "";
    /// <summary>The pairing's epoch (part of the session key).</summary>
    public string Epoch { get; set; } = "";
    /// <summary>This phone's id for this PC (the phone uses one key pair per PC).</summary>
    public string PhoneId { get; set; } = "";
    /// <summary>The last counter accepted from the PC (replay protection across restarts).</summary>
    public long LastIn { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
    public DateTimeOffset? LastSeen { get; set; }

    public global::RigControl.Link.LinkedPc ToLibrary() => new(Id, Name, SigningKey, AgreementKey, Relay, Epoch);

    /// <summary>The relay as shown to people (host, with the port when it isn't 443).</summary>
    public string RelayHost =>
        global::RigControl.Link.LinkEndpoints.ParseRelay(Relay) is { } u ? global::RigControl.Link.LinkEndpoints.HostForQr(u) : Relay;

    public StoredPc Copy() => (StoredPc)MemberwiseClone();

    public static string Serialize(IEnumerable<StoredPc> pcs) =>
        JsonSerializer.Serialize(pcs.ToList(), StoredPcJson.Default.ListStoredPc);

    /// <summary>Reads a saved list; entries that are not usable are left out (never throws).</summary>
    public static List<StoredPc> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var list = JsonSerializer.Deserialize(json, StoredPcJson.Default.ListStoredPc) ?? [];
            return list.Where(p => p is not null && p.Id.Length > 0 && p.AgreementKey.Length > 0 && p.Epoch.Length > 0
                    && global::RigControl.Link.LinkEndpoints.ParseRelay(p.Relay) is { Scheme: "wss" })
                .GroupBy(p => p.Id, StringComparer.Ordinal).Select(g => g.Last()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Source-generated JSON for the stored list (release builds are trimmed, so no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<StoredPc>))]
internal sealed partial class StoredPcJson : JsonSerializerContext;
