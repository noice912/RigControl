using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RigControl.Updates;

/// <summary>
/// mobile.json, the signed description of the phone app (.ipa) a PC release carries:
/// {"app":"RigControl Mobile","version":"1.2.3","file":"RigControl-Mobile-v1.2.3.ipa","sha256":"&lt;hex&gt;","size":&lt;bytes&gt;}.
/// Signed with the same update key as update.json (mobile.json.sig). The "app" field is inside the signed bytes and
/// update.json has none, so one file's signature can never be passed off as the other's.
/// The PC's iPhone installer only installs an .ipa whose SHA-256 and size match a correctly signed mobile.json.
/// </summary>
public sealed record MobileManifest(AppVersion Version, string File, string Sha256, long Size)
{
    public const string AppName = "RigControl Mobile";
    public const string FileName = "mobile.json";
    public const string SignatureFileName = "mobile.json.sig";
    public const long MaxSize = 512L * 1024 * 1024;

    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static string IpaFileName(AppVersion version) => "RigControl-Mobile-v" + version + ".ipa";

    public byte[] ToJsonBytes()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("app", AppName);
            w.WriteString("version", Version.ToString());
            w.WriteString("file", File);
            w.WriteString("sha256", Sha256);
            w.WriteNumber("size", Size);
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Checks the signature first, then reads the text exactly. Throws <see cref="UpdateException"/>.</summary>
    public static MobileManifest ParseSigned(byte[] json, string? signatureBase64, string publicKeyBase64)
    {
        if (!EcSignature.VerifyBase64(json, signatureBase64, publicKeyBase64))
            throw new UpdateException("The phone app's information is not signed with RigControl's update key, so it was ignored.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("it is not a JSON object");
            if (String(root, "app") != AppName) throw Bad("it is not about the phone app");
            if (!AppVersion.TryParse(String(root, "version"), out var version)) throw Bad("the version is not valid");
            var file = String(root, "file");
            if (file != IpaFileName(version)) throw Bad("the file name does not match the version");
            var sha = String(root, "sha256");
            if (!Hex64.IsMatch(sha)) throw Bad("the SHA-256 is not 64 lower-case hex digits");
            if (!root.TryGetProperty("size", out var sizeEl) || !sizeEl.TryGetInt64(out var size) || size <= 0 || size > MaxSize)
                throw Bad("the size is missing or out of range");
            return new MobileManifest(version, file, sha, size);
        }
        catch (JsonException ex)
        {
            throw new UpdateException("The phone app's information is not valid JSON.", ex);
        }
    }

    /// <summary>True when the file at <paramref name="path"/> is exactly the one this manifest describes.</summary>
    public bool Matches(string path) =>
        new FileInfo(path).Length == Size && UpdateManifest.HashFile(path) == Sha256;

    /// <summary>
    /// Copies the .ipa into <paramref name="outFolder"/> under its release name and writes mobile.json and its
    /// signature next to it (the signature is checked with the key's public half before returning).
    /// </summary>
    public static (string Ipa, string Manifest, string Signature) Make(string ipaPath, AppVersion version, ECDsa key, string outFolder)
    {
        if (!System.IO.File.Exists(ipaPath)) throw new UpdateException("The phone app file was not found: " + ipaPath);
        Directory.CreateDirectory(outFolder);
        var ipa = Path.Combine(outFolder, IpaFileName(version));
        if (!string.Equals(Path.GetFullPath(ipaPath), Path.GetFullPath(ipa), StringComparison.OrdinalIgnoreCase))
            System.IO.File.Copy(ipaPath, ipa, overwrite: true);
        var size = new FileInfo(ipa).Length;
        if (size <= 0 || size > MaxSize) throw new UpdateException("The phone app file is empty or too large.");
        var manifest = new MobileManifest(version, IpaFileName(version), UpdateManifest.HashFile(ipa), size);
        var json = manifest.ToJsonBytes();
        var signature = EcSignature.Sign(json, key);
        if (!EcSignature.VerifyBase64(json, signature, EcSignature.PublicKeyBase64(key)))
            throw new UpdateException("The signature just made does not check out; nothing was written.");
        var jsonPath = Path.Combine(outFolder, FileName);
        var sigPath = Path.Combine(outFolder, SignatureFileName);
        System.IO.File.WriteAllBytes(jsonPath, json);
        System.IO.File.WriteAllText(sigPath, signature + "\n", new UTF8Encoding(false));
        return (ipa, jsonPath, sigPath);
    }

    private static string String(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()! : throw Bad($"\"{name}\" is missing");

    private static UpdateException Bad(string why) => new("The phone app's information is not usable: " + why + ".");
}
