using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RigControl.Radio.Store;

/// <summary>
/// The elevated app writing into a folder the user (and so any program of theirs) can change: the folder is opened
/// without following links and held open without delete sharing while the file is written and renamed, so it can't
/// be swapped for a link in the meantime; its real location must be the expected one.
/// </summary>
internal sealed class SafeFolder : IDisposable
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareRead = 1, ShareWrite = 2; // no FILE_SHARE_DELETE: the folder can't be renamed or removed
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000, FlagOpenReparsePoint = 0x00200000;
    private readonly SafeFileHandle _handle;

    private SafeFolder(SafeFileHandle handle) => _handle = handle;

    /// <summary>Opens and holds <paramref name="path"/>. Throws IOException when it is a link or isn't where it says.</summary>
    public static SafeFolder Hold(string path)
    {
        var full = LongName(Path.GetFullPath(path)).TrimEnd('\\');
        var handle = CreateFileW(full, FileReadAttributes, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The learned songs folder couldn't be opened.", new Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("The learned songs folder couldn't be checked.");
            if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException("The learned songs folder is a link to somewhere else, so RigControl won't write there.");
            var buffer = new char[1024];
            var n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (n == 0 || n >= buffer.Length) throw new IOException("The learned songs folder's real place couldn't be read.");
            var real = new string(buffer, 0, (int)n);
            if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) real = @"\\" + real[8..];
            else if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) real = real[4..];
            if (!string.Equals(LongName(real).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The learned songs folder leads somewhere else through a link, so RigControl won't write there.");
            return new SafeFolder(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>The long form of a path (8.3 short names such as PROGRA~1 expanded), so the two can be compared.</summary>
    private static string LongName(string path)
    {
        var buffer = new char[1024];
        var n = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        return n == 0 || n >= buffer.Length ? path : new string(buffer, 0, (int)n);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint length);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
