using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RigControl.Presence;

/// <summary>
/// Starts games without administrator rights. RigControl runs elevated, and anything it starts directly would
/// inherit that (some anti-cheats and save folders object). Windows' own desktop shell (Explorer, not elevated) is
/// asked to start the game instead, exactly as if it was double-clicked: IShellWindows -> desktop -> IShellDispatch2.
/// </summary>
public static class Launcher
{
    public static void Start(string target)
    {
        var isLink = System.Text.RegularExpressions.Regex.IsMatch(target, "^[a-z][a-z0-9.+-]*://", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!isLink && !File.Exists(target)) throw new FileNotFoundException("The game's file is missing.", target);
        var dir = isLink ? "" : Path.GetDirectoryName(target) ?? "";
        if (Environment.IsPrivilegedProcess)
        {
            ShellExecuteFromDesktop(target, dir);
            return;
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = dir })?.Dispose();
    }

    /// <summary>Opens a folder in Explorer, without administrator rights.</summary>
    public static void StartFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        if (Environment.IsPrivilegedProcess) ShellExecuteFromDesktop(folder, "");
        else Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Opens a shell target that isn't a file (tel:, sms:, ms-phone:, shell:AppsFolder\...) without administrator rights.</summary>
    public static void StartShell(string target)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(target, @"^(tel:|sms:|ms-phone:|shell:AppsFolder\\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("Not a shell link RigControl opens.", nameof(target));
        if (Environment.IsPrivilegedProcess) ShellExecuteFromDesktop(target, "");
        else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }

    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SVGIO_BACKGROUND = 0;

    private static void ShellExecuteFromDesktop(string file, string dir)
    {
        var type = Type.GetTypeFromCLSID(CLSID_ShellWindows) ?? throw new InvalidOperationException("Windows' shell is not available.");
        dynamic windows = Activator.CreateInstance(type)!;
        object loc = 0, empty = new VariantWrapper(null);
        int hwnd;
        object? disp = windows.FindWindowSW(ref loc, ref empty, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH);
        if (disp is null) throw new InvalidOperationException("The Windows desktop (Explorer) is not running.");
        var sp = (IServiceProvider)disp;
        var sidBrowser = SID_STopLevelBrowser;
        var iidBrowser = typeof(IShellBrowser).GUID;
        sp.QueryService(ref sidBrowser, ref iidBrowser, out var browserObj);
        var browser = (IShellBrowser)browserObj;
        browser.QueryActiveShellView(out var view);
        var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
        view.GetItemObject(SVGIO_BACKGROUND, ref iidDispatch, out var folderViewObj);
        dynamic folderView = folderViewObj;
        dynamic shell = folderView.Application;
        shell.ShellExecute(file, "", dir, "open", 1);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        void QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        void QueryActiveShellView(out IShellView ppshv);
        void OnViewWindowActive(IShellView pshv);
        void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void TranslateAccelerator(IntPtr pmsg);
        void EnableModeless(bool fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(IShellView psvPrevious, IntPtr pfs, IShellBrowser psb, IntPtr prcView, out IntPtr phWnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr pfs);
        void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
        void SaveViewState();
        void SelectItem(IntPtr pidlItem, uint uFlags);
        void GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }
}
