using System.Security.Cryptography;
using RigControl.Updates;

// Signs a release's setup exe for auto-update. The key file is read, used and never printed.
//   dotnet run --project tools/UpdateSigner -- --key <pem> --exe <file> --version 1.1.2 --out <dir> [--allow-other-key]
// Without --allow-other-key the key must be the one this build's app checks updates with, so a release signed with
// the wrong key is caught here and not by every user's app.

const string Usage = "Usage: UpdateSigner --key <private key .pem> --exe <setup exe> --version <1.2.3> --out <folder> [--allow-other-key]";

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var allowOther = false;
for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (a == "--allow-other-key") { allowOther = true; continue; }
    if (a is "--key" or "--exe" or "--version" or "--out" && i + 1 < args.Length) { options[a] = args[++i]; continue; }
    if (a is "-h" or "--help") { Console.WriteLine(Usage); return 0; }
    Console.Error.WriteLine("Unknown or incomplete option: " + a);
    Console.Error.WriteLine(Usage);
    return 2;
}
foreach (var required in new[] { "--key", "--exe", "--version", "--out" })
{
    if (!options.ContainsKey(required))
    {
        Console.Error.WriteLine("Missing " + required + ".");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
if (!AppVersion.TryParse(options["--version"], out var version))
{
    Console.Error.WriteLine("The version must look like 1.2.3.");
    return 2;
}

try
{
    using var key = EcSignature.ImportPrivateKeyPem(File.ReadAllText(options["--key"]));
    if (!allowOther && EcSignature.PublicKeyBase64(key) != TrustedKeys.UpdatePublicKey)
    {
        Console.Error.WriteLine("That key is not the one RigControl checks updates with. Nothing was written. (--allow-other-key to sign anyway.)");
        return 1;
    }
    var files = ReleaseFiles.Make(options["--exe"], version, key, Path.GetFullPath(options["--out"]), DateTimeOffset.UtcNow);
    Console.WriteLine("Signed RigControl " + version + " (" + files.Manifest.Size + " bytes, sha256 " + files.Manifest.Sha256 + ").");
    foreach (var f in files.All) Console.WriteLine("  " + f);
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or UpdateException)
{
    Console.Error.WriteLine("Signing failed: " + ex.Message);
    return 1;
}
