using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RigControl.App;

/// <summary>
/// Ensures one RigControl per Windows session. Based on the existence of a named kernel object rather than
/// mutex ownership, so it can be released from any thread.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly string _mutexName;
    private readonly string _eventName;
    private readonly string _exitEventName;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _wait;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;

    /// <summary>Raised (on a thread-pool thread) when a second copy was started and asked us to show our window.</summary>
    public event Action? ShowRequested;

    /// <summary>Raised (on a thread-pool thread) when "RigControl.exe --exit" asked this copy to close.</summary>
    public event Action? ExitRequested;

    public SingleInstance(string name)
    {
        _mutexName = "Local\\" + name + ".Instance";
        _eventName = "Local\\" + name + ".Show";
        _exitEventName = "Local\\" + name + ".Exit";
    }

    /// <summary>
    /// A signal another copy of RigControl in this Windows session can set, even when this copy runs as
    /// administrator and the other does not (Windows otherwise stops a normal program from signalling an elevated
    /// one). Low-integrity programs (sandboxed browsers and the like) still cannot.
    /// </summary>
    private static EventWaitHandle CreateSignal(string name)
    {
        const string sddl = "D:(A;;0x1F0003;;;WD)(A;;0x1F0003;;;SY)S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var sd, IntPtr.Zero))
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd, bInheritHandle = false };
            var handle = CreateEvent(ref sa, false, false, name);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return new EventWaitHandle(false, EventResetMode.AutoReset, name);
            }
            return new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
        }
        finally
        {
            LocalFree(sd);
        }
    }

    private static RegisteredWaitHandle Listen(EventWaitHandle signal, Func<Action?> handler)
        => ThreadPool.RegisterWaitForSingleObject(signal, (_, timedOut) =>
        {
            if (!timedOut) handler()?.Invoke();
        }, null, Timeout.Infinite, false);

    /// <summary>Asks the running copy (if any) to close. Returns false if there is none to ask.</summary>
    public bool SignalExit()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(_exitEventName, out var existing)) return false;
            existing.Set();
            existing.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEvent(ref SECURITY_ATTRIBUTES attributes, bool manualReset, bool initialState, string name);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public bool TryAcquire()
    {
        try
        {
            var mutex = new Mutex(false, _mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
            _mutex = mutex;
        }
        catch (UnauthorizedAccessException)
        {
            // Another copy exists at a higher privilege level.
            return false;
        }

        try
        {
            _showEvent = CreateSignal(_eventName);
            _wait = Listen(_showEvent, () => ShowRequested);
        }
        catch
        {
            // Not being able to receive "show yourself" requests is not fatal.
        }
        try
        {
            _exitEvent = CreateSignal(_exitEventName);
            _exitWait = Listen(_exitEvent, () => ExitRequested);
        }
        catch
        {
            // Then "--exit" cannot reach this copy; the tray menu still can.
        }
        return true;
    }

    public void SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(_eventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch
        {
            // The other copy may be elevated; nothing more we can do.
        }
    }

    public void Dispose()
    {
        try { _wait?.Unregister(null); } catch { /* ignore */ }
        _wait = null;
        try { _exitWait?.Unregister(null); } catch { /* ignore */ }
        _exitWait = null;
        try { _exitEvent?.Dispose(); } catch { /* ignore */ }
        _exitEvent = null;
        try { _showEvent?.Dispose(); } catch { /* ignore */ }
        _showEvent = null;
        try { _mutex?.Dispose(); } catch { /* ignore */ }
        _mutex = null;
    }
}

/// <summary>Notification-area icon with Open / Exit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayIcon(Action open, Action exit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open RigControl", null, (_, _) => open());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "RigControl",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) open();
        };
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // fall through to the stock icon
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void ShowNotice(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(6000);
        }
        catch
        {
            // Notifications may be disabled; not important.
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

internal static class ShellActions
{
    /// <summary>Opens an https link in the user's default browser. Anything else is refused.</summary>
    public static bool OpenHttpsUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return true;
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false })?.Dispose();
    }
}
