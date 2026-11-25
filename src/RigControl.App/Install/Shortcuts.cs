using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace RigControl.App.Install;

/// <summary>The Start menu and desktop shortcuts, for every user of the PC (the install is for the whole PC).</summary>
internal static class Shortcuts
{
    public const string FileName = "RigControl.lnk";

    /// <summary>%ProgramData%\Microsoft\Windows\Start Menu\Programs\RigControl.lnk</summary>
    public static string StartMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), FileName);

    /// <summary>The Public desktop's RigControl.lnk.</summary>
    public static string Desktop => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), FileName);

    /// <summary>Creates or replaces a shortcut that starts this exe.</summary>
    public static void Create(string linkPath, string targetExe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetExe);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetExe)!);
            link.SetDescription("RigControl: fans, lights, sensors and more for your PC");
            link.SetIconLocation(targetExe, 0);
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static void Remove(string linkPath)
    {
        if (File.Exists(linkPath)) File.Delete(linkPath);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relPath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
