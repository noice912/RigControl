using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Principal;
using RigControl.App.Updates;
using RigControl.iPhone.Anisette;
using RigControl.iPhone.Apple;
using RigControl.iPhone.Device;
using RigControl.iPhone.Helper;
using RigControl.Updates;

namespace RigControl.App.IPhone;

/// <summary>
/// "RigControl.exe --iphone [--renew]": the iPhone installer, a separate process WITHOUT administrator rights. The
/// Apple ID password, the two-factor code and the signing key exist only here, never in the elevated main app.
/// Started from Settings > Phone ("Install on iPhone") and, for renewals, by the main app when a connected iPhone's
/// app is within a day of expiring.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IphoneHelper
{
    public const string Switch = "--iphone";
    public const string RenewSwitch = "--renew";

    /// <summary>Added when the installer restarts itself without admin rights, so it can't loop if that didn't work.</summary>
    public const string DeelevatedSwitch = "--deelevated";

    public static InstallerPaths Paths { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigControl", "iphone"));

    /// <summary>True when the arguments ask for the installer (then this process is the installer and nothing else).</summary>
    public static bool Wants(string[] args) => args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the installer. With administrator rights it starts itself again without them (through Explorer) and
    /// returns false: the caller exits. Otherwise it opens the window and returns true (the app ends with the window).
    /// </summary>
    public static bool Run(System.Windows.Application app, string[] args, Action<string> log)
    {
        if (Environment.IsPrivilegedProcess && args.Any(a => string.Equals(a, DeelevatedSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            log("The iPhone installer still has administrator rights after restarting without them (UAC off?); it won't run.");
            System.Windows.MessageBox.Show(
                "The iPhone installer must run without administrator rights, so your Apple ID password never reaches the " +
                "part of RigControl that has them. Windows started it with them anyway (User Account Control may be off), " +
                "so it was stopped.", "RigControl", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
        if (Environment.IsPrivilegedProcess)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("RigControl can't find its own program file.");
            var rest = string.Join(' ', args
                .Where(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase) || string.Equals(a, RenewSwitch, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.ToLowerInvariant())
                .Append(DeelevatedSwitch));
            log("The iPhone installer was started with administrator rights; starting it again without them.");
            RigControl.Presence.Launcher.StartProgram(exe, rest);
            return false;
        }
        // This process skips the main app's start-up, so it gets its own last-resort handlers: an error is logged and
        // shown, never a silent exit.
        app.DispatcherUnhandledException += (_, e) =>
        {
            log("iPhone installer: unexpected " + e.Exception.GetType().Name + ": " + e.Exception.Message);
            (app.MainWindow as InstallerWindow)?.ShowError(e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log("iPhone installer: unobserved " + e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.SetObserved();
        };
        var renew = args.Any(a => string.Equals(a, RenewSwitch, StringComparison.OrdinalIgnoreCase));
        var window = new InstallerWindow(CreateSession(log), renew);
        app.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
        app.MainWindow = window;
        window.Show();
        return true;
    }

    private static InstallerSession CreateSession(Action<string> log)
    {
        // Apple's sign-in servers use Apple's own root CA, trusted here for apple.com hosts only (AppleTls).
        var http = new HttpClient(AppleTls.CreateHandler()) { Timeout = TimeSpan.FromSeconds(60) };
        return new InstallerSession(http, log);
    }
}

/// <summary>DPAPI for the current Windows user, with a fixed purpose so other RigControl secrets can't be swapped in.</summary>
[SupportedOSPlatform("windows")]
public sealed class IphoneSecretProtector : ISecretProtector
{
    private static readonly byte[] Purpose = "RigControl.iPhoneInstaller.v1"u8.ToArray();
    private readonly RigControl.Core.Platform.DpapiSecretProtector _dpapi = new();

    public byte[] Protect(byte[] plain) => _dpapi.Protect(plain, Purpose);

    public byte[] Unprotect(byte[] protectedBytes) => _dpapi.Unprotect(protectedBytes, Purpose);
}

/// <summary>Everything one installer window works with, created once per process.</summary>
[SupportedOSPlatform("windows")]
internal sealed class InstallerSession(HttpClient http, Action<string> log)
{
    private CoreAdi? _adi;
    private IphoneInstaller? _installer;

    public Action<string> Log { get; } = log;
    public DeviceService Devices { get; } = new();

    /// <summary>Loads Apple's CoreADI (first use only) and builds the installer.</summary>
    public IphoneInstaller Installer()
    {
        if (_installer is not null) return _installer;
        var path = CoreAdiLibrary.Prepare(IphoneHelper.Paths.Adi);
        _adi = CoreAdi.Load(path);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var anisette = new CoreAdiAnisette(_adi, http, WindowsMachine.DeviceIdentifier(), sid, WindowsMachine.WindowsVersion(),
            identityFile: Path.Combine(IphoneHelper.Paths.Adi, "identity.bin"));
        _installer = new IphoneInstaller(http, anisette, new IphoneSecretProtector(), Devices, IphoneHelper.Paths);
        return _installer;
    }

    /// <summary>
    /// The phone app for THIS RigControl version, from its GitHub release: mobile.json and its signature are checked
    /// with RigControl's update key first, then the .ipa is downloaded and must match the signed size and SHA-256.
    /// </summary>
    public async Task<string> DownloadIpaAsync(IProgress<string> progress, CancellationToken ct)
    {
        var version = RunningVersion.Resolve(null);
        var gh = GitHubHttp.CreateClient();
        var baseUrl = GitHubHosts.ReleaseDownloadPrefix + version.Tag + "/";
        progress.Report("Checking the phone app for RigControl " + version + "...");
        var json = await GetBytesAsync(gh, baseUrl + MobileManifest.FileName, 64 * 1024, ct).ConfigureAwait(false);
        var sig = System.Text.Encoding.ASCII.GetString(await GetBytesAsync(gh, baseUrl + MobileManifest.SignatureFileName, 4096, ct).ConfigureAwait(false)).Trim();
        var manifest = MobileManifest.ParseSigned(json, sig, TrustedKeys.UpdatePublicKey);
        if (manifest.Version != version)
            throw new UpdateException("The phone app's information is for another version.");

        Directory.CreateDirectory(IphoneHelper.Paths.Work);
        var target = Path.Combine(IphoneHelper.Paths.Work, manifest.File);
        if (File.Exists(target) && manifest.Matches(target))
            return target;
        progress.Report("Downloading RigControl Mobile...");
        var temp = target + ".download";
        using (var response = await gh.GetAsync(baseUrl + manifest.File, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The phone app couldn't be downloaded (GitHub answered {(int)response.StatusCode}).");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(temp);
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > manifest.Size) throw new UpdateException("The phone app download is larger than its signed size.");
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        if (!manifest.Matches(temp))
        {
            File.Delete(temp);
            throw new UpdateException("The downloaded phone app doesn't match its signed checksum, so it was not used.");
        }
        File.Move(temp, target, overwrite: true);
        return target;
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient http, string url, int max, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new UpdateException("This RigControl version has no phone app attached to its release yet.");
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"GitHub answered {(int)response.StatusCode} for the phone app's information.");
        return await GitHubHttp.ReadLimitedAsync(response, max, ct).ConfigureAwait(false);
    }
}
