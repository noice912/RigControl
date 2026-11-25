using System.Security.Cryptography;
using RigControl.Updates;

// Makes the dev token for one PC. The issuer key file is read, used and never printed.
//   dotnet run --project tools/DevEnroll -- --issuer <issuer .pem> --request <RCREQ2...> [--sub noice912]
// The token only works on the PC whose Windows Hello key made the request.

const string Usage = "Usage: DevEnroll --issuer <issuer private key .pem> --request <RCREQ2...> [--sub <name>]";

string? issuerPath = null, request = null, sub = "noice912";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--issuer" when i + 1 < args.Length: issuerPath = args[++i]; break;
        case "--request" when i + 1 < args.Length: request = args[++i]; break;
        case "--sub" when i + 1 < args.Length: sub = args[++i]; break;
        case "-h" or "--help": Console.WriteLine(Usage); return 0;
        default:
            Console.Error.WriteLine("Unknown or incomplete option: " + args[i]);
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
if (issuerPath is null || request is null)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var spki = DevEnrollment.ReadRequest(request, out var problem);
if (spki is null)
{
    Console.Error.WriteLine(problem);
    return 1;
}

try
{
    using var issuer = EcSignature.ImportPrivateKeyPem(File.ReadAllText(issuerPath));
    if (EcSignature.PublicKeyBase64(issuer) != TrustedKeys.DevKeyIssuerPublicKey)
    {
        Console.Error.WriteLine("That issuer key is not the one RigControl checks dev tokens with. Nothing was made.");
        return 1;
    }
    var token = DevEnrollment.Issue(issuer, spki, sub, DateTimeOffset.UtcNow);
    // Checked the way the app checks it before it is handed out.
    if (DevEnrollment.Verify(token, TrustedKeys.DevKeyIssuerPublicKey, out var check) is null)
    {
        Console.Error.WriteLine("The token just made does not check out: " + check);
        return 1;
    }
    Console.Error.WriteLine("Dev token for PC " + DevEnrollment.DeviceHash(spki) + ":");
    Console.WriteLine(token);
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
{
    Console.Error.WriteLine("Making the token failed: " + ex.Message);
    return 1;
}
