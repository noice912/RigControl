using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace RigControl.Nvidia;

/// <summary>
/// Downloads NVIDIA's own driver installer and starts it. The file must come from NVIDIA's download servers over
/// HTTPS and must carry a valid Windows signature from NVIDIA Corporation, or it is deleted and never run.
/// </summary>
public static class DriverInstaller
{
    /// <summary>True only for https links on NVIDIA's download servers that point at an .exe.</summary>
    public static bool IsAllowedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
        if (!u.Host.EndsWith(".download.nvidia.com", StringComparison.OrdinalIgnoreCase)) return false;
        return u.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads to <paramref name="path"/>, reporting (received bytes, total bytes or null).</summary>
    public static async Task DownloadAsync(string url, string path, Action<long, long?> progress, CancellationToken ct)
    {
        if (!IsAllowedUrl(url)) throw new InvalidOperationException("That is not an NVIDIA download link.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // A redirect must not lead away from NVIDIA.
        if (!IsAllowedUrl(response.RequestMessage?.RequestUri?.ToString())) throw new InvalidOperationException("The download was redirected away from NVIDIA.");
        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            long received = 0;
            var lastReport = Stopwatch.StartNew();
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                if (lastReport.ElapsedMilliseconds >= 500)
                {
                    progress(received, total);
                    lastReport.Restart();
                }
            }
            progress(received, total);
            if (total is { } t && received != t) throw new IOException("The download ended early.");
        }
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>True if Windows accepts the file's signature and the signer is NVIDIA Corporation.</summary>
    public static bool IsSignedByNvidia(string path)
    {
        try
        {
            if (!WindowsTrusts(path)) return false;
#pragma warning disable SYSLIB0057 // the replacement API does not read Authenticode signers
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains("O=NVIDIA Corporation", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts the installer. Windows shows its own permission prompt and NVIDIA's installer takes over.</summary>
    public static void Start(string path)
    {
        using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static bool WindowsTrusts(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // no dialogs
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,       // a file
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x00001000, // do not go online just for revocation lists
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // generic "verify this file's signature"
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

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
