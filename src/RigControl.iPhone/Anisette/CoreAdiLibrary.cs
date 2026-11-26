using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RigControl.iPhone.Anisette;

/// <summary>
/// Finds Apple's CoreADI64.dll in an Apple app already installed on this PC (Apple Devices, Apple Music, iTunes or
/// iCloud), checks it is signed by Apple, and copies it to RigControl's own folder for this Windows user. Windows won't
/// load a DLL straight out of a Store app's folder for another program, and a private copy also can't change under us.
/// RigControl never ships Apple's file; it only uses the copy the person installed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CoreAdiLibrary
{
    private const string FileName = "CoreADI64.dll";

    /// <summary>Where installed Apple apps keep CoreADI64.dll, best first. Only existing files.</summary>
    public static IReadOnlyList<string> Candidates()
    {
        var found = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            var path = Path.Combine(dir, FileName);
            if (File.Exists(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
                found.Add(path);
        }

        // Store apps: their folders are found through the package list of the current user.
        foreach (var dir in StorePackageFolders(["AppleInc.AppleDevices_", "AppleInc.AppleMusicWin_", "AppleInc.iTunes_", "AppleInc.iCloud_"]))
            Add(dir);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        Add(Path.Combine(pf, "iTunes"));
        Add(Path.Combine(common, "Apple", "Apple Application Support"));
        Add(Path.Combine(common, "Apple", "Internet Services"));
        return found;
    }

    private static IEnumerable<string> StorePackageFolders(string[] prefixes)
    {
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (root is null) yield break;
        // Newest version first for each app.
        var names = root.GetSubKeyNames()
            .Where(n => prefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Where(n => n.Contains("_x64_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => Array.FindIndex(prefixes, p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(n => n, StringComparer.OrdinalIgnoreCase);
        // This registry key is the user's own (writable by them): only real Store folders are looked at, so the
        // elevated app never touches a network path or another place someone pointed it at.
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + Path.DirectorySeparatorChar;
        foreach (var name in names)
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("PackageRootFolder") is not string folder) continue;
            string full;
            try { full = Path.GetFullPath(folder); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (full.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                yield return full;
        }
    }

    /// <summary>
    /// Copies the first Apple-signed candidate into <paramref name="folder"/> (RigControl's per-user folder) and returns
    /// the copy's path, re-checking the copy's signature. Throws <see cref="AnisetteException"/> with plain advice when
    /// there is none.
    /// </summary>
    public static string Prepare(string folder)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, FileName);
        foreach (var source in Candidates())
        {
            if (!IsSignedByApple(source))
                continue;
            if (!File.Exists(target) || !SameBytes(source, target))
            {
                var temp = target + ".new";
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, target, overwrite: true);
            }
            if (IsSignedByApple(target))
                return target;
        }
        throw new AnisetteException(
            "RigControl needs Apple's sign-in component, which comes with the free Apple Devices app. Install Apple Devices " +
            "from the Microsoft Store (it also lets Windows talk to your iPhone over USB), then try again.");
    }

    private static bool SameBytes(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length) return false;
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    /// <summary>
    /// True when Windows trusts the file's Authenticode signature (WinVerifyTrust, whole chain, revocation not
    /// checked online) and the signer is Apple Inc.
    /// </summary>
    public static bool IsSignedByApple(string path)
    {
        if (!VerifyTrust(path))
            return false;
        try
        {
#pragma warning disable SYSLIB0057 // reading the signer of a signed file; the trust check above is WinVerifyTrust
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var org = cert.GetNameInfo(X509NameType.SimpleName, false);
            return cert.Subject.Contains("O=Apple Inc.", StringComparison.Ordinal)
                && org.Equals("Apple Inc.", StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // ---- WinVerifyTrust ----------------------------------------------------------------------------------------------

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WinTrustData data);

    private static bool VerifyTrust(string path)
    {
        var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,           // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x1000 | 0x80, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(-1, ref action, ref data);
            data.dwStateAction = 2;          // WTD_STATEACTION_CLOSE
            WinVerifyTrust(-1, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }
}
