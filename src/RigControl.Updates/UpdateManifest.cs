using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RigControl.Updates;

/// <summary>Something about an update or a release was wrong; the message is fit to show the user.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// update.json, the signed description of one release:
/// {"version":"1.1.2","file":"RigControl-Setup-v1.1.2.exe","sha256":"&lt;hex&gt;","size":&lt;bytes&gt;,"published":"&lt;iso&gt;"}.
/// The signature (update.json.sig) covers the exact bytes of the file, so it is always checked BEFORE the text is read.
/// </summary>
public sealed record UpdateManifest(AppVersion Version, string File, string Sha256, long Size, DateTimeOffset Published)
{
    public const string FileName = "update.json";
    public const string SignatureFileName = "update.json.sig";
    public const string SumsFileName = "SHA256SUMS.txt";

    /// <summary>The largest setup file RigControl will download (the real one is far smaller).</summary>
    public const long MaxSize = 1024L * 1024 * 1024;

    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>The bytes that get signed. Always written the same way: these fields, in this order, UTF-8 without BOM.</summary>
    public byte[] ToJsonBytes()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("version", Version.ToString());
            w.WriteString("file", File);
            w.WriteString("sha256", Sha256);
            w.WriteNumber("size", Size);
            w.WriteString("published", Published.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>
    /// Checks the signature first and only then reads the text. Throws <see cref="UpdateException"/> for a bad signature
    /// or anything in the file that is not exactly as expected.
    /// </summary>
    public static UpdateManifest ParseSigned(byte[] json, string? signatureBase64, string publicKeyBase64)
    {
        if (!EcSignature.VerifyBase64(json, signatureBase64, publicKeyBase64))
            throw new UpdateException("The update information is not signed with RigControl's update key, so it was ignored.");
        return Parse(json);
    }

    /// <summary>Reads update.json. Only call this on bytes whose signature has been checked.</summary>
    internal static UpdateManifest Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("it is not a JSON object");
            var versionText = String(root, "version");
            if (!AppVersion.TryParse(versionText, out var version)) throw Bad("the version is not like 1.1.2");
            var file = String(root, "file");
            if (file != version.SetupFileName) throw Bad("the file name does not match the version");
            var sha = String(root, "sha256");
            if (!Hex64.IsMatch(sha)) throw Bad("the SHA-256 is not 64 lower-case hex digits");
            if (!root.TryGetProperty("size", out var sizeEl) || !sizeEl.TryGetInt64(out var size) || size <= 0 || size > MaxSize)
                throw Bad("the size is missing or out of range");
            if (!DateTimeOffset.TryParse(String(root, "published"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published))
                throw Bad("the publish date is not a date");
            return new UpdateManifest(version, file, sha, size, published);
        }
        catch (JsonException ex)
        {
            throw new UpdateException("The update information is not valid JSON.", ex);
        }
    }

    private static string String(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()! : throw Bad($"\"{name}\" is missing");

    private static UpdateException Bad(string why) => new("The update information is not usable: " + why + ".");

    /// <summary>SHA-256 of a file as lower-case hex.</summary>
    public static string HashFile(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>The four files a release carries for updates, made in one folder.</summary>
public sealed record ReleaseFileSet(string Folder, string SetupExe, string ManifestPath, string SignaturePath, string SumsPath, UpdateManifest Manifest)
{
    public IReadOnlyList<string> All => new[] { SetupExe, ManifestPath, SignaturePath, SumsPath };
}

/// <summary>Makes update.json, update.json.sig and SHA256SUMS.txt for a setup exe. Used by the app and tools/UpdateSigner.</summary>
public static class ReleaseFiles
{
    /// <summary>
    /// Copies <paramref name="exePath"/> into <paramref name="outFolder"/> as RigControl-Setup-v&lt;version&gt;.exe and
    /// writes the three files next to it. The signature is checked with the key's own public half before returning.
    /// </summary>
    public static ReleaseFileSet Make(string exePath, AppVersion version, ECDsa signingKey, string outFolder, DateTimeOffset published)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (!System.IO.File.Exists(exePath)) throw new UpdateException("The program file was not found: " + exePath);
        Directory.CreateDirectory(outFolder);

        var setup = Path.Combine(outFolder, version.SetupFileName);
        if (!string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(setup), StringComparison.OrdinalIgnoreCase))
            System.IO.File.Copy(exePath, setup, overwrite: true);

        var size = new FileInfo(setup).Length;
        if (size <= 0 || size > UpdateManifest.MaxSize) throw new UpdateException("The program file is empty or too large.");
        var manifest = new UpdateManifest(version, version.SetupFileName, UpdateManifest.HashFile(setup), size, published);
        var json = manifest.ToJsonBytes();
        var signature = EcSignature.Sign(json, signingKey);
        if (!EcSignature.VerifyBase64(json, signature, EcSignature.PublicKeyBase64(signingKey)))
            throw new UpdateException("The signature just made does not check out; nothing was written.");

        var manifestPath = Path.Combine(outFolder, UpdateManifest.FileName);
        var sigPath = Path.Combine(outFolder, UpdateManifest.SignatureFileName);
        var sumsPath = Path.Combine(outFolder, UpdateManifest.SumsFileName);
        System.IO.File.WriteAllBytes(manifestPath, json);
        System.IO.File.WriteAllText(sigPath, signature + "\n", new UTF8Encoding(false));

        // The usual "sha256sum" layout: hash, two spaces, name.
        var sums = new StringBuilder();
        foreach (var path in new[] { setup, manifestPath, sigPath })
            sums.Append(UpdateManifest.HashFile(path)).Append("  ").Append(Path.GetFileName(path)).Append('\n');
        System.IO.File.WriteAllText(sumsPath, sums.ToString(), new UTF8Encoding(false));

        return new ReleaseFileSet(outFolder, setup, manifestPath, sigPath, sumsPath, manifest);
    }
}
