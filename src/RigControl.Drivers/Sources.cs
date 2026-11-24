using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32;

namespace RigControl.Drivers;

/// <summary>Everything the drivers module needs from Windows and the internet. Abstracted so tests touch neither.</summary>
public interface IDriverSources
{
    /// <summary>The motherboard as Windows reports it: (maker, model).</summary>
    (string? Maker, string? Model) Board();
    Task<IReadOnlyList<AsusFile>> GetAsusAsync(string model, CancellationToken ct);
    IReadOnlyList<InstalledDriver> GetInstalled();
    Task<IReadOnlyList<WindowsUpdateDriver>> SearchWindowsUpdateAsync(CancellationToken ct);
    /// <summary>Downloads and installs the given Windows Update drivers. Returns (installed count, restart needed, problem).</summary>
    Task<(int Installed, bool RestartNeeded, string? Problem)> InstallWindowsUpdateAsync(IReadOnlyList<string> ids, CancellationToken ct);
    Task DownloadAsync(string url, string path, Action<long, long?> progress, CancellationToken ct);
    /// <summary>The company that signed the file, if Windows trusts the signature; otherwise null.</summary>
    string? TrustedSigner(string path);
    void Start(string path);
}

public sealed class WindowsDriverSources : IDriverSources
{
    public (string? Maker, string? Model) Board()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return (key?.GetValue("BaseBoardManufacturer") as string, key?.GetValue("BaseBoardProduct") as string);
        }
        catch
        {
            return (null, null);
        }
    }

    public async Task<IReadOnlyList<AsusFile>> GetAsusAsync(string model, CancellationToken ct)
    {
        // The same list ASUS's own support page shows for this board and Windows 11 (osid 52). Sends only the model name.
        var url = "https://www.asus.com/support/api/product.asmx/GetPDDrivers?website=global&osid=52&model=" + Uri.EscapeDataString(model);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return DriverPlan.ParseAsus(doc.RootElement);
    }

    public IReadOnlyList<InstalledDriver> GetInstalled()
    {
        var list = new List<InstalledDriver>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceClass, DeviceName, DriverVersion, DriverProviderName FROM Win32_PnPSignedDriver");
        foreach (var o in searcher.Get())
        {
            using (o)
            {
                var name = o["DeviceName"] as string;
                var version = o["DriverVersion"] as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) continue;
                list.Add(new InstalledDriver(o["DeviceClass"] as string ?? "", name, version, o["DriverProviderName"] as string));
            }
        }
        return list;
    }

    // ---------------------------------------------------------------- Windows Update (the Windows Update Agent's own COM interface)

    private static dynamic NewSession()
        => Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!)!;

    public Task<IReadOnlyList<WindowsUpdateDriver>> SearchWindowsUpdateAsync(CancellationToken ct) => Task.Run(() =>
    {
        dynamic session = NewSession();
        session.ClientApplicationID = "RigControl";
        dynamic result = session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        var list = new List<WindowsUpdateDriver>();
        int count = result.Updates.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic u = result.Updates.Item(i);
            string? date = null;
            try { date = ((DateTime)u.DriverVerDate).ToString("yyyy-MM-dd"); } catch { /* not given */ }
            list.Add(new WindowsUpdateDriver((string)u.Identity.UpdateID, (string)u.Title, (string?)u.DriverClass, date,
                Math.Round(Convert.ToDouble(u.MaxDownloadSize) / 1048576.0, 1), !(bool)u.EulaAccepted));
        }
        return (IReadOnlyList<WindowsUpdateDriver>)list;
    }, ct);

    public Task<(int Installed, bool RestartNeeded, string? Problem)> InstallWindowsUpdateAsync(IReadOnlyList<string> ids, CancellationToken ct) => Task.Run(() =>
    {
        dynamic session = NewSession();
        session.ClientApplicationID = "RigControl";
        dynamic found = session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        dynamic chosen = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", throwOnError: true)!)!;
        var skipped = 0;
        int count = found.Updates.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic u = found.Updates.Item(i);
            if (!ids.Contains((string)u.Identity.UpdateID)) continue;
            // Licence terms are the user's to accept; such an update is left for Windows Update itself.
            if (!(bool)u.EulaAccepted)
            {
                skipped++;
                continue;
            }
            chosen.Add(u);
        }
        if ((int)chosen.Count == 0)
            return (0, false, skipped > 0 ? "These updates ask you to accept licence terms; install them from Windows Update in Settings." : "Those updates are no longer offered.");
        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = chosen;
        downloader.Download();
        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = chosen;
        dynamic result = installer.Install();
        var installed = 0;
        for (var i = 0; i < (int)chosen.Count; i++)
            if ((int)result.GetUpdateResult(i).ResultCode == 2) installed++; // 2 = succeeded
        string? problem = installed < (int)chosen.Count ? $"{(int)chosen.Count - installed} update(s) did not install." : null;
        if (skipped > 0) problem = (problem is null ? "" : problem + " ") + $"{skipped} need licence terms accepted in Windows Update.";
        return (installed, (bool)result.RebootRequired, problem);
    }, ct);

    // ---------------------------------------------------------------- ASUS packages

    public async Task DownloadAsync(string url, string path, Action<long, long?> progress, CancellationToken ct)
    {
        if (!AsusDownloads.IsAllowedUrl(url)) throw new InvalidOperationException("That is not an ASUS download link.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (!AsusDownloads.IsAllowedUrl(response.RequestMessage?.RequestUri?.ToString())) throw new InvalidOperationException("The download was redirected away from ASUS.");
        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            long received = 0;
            var last = Stopwatch.StartNew();
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                if (last.ElapsedMilliseconds >= 500)
                {
                    progress(received, total);
                    last.Restart();
                }
            }
            progress(received, total);
            if (total is { } t && received != t) throw new IOException("The download ended early.");
        }
        File.Move(partial, path, overwrite: true);
    }

    public string? TrustedSigner(string path)
    {
        try
        {
            if (!Authenticode.WindowsTrusts(path)) return null;
#pragma warning disable SYSLIB0057 // the replacement API does not read Authenticode signers
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false) + " / " + cert.Subject;
        }
        catch
        {
            return null;
        }
    }

    public void Start(string path)
    {
        using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
    }

    /// <summary>SHA-256 of a file, lower-case hex.</summary>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Unpacks a zip into an empty folder. .NET refuses entries that would land outside it.</summary>
    public static void Unpack(string zip, string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        ZipFile.ExtractToDirectory(zip, folder);
    }
}

internal static class Authenticode
{
    public static bool WindowsTrusts(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,
                dwUnionChoice = 1,
                pFile = filePtr,
                dwProvFlags = 0x00001000,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
