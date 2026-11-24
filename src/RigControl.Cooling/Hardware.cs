using System.Diagnostics;
using System.ServiceProcess;
using HidSharp;
using Microsoft.Win32;

namespace RigControl.Cooling;

/// <summary>An open HID device. Reports are whole 64-byte buffers including the report id at byte 0.</summary>
public interface IHidLink : IDisposable
{
    int MaxInputLength { get; }
    int MaxOutputLength { get; }
    void Write(byte[] report);

    /// <summary>Returns the next input report, or null if none arrived within the timeout.</summary>
    byte[]? Read(int timeoutMs);
}

/// <summary>Finds and opens HID devices. Abstracted so tests never touch real hardware.</summary>
public interface IHidBus
{
    /// <summary>Opens the device if exactly one with this id is present; otherwise returns null.</summary>
    IHidLink? OpenSingle(int vendorId, int productId);

    /// <summary>True if exactly one device with this id is plugged in (whether or not it can be opened).</summary>
    bool IsPresent(int vendorId, int productId);
}

public sealed class HidSharpBus : IHidBus
{
    public bool IsPresent(int vendorId, int productId)
    {
        try { return DeviceList.Local.GetHidDevices(vendorId, productId).Count() == 1; }
        catch { return false; }
    }

    public IHidLink? OpenSingle(int vendorId, int productId)
    {
        var devices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
        if (devices.Count != 1) return null;
        var device = devices[0];
        int maxIn, maxOut;
        try
        {
            maxIn = device.GetMaxInputReportLength();
            maxOut = device.GetMaxOutputReportLength();
        }
        catch
        {
            return null;
        }
        // Shared (non-exclusive) open: other programs such as NZXT CAM keep working.
        return device.TryOpen(out var stream) ? new Link(stream, maxIn, maxOut) : null;
    }

    private sealed class Link : IHidLink
    {
        private readonly HidStream _stream;
        public int MaxInputLength { get; }
        public int MaxOutputLength { get; }

        public Link(HidStream stream, int maxIn, int maxOut)
        {
            _stream = stream;
            MaxInputLength = maxIn;
            MaxOutputLength = maxOut;
            _stream.WriteTimeout = 2000;
        }

        public void Write(byte[] report) => _stream.Write(report);

        public byte[]? Read(int timeoutMs)
        {
            _stream.ReadTimeout = Math.Max(1, timeoutMs);
            try
            {
                var buffer = new byte[Math.Max(MaxInputLength, 64)];
                var n = _stream.Read(buffer, 0, buffer.Length);
                return n <= 0 ? null : buffer.AsSpan(0, n).ToArray();
            }
            catch (System.TimeoutException)
            {
                return null;
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}

public sealed record CamState(bool AppRunning, bool ServiceRunning, bool StartsWithWindows)
{
    /// <summary>True while any part of NZXT CAM that can talk to the devices is running.</summary>
    public bool Active => AppRunning || ServiceRunning;
}

/// <summary>Everything RigControl knows or does about NZXT CAM. Only user-triggered methods change anything.</summary>
public interface ICamControl
{
    CamState GetState();

    /// <summary>Closes the CAM app (window, tray and helpers). Returns false if some of it is still running.</summary>
    bool CloseApp();

    /// <summary>Stops the CAM background service (needs administrator). Returns false if it is still running.</summary>
    bool StopService();

    /// <summary>Turns CAM's two ways of starting with Windows off (startup entry + service). Service part needs administrator.</summary>
    void SetStartsWithWindows(bool enabled);
}

public sealed class NzxtCam : ICamControl
{
    private const string ServiceName = "CAMService";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NZXT.CAM";
    private const string BackupKey = @"Software\RigControl";
    private const string BackupValue = "NzxtCamRunCommand";
    private const string DefaultCommand = "\"C:\\Program Files\\NZXT CAM\\NZXT CAM.exe\" --startup";
    private static readonly string[] AppProcesses = { "NZXT CAM", "cam_helper" };

    public CamState GetState()
    {
        var app = false;
        foreach (var name in AppProcesses)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                app |= procs.Length > 0;
                foreach (var p in procs) p.Dispose();
            }
            catch
            {
                // treat as not running
            }
        }
        return new CamState(app, ServiceStatus() is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending,
            RunCommand() is not null || ServiceStartsAutomatically());
    }

    private static ServiceControllerStatus? ServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch
        {
            return null; // not installed
        }
    }

    private static bool ServiceStartsAutomatically()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.StartType == ServiceStartMode.Automatic;
        }
        catch
        {
            return false;
        }
    }

    private static string? RunCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) as string;
        }
        catch
        {
            return null;
        }
    }

    public bool CloseApp()
    {
        foreach (var name in AppProcesses)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in procs)
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); }
                catch { /* may have exited */ }
            }
            // CAM is an Electron app: closing the window only hides it to the tray, so end the processes.
            foreach (var p in procs)
            {
                try
                {
                    if (!p.WaitForExit(1500)) p.Kill(entireProcessTree: true);
                    p.WaitForExit(4000);
                }
                catch
                {
                    // access denied (elevated helper) or already gone; checked below
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return !GetState().AppRunning;
    }

    public bool StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Stopped) return true;
            if (sc.Status is not ServiceControllerStatus.StopPending) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            return true;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is null)
        {
            return true; // service not installed
        }
        catch
        {
            return false;
        }
    }

    public void SetStartsWithWindows(bool enabled)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
        using (var backup = Registry.CurrentUser.CreateSubKey(BackupKey, writable: true))
        {
            if (enabled)
            {
                var command = backup.GetValue(BackupValue) as string ?? DefaultCommand;
                run.SetValue(RunValue, command, RegistryValueKind.String);
            }
            else
            {
                if (run.GetValue(RunValue) is string current) backup.SetValue(BackupValue, current, RegistryValueKind.String);
                run.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }

        // Service start type: "sc config" is the supported way without extra dependencies. Needs administrator.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add(ServiceName);
        psi.ArgumentList.Add("start=");
        psi.ArgumentList.Add(enabled ? "auto" : "demand");
        using var p = Process.Start(psi);
        p?.WaitForExit(10000);
        if (ServiceStatus() is not null && ServiceStartsAutomatically() != enabled)
            throw new InvalidOperationException("Windows did not change how the CAM service starts.");
    }
}
