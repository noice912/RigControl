using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Signing;

/// <summary>
/// Re-signs an .ipa for a team: new bundle ids, each bundle's profile as embedded.mobileprovision, entitlements
/// from the profiles, and a fresh signature on every Mach-O, inside-out (frameworks and libraries, then app
/// extensions, then the app's own executable), each bundle sealed with its _CodeSignature/CodeResources before
/// its executable is signed. Nothing about the certificate's key is logged or reported.
/// </summary>
public static class IpaSigner
{
    private static readonly Regex BundleIdPattern = new("^[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)*$", RegexOptions.CultureInvariant);

    public static Task SignAsync(SignRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Sign(request, progress, ct), ct);
    }

    private sealed record BundlePlan(string Identifier, PlistDict? Entitlements);

    private sealed class Context(MachOSigner signer, string teamId, string? commonName, string root,
        IProgress<string>? progress, CancellationToken ct)
    {
        public MachOSigner Signer { get; } = signer;
        public string TeamId { get; } = teamId;
        public string? CommonName { get; } = commonName;
        public string Root { get; } = root;
        public IProgress<string>? Progress { get; } = progress;
        public CancellationToken Ct { get; } = ct;
        public Dictionary<string, BundlePlan> Plans { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    }

    private static void Sign(SignRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        if (!request.Certificate.HasPrivateKey)
            throw new ArgumentException("The signing certificate has no private key.");
        if (!BundleIdPattern.IsMatch(request.MainBundleId) || request.MainBundleId.Length > 255)
            throw new ArgumentException($"\"{request.MainBundleId}\" is not a valid bundle id.");
        if (request.Profiles.Count == 0)
            throw new ArgumentException("No provisioning profiles were given.");
        if (!File.Exists(request.InputIpa))
            throw new FileNotFoundException("The .ipa to sign was not found.", request.InputIpa);

        var work = Path.Combine(Path.GetTempPath(), "RigControl-sign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report("Unpacking the app");
            var modes = IpaArchive.Extract(request.InputIpa, work, ct);
            var bundles = IpaInspector.ListBundlesInFolder(work);
            var main = bundles[0];

            var signer = new MachOSigner(new CmsSignature(request.Certificate,
                request.ExtraCertificates ?? AppleCertificates.DefaultChain));
            var commonName = request.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            string? teamId = null;
            var plans = new List<(string Dir, BundlePlan Plan)>();

            foreach (var bundle in bundles)
            {
                ct.ThrowIfCancellationRequested();
                var newId = bundle.IsExtension
                    ? ExtensionId(bundle.OriginalBundleId, main.OriginalBundleId, request.MainBundleId)
                    : request.MainBundleId;
                var profileBytes = request.Profiles.GetValueOrDefault(newId)
                    ?? request.Profiles.GetValueOrDefault(bundle.OriginalBundleId)
                    ?? throw new InvalidOperationException($"No provisioning profile was given for {newId}.");
                var profile = ProvisioningProfile.Parse(profileBytes);
                if (profile.DeveloperCertificates.Any() && !profile.IncludesCertificate(request.Certificate))
                    throw new InvalidOperationException($"The profile for {newId} was not issued for this certificate.");
                if (profile.ExpirationDate is { } expires && expires < DateTime.UtcNow)
                    throw new InvalidOperationException($"The profile for {newId} expired on {expires:yyyy-MM-dd}.");
                teamId ??= profile.TeamId;
                if (profile.TeamId != teamId)
                    throw new InvalidOperationException("The profiles belong to different teams.");

                var dir = Path.Combine(work, bundle.Path.Replace('/', Path.DirectorySeparatorChar));
                var infoPath = Path.Combine(dir, "Info.plist");
                var info = PlistFile.ParseDict(File.ReadAllBytes(infoPath));
                info["CFBundleIdentifier"] = newId;
                File.WriteAllBytes(infoPath, PlistFile.ToXml(info));
                File.WriteAllBytes(Path.Combine(dir, "embedded.mobileprovision"), profile.Raw);
                plans.Add((dir, new BundlePlan(newId, profile.EntitlementsFor(newId))));
            }

            var context = new Context(signer, teamId!, commonName, work, progress, ct);
            foreach (var (dir, plan) in plans)
                context.Plans[Path.GetFullPath(dir)] = plan;
            SignBundle(Path.GetFullPath(plans[0].Dir), context);

            progress?.Report("Packing the signed app");
            var partial = request.OutputIpa + ".partial";
            IpaArchive.Pack(work, partial, modes, ct);
            File.Move(partial, request.OutputIpa, overwrite: true);
            progress?.Report("Signed");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static string ExtensionId(string original, string originalMain, string newMain)
    {
        var suffix = original.StartsWith(originalMain + ".", StringComparison.Ordinal)
            ? original[originalMain.Length..]
            : "." + original.Split('.')[^1];
        return newMain + suffix;
    }

    /// <summary>Signs one bundle: what it contains first, then its seal, then its executable.</summary>
    private static void SignBundle(string dir, Context ctx)
    {
        ctx.Ct.ThrowIfCancellationRequested();
        var infoPath = Path.Combine(dir, "Info.plist");
        PlistDict? info = File.Exists(infoPath) ? PlistFile.ParseDict(File.ReadAllBytes(infoPath)) : null;
        ctx.Plans.TryGetValue(dir, out var plan);
        var isFramework = dir.EndsWith(".framework", StringComparison.OrdinalIgnoreCase);
        var executable = info?.String("CFBundleExecutable")
            ?? (isFramework ? Path.GetFileNameWithoutExtension(dir) : null)
            ?? throw new InvalidDataException($"{ctx.Relative(dir)} names no executable.");
        if (!IpaInspector.IsSafeName(executable))
            throw new InvalidDataException($"{ctx.Relative(dir)} has an invalid executable name.");
        var identifier = plan?.Identifier ?? info?.String("CFBundleIdentifier") ?? Path.GetFileNameWithoutExtension(dir);

        // Nested bundles: frameworks need no profile; app extensions were planned with theirs.
        var nested = new List<string>();
        foreach (var (folder, extension) in new[] { ("Frameworks", ".framework"), ("PlugIns", ".appex"), ("Extensions", ".appex") })
        {
            var parent = Path.Combine(dir, folder);
            if (!Directory.Exists(parent))
                continue;
            foreach (var child in Directory.EnumerateDirectories(parent).Order(StringComparer.Ordinal))
                if (child.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    if (extension == ".appex" && !ctx.Plans.ContainsKey(child))
                        throw new InvalidDataException($"{ctx.Relative(child)} was not planned for signing.");
                    SignBundle(child, ctx);
                    nested.Add(child + Path.DirectorySeparatorChar);
                }
        }

        // Loose Mach-O files (Frameworks/*.dylib and the like) that aren't inside a nested bundle.
        var mainPath = Path.Combine(dir, executable);
        var signatureDir = Path.Combine(dir, "_CodeSignature") + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ctx.Ct.ThrowIfCancellationRequested();
            if (file == mainPath || file.StartsWith(signatureDir, StringComparison.OrdinalIgnoreCase)
                || nested.Any(n => file.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                || !MachOImage.FileLooksLikeMachO(file))
                continue;
            ctx.Progress?.Report($"Signing {ctx.Relative(file)}");
            SignFile(file, ctx, new CodeSignOptions(Path.GetFileNameWithoutExtension(file), ctx.TeamId, ctx.CommonName,
                null, null, null));
        }

        if (!File.Exists(mainPath))
        {
            if (isFramework)
                return;   // a resource-only framework: nothing to sign
            throw new InvalidDataException($"{ctx.Relative(mainPath)} is missing.");
        }

        if (Directory.Exists(signatureDir))
            Directory.Delete(signatureDir, recursive: true);
        var resources = CodeResources.Build(dir, executable, ctx.Ct);
        Directory.CreateDirectory(signatureDir);
        File.WriteAllBytes(Path.Combine(signatureDir, "CodeResources"), resources);

        ctx.Progress?.Report($"Signing {ctx.Relative(mainPath)}");
        SignFile(mainPath, ctx, new CodeSignOptions(identifier, ctx.TeamId, ctx.CommonName,
            File.Exists(infoPath) ? File.ReadAllBytes(infoPath) : null, resources, plan?.Entitlements));
    }

    private static void SignFile(string path, Context ctx, CodeSignOptions options)
    {
        var length = new FileInfo(path).Length;
        if (length > IpaArchive.MaxFileSize)
            throw new InvalidDataException($"{ctx.Relative(path)} is too large to sign.");
        byte[] signed;
        try
        {
            signed = ctx.Signer.Sign(File.ReadAllBytes(path), options);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{ctx.Relative(path)}: {e.Message}", e);
        }
        File.WriteAllBytes(path, signed);
    }
}
