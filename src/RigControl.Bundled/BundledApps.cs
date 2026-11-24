using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace RigControl.Bundled;

/// <summary>One file RigControl carries: its path inside the payload ("openrgb/OpenRGB.exe") and its SHA-256.</summary>
public sealed record BundledFile(string Rel, string Sha256);

/// <summary>What the Settings page shows about the carried programs.</summary>
public sealed record BundledStatus(bool OpenRgbCarried, string? OpenRgbPath, bool OpenRgbServiceUsesIt, bool PawnIoCarried, bool PawnIoInstalled,
    bool EqualizerApoCarried, bool EqualizerApoInstalled, IReadOnlyList<string> Notes);

/// <summary>The helper programs RigControl needs. They are set up together, after one yes.</summary>
public static class BundledIds
{
    /// <summary>RigControl's own OpenRGB copy in its Program Files folder (an existing OpenRGB service is moved to it).</summary>
    public const string OpenRgb = "openrgb";
    /// <summary>The PawnIO driver.</summary>
    public const string PawnIo = "pawnio";
    /// <summary>Equalizer APO.</summary>
    public const string EqualizerApo = "equalizerapo";

    public static readonly IReadOnlyList<string> All = new[] { OpenRgb, PawnIo, EqualizerApo };
}

/// <summary>
/// The one yes for all helper programs ("Install and continue" on the setup screen). There is no yes or no per
/// program: RigControl needs all of them. A plain object so the app can keep it where only administrators can write.
/// </summary>
public sealed class BundledConsent
{
    /// <summary>True once the user pressed "Install and continue" in a copy of RigControl that runs as administrator.</summary>
    public bool AllHelpers { get; set; }
}

/// <summary>One helper program, for the setup screen or the Settings page.</summary>
/// <param name="Id">One of <see cref="BundledIds"/>.</param>
/// <param name="What">What it is, in one plain sentence.</param>
/// <param name="Licence">The licence's name.</param>
/// <param name="Carried">This build of RigControl has it inside.</param>
/// <param name="Installed">It is already on this PC.</param>
public sealed record BundledItem(string Id, string Name, string What, string Licence, bool Carried, bool Installed);

/// <summary>Where the helper programs stand on this PC.</summary>
/// <param name="Consented">The yes was given, or there is nothing left to set up (a PC that already has everything is never asked).</param>
/// <param name="NeedsYes">Something RigControl carries is not set up and nobody has said yes yet: show the setup step.</param>
/// <param name="Pending">Something RigControl carries is still not set up (before the yes, or after it when a set-up failed).</param>
/// <param name="ServiceWillMove">An OpenRGB service is on this PC and does not run RigControl's copy yet; the yes moves it.</param>
/// <param name="NotCarried">Names of the programs that are neither on this PC nor inside this build.</param>
/// <param name="Problems">What went wrong at the last try, one plain line per program.</param>
public sealed record BundledSummary(IReadOnlyList<BundledItem> Items, bool Consented, bool NeedsYes, bool Pending, bool ServiceWillMove,
    IReadOnlyList<string> NotCarried, IReadOnlyList<string> Problems)
{
    /// <summary>What is shown while the helper programs have not been looked at yet: nothing to ask, nothing to do.</summary>
    public static BundledSummary Nothing { get; } = new(Array.Empty<BundledItem>(), true, false, false, false, Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Sets up the programs RigControl carries (third_party/BUNDLED.md). RigControl needs all of them, so there is one
/// yes for all (<see cref="BundledConsent"/>): before it nothing is installed and an existing OpenRGB service is
/// never changed. A PC where everything is already set up needs no yes and is left as it is. Every step only runs
/// elevated, checks each file's SHA-256 against the one recorded at build time before writing or running it, and works
/// into C:\Program Files\RigControl after checking that programs without administrator rights can't write there.
/// </summary>
public sealed class BundledApps
{
    private readonly IBundledPayload _payload;
    private readonly BundledSystem _sys;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly List<string> _notes = new();
    private readonly Dictionary<string, string> _problems = new(); // program name -> what went wrong at the last try
    private bool _saidWaiting;
    private readonly object _run = new();   // one set-up at a time
    private readonly object _files = new(); // RigControl's OpenRGB files are being written or checked

    public BundledApps(Action<string> info, Action<string> warn, Assembly? payload = null, BundledConsent? consent = null)
        : this(info, warn, new AssemblyPayload(payload ?? typeof(BundledApps).Assembly), new BundledSystem(), consent) { }

    internal BundledApps(Action<string> info, Action<string> warn, IBundledPayload payload, BundledSystem system, BundledConsent? consent)
    {
        _payload = payload;
        _sys = system;
        _info = info;
        _warn = warn;
        Consent = consent ?? new BundledConsent();
    }

    /// <summary>The user's yes. The app loads it from the administrators-only store and passes it in; no yes = nothing installed.</summary>
    public BundledConsent Consent { get; }

    /// <summary>Called after <see cref="GiveConsent"/> recorded the yes, so the app can save it.</summary>
    public Action<BundledConsent>? ConsentChanged { get; set; }

    /// <summary>C:\Program Files\RigControl.</summary>
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RigControl");

    /// <summary>Where RigControl's own OpenRGB lives.</summary>
    public static string OpenRgbDir => Path.Combine(Root, "OpenRGB");

    private string OwnOpenRgbDir => Path.Combine(_sys.Root, "OpenRGB");
    private string OwnOpenRgbExe => Path.Combine(OwnOpenRgbDir, "OpenRGB.exe");

    /// <summary>The carried files, from the build-time metadata (only those really embedded).</summary>
    public IReadOnlyList<BundledFile> Files(string kind) => _payload.Files(kind);

    public bool Carries(string kind) => Files(kind).Count > 0;

    // ---------------------------------------------------------------- the yes

    /// <summary>
    /// Records the user's yes for all helper programs. Nothing is installed by this call (<see cref="EnsureAll"/> does that).
    /// Only a copy of RigControl that runs as administrator may take the yes: any program can talk to one that does not.
    /// </summary>
    public void GiveConsent()
    {
        if (!_sys.Elevated)
            throw new InvalidOperationException("RigControl only takes this yes when it runs as administrator. Restart it as administrator and press the button again.");
        if (Consent.AllHelpers) return;
        Consent.AllHelpers = true;
        ConsentChanged?.Invoke(Consent);
    }

    private bool OpenRgbCopyThere => File.Exists(OwnOpenRgbExe);

    /// <summary>An OpenRGB service exists and does not run RigControl's copy (and this build has a copy to give it).</summary>
    private bool ServiceWillMove => Carries("openrgb") && _sys.OpenRgbServiceImagePath() is not null && !ServiceUses(OwnOpenRgbExe);

    /// <summary>The helper programs with what the setup screen and the Settings page say about them.</summary>
    public IReadOnlyList<BundledItem> Items()
    {
        var openRgb = Carries("openrgb");
        return new[]
        {
            new BundledItem(BundledIds.OpenRgb, "OpenRGB",
                "Sets the colours of your RGB lights. RigControl keeps its own copy in its Program Files folder.",
                "GPL-2.0-only (its Qt files: LGPL-3.0)", openRgb,
                // A build without its own copy can only tell that some OpenRGB is set up on this PC.
                OpenRgbCopyThere || (!openRgb && _sys.OpenRgbServiceImagePath() is not null)),
            new BundledItem(BundledIds.PawnIo, "PawnIO",
                "A signed Windows driver that RigControl needs to read temperatures and control fans.",
                // What PawnIO's own repository states. THIRD-PARTY-NOTICES.md says what was and was not checked.
                "GPL-2.0 or later, with PawnIO's own exception (see pawnio.eu)", Carries("pawnio"), _sys.PawnIoInstalled()),
            new BundledItem(BundledIds.EqualizerApo, "Equalizer APO",
                "Puts RigControl's audio equaliser on your sound output. Windows needs one restart after it is installed.",
                "GPL-2.0", Carries("equalizerapo"), _sys.EqualizerApoInstalled()),
        };
    }

    /// <summary>
    /// Where the helper programs stand. A PC where everything RigControl carries is already set up (and an OpenRGB
    /// service, if there is one, already runs RigControl's copy) counts as a yes: it is never asked and nothing is
    /// installed again.
    /// </summary>
    public BundledSummary Summary()
    {
        var items = Items();
        var move = ServiceWillMove;
        var pending = move || items.Any(i => i.Carried && !i.Installed);
        List<string> problems;
        lock (_problems) problems = _problems.Select(p => p.Key + ": " + p.Value).ToList();
        return new BundledSummary(items, Consent.AllHelpers || !pending, pending && !Consent.AllHelpers, pending, move,
            items.Where(i => !i.Carried && !i.Installed).Select(i => i.Name).ToList(), problems);
    }

    // ---------------------------------------------------------------- the whole set-up (at start and after a yes, elevated)

    /// <summary>
    /// Installs every helper program this build carries, once the yes exists. Before the yes it only keeps a copy of
    /// OpenRGB that is already RigControl's own up to date. Safe to call again (after the yes, or to try again).
    /// </summary>
    public void EnsureAll()
    {
        lock (_run)
        {
            if (!_sys.Elevated)
            {
                Warn("The programs RigControl needs can only be set up when RigControl runs as administrator.");
                return;
            }
            if (!Consent.AllHelpers)
            {
                var waiting = Items().Where(i => i.Carried && !i.Installed).Select(i => i.Name).ToList();
                // Said once in the log only: the Settings page shows where things stand now, not what was true at start.
                if (waiting.Count > 0 && !_saidWaiting)
                    _info($"RigControl needs {string.Join(", ", waiting)}, which {(waiting.Count == 1 ? "is" : "are")} not set up on this PC yet. " +
                         "Nothing is installed until you press \"Install and continue\" on the setup screen.");
                if (ServiceWillMove && !_saidWaiting)
                    _info("An OpenRGB service is already set up on this PC. RigControl leaves it as it is until you press \"Install and continue\" on the setup screen.");
                _saidWaiting = true;
            }
            Try("PawnIO", EnsurePawnIo);
            Try("OpenRGB", () => { if (EnsureOpenRgbFiles()) MoveOpenRgbService(); });
            Try("Equalizer APO", EnsureEqualizerApo);
        }
    }

    /// <summary>One program's set-up. What went wrong is kept for the screen until a later try works.</summary>
    private void Try(string name, Action setUp)
    {
        try
        {
            setUp();
            lock (_problems) _problems.Remove(name);
        }
        catch (Exception ex)
        {
            lock (_problems) _problems[name] = ex.Message;
            Warn(name + ": " + ex.Message);
        }
    }

    public BundledStatus Status()
    {
        var exe = OwnOpenRgbExe;
        List<string> notes;
        lock (_notes) notes = _notes.ToList();
        return new BundledStatus(Carries("openrgb"), File.Exists(exe) ? exe : null, ServiceUses(exe), Carries("pawnio"), _sys.PawnIoInstalled(),
            Carries("equalizerapo"), _sys.EqualizerApoInstalled(), notes);
    }

    private void Info(string s) { if (Note(s)) _info(s); }
    private void Warn(string s) { if (Note(s)) _warn(s); }

    /// <summary>Each message is kept (and logged) once, however often the set-up runs.</summary>
    private bool Note(string s)
    {
        lock (_notes)
        {
            if (_notes.Contains(s)) return false;
            _notes.Add(s);
            return true;
        }
    }

    // ---------------------------------------------------------------- OpenRGB

    /// <summary>
    /// Writes RigControl's OpenRGB to Program Files if any file is missing or different. True when it is complete.
    /// Does nothing (false) before the user's yes, unless the copy is already there (then it is only kept up to date).
    /// </summary>
    public bool EnsureOpenRgbFiles()
    {
        lock (_run)
        lock (_files)
        {
            var files = Files("openrgb");
            if (files.Count == 0) return false;
            if (!Consent.AllHelpers && !OpenRgbCopyThere) return false;
            // Files that are already there are only trusted if nobody without administrator rights could have changed them.
            if (Directory.Exists(OwnOpenRgbDir)) RequireSafe(OwnOpenRgbDir);
            var stale = files.Where(f => !Matches(Target(f), f.Sha256)).ToList();
            if (stale.Count == 0) return true;

            // Files in use can't be replaced: stop an OpenRGB that runs from this folder first (the service is started again below).
            var exe = OwnOpenRgbExe;
            var restartService = false;
            if (File.Exists(exe))
            {
                if (ServiceUses(exe)) restartService = _sys.StopService("OpenRGB");
                _sys.StopProcessesRunning(exe);
            }

            foreach (var f in stale) Write(f, Target(f));
            var bad = files.Where(f => !Matches(Target(f), f.Sha256)).Select(f => f.Rel).ToList();
            if (bad.Count > 0) throw new InvalidOperationException("these files didn't come out right: " + string.Join(", ", bad));
            Info($"OpenRGB {(stale.Count == files.Count ? "set up" : "updated")} in {OwnOpenRgbDir} ({stale.Count} file(s) written, all checked).");
            if (restartService) StartService("OpenRGB");
            return true;
        }
    }

    /// <summary>
    /// The check before RigControl's own OpenRGB is started: it is the copy in RigControl's folder, that folder can't be
    /// written by programs without administrator rights, and every carried file is there unchanged (SHA-256).
    /// False when this build carries no OpenRGB (then there is nothing to compare with).
    /// Waits while the files are being written.
    /// </summary>
    /// <remarks>Hand <see cref="OwnCopyVerifier"/> to the lights, not this method: it is null when nothing can be compared.</remarks>
    public bool VerifyOpenRgbCopy(string executable)
    {
        lock (_files)
        {
            try
            {
                var files = Files("openrgb");
                if (files.Count == 0) return false;
                if (!string.Equals(Path.GetFullPath(executable), Path.GetFullPath(OwnOpenRgbExe), StringComparison.OrdinalIgnoreCase)) return false;
                if (_sys.OthersCanChange(OwnOpenRgbDir)) return false;
                return files.All(f => Matches(Target(f), f.Sha256));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The check the lights use before they start RigControl's own OpenRGB. Null when this build carries no OpenRGB:
    /// "cannot check" is not "wrong", and a copy an earlier build put there must still start.
    /// </summary>
    public Func<string, bool>? OwnCopyVerifier => Carries("openrgb") ? VerifyOpenRgbCopy : null;

    private string Target(BundledFile f) =>
        Path.Combine(OwnOpenRgbDir, f.Rel["openrgb/".Length..].Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// An OpenRGB service that runs from anywhere else (an older, separate install) is pointed at RigControl's copy;
    /// its settings folder comes along. The old files are left where they are. Only after the user's yes
    /// (the setup screen says that this is part of it).
    /// </summary>
    public void MoveOpenRgbService()
    {
        lock (_run)
        {
            var exe = OwnOpenRgbExe;
            if (_sys.OpenRgbServiceImagePath() is not { } image || ServiceUses(exe)) return;
            if (!Consent.AllHelpers) return;
            var (oldExe, args) = SplitCommand(image);
            var oldDir = Path.GetDirectoryName(oldExe);
            var oldConfig = oldDir is null ? null : Path.Combine(oldDir, "service_config");
            var newConfig = Path.Combine(OwnOpenRgbDir, "service_config");
            if (oldConfig is not null && Directory.Exists(oldConfig) && !Directory.Exists(newConfig))
            {
                CopyDirectory(oldConfig, newConfig, skip: "logs");
                Info("OpenRGB's settings were copied to RigControl's copy.");
            }
            var wasRunning = _sys.StopService("OpenRGB");
            var bin = "\"" + exe + "\"" + (args.Length > 0 ? " " + args : "");
            try
            {
                _sys.SetOpenRgbServiceImagePath(bin);
            }
            catch (Exception ex)
            {
                if (wasRunning) StartService("OpenRGB");
                throw new InvalidOperationException("Windows refused to change the OpenRGB service: " + ex.Message);
            }
            Info($"The OpenRGB service now runs RigControl's copy (was {oldExe}).");
            if (wasRunning) StartService("OpenRGB");
        }
    }

    /// <summary>"C:\path\app.exe" --a b  ->  (C:\path\app.exe, "--a b").</summary>
    internal static (string Exe, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        var i = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? (command, "") : (command[..(i + 4)], command[(i + 4)..].Trim());
    }

    private bool ServiceUses(string exe)
    {
        try
        {
            return _sys.OpenRgbServiceImagePath() is { } image && string.Equals(SplitCommand(image).Exe, exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- PawnIO and Equalizer APO

    public static bool PawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is not null;
    }

    public static bool EqualizerApoInstalled() => EqualizerApoFolder() is not null;

    internal static string? EqualizerApoFolder()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
        return key?.GetValue("InstallPath") is string path && Directory.Exists(path) ? path : null;
    }

    /// <summary>Installs the PawnIO driver when it is missing and carried, after the user's yes.</summary>
    public void EnsurePawnIo()
    {
        lock (_run)
        {
            if (_sys.PawnIoInstalled() || !Carries("pawnio")) return;
            if (!Consent.AllHelpers) return;
            var setup = Files("pawnio")[0];
            Info("PawnIO (sensors and fans) is missing: installing RigControl's copy.");
            var (code, output) = RunInstaller(setup, "-install -silent");
            if (code != 0 || !_sys.PawnIoInstalled()) throw new InvalidOperationException($"its setup ended with code {code}. {output.Trim()}");
            Info("PawnIO installed. Sensors and fans may need a RigControl restart to appear.");
        }
    }

    /// <summary>Installs Equalizer APO when it is missing and carried, after the user's yes.</summary>
    public void EnsureEqualizerApo()
    {
        lock (_run)
        {
            if (_sys.EqualizerApoInstalled() || !Carries("equalizerapo")) return;
            if (!Consent.AllHelpers) return;
            var setup = Files("equalizerapo")[0];
            Info("Equalizer APO (audio equaliser) is missing: installing RigControl's copy.");
            var (code, output) = RunInstaller(setup, "/S");
            if (code != 0 || !_sys.EqualizerApoInstalled()) throw new InvalidOperationException($"its setup ended with code {code}. {output.Trim()}");
            Info("Equalizer APO installed. Pick your sound output in its Device Selector, then restart the PC once.");
            _sys.OpenEqualizerApoDeviceSelector();
        }
    }

    /// <summary>Writes a carried installer to an admin-only folder, checks it, runs it, then deletes it.</summary>
    private (int Code, string Output) RunInstaller(BundledFile f, string args)
    {
        var dir = Path.Combine(_sys.Root, "installers");
        var path = Path.Combine(dir, Path.GetFileName(f.Rel));
        Write(f, path);
        try
        {
            if (!Matches(path, f.Sha256)) throw new InvalidOperationException("the installer didn't come out right");
            return _sys.Run(path, args, TimeSpan.FromMinutes(3));
        }
        finally
        {
            try { File.Delete(path); } catch { /* removed next time */ }
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Stops (in plain words) when programs without administrator rights could change what is in this folder.</summary>
    private void RequireSafe(string dir)
    {
        if (!_sys.OthersCanChange(dir)) return;
        throw new InvalidOperationException(UnsafeFolderMessage(dir, _sys.OpenFolder(dir)));
    }

    /// <summary>
    /// What to tell the user when a folder RigControl wants to write to is not safe. It names the folder that is
    /// really open, which is often one above (a RigControl folder that was moved into Program Files keeps its old
    /// owner), and only suggests deleting when the open folder is the one RigControl itself makes.
    /// </summary>
    internal static string UnsafeFolderMessage(string wanted, string? open)
    {
        static string Tidy(string p) => p.TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(open) || string.Equals(Tidy(open), Tidy(wanted), StringComparison.OrdinalIgnoreCase))
            return $"programs without administrator rights can change the folder {wanted}, so RigControl put nothing there and trusts nothing in it. " +
                   "Give that folder back to administrators only (or delete it) and restart RigControl.";
        return $"programs without administrator rights can change the folder {open}, so RigControl put nothing in {wanted} and trusts nothing in it. " +
               $"Make administrators the owner of {open} and let only administrators change it (a folder that was moved into Program Files keeps its old owner), then restart RigControl.";
    }

    private void Write(BundledFile f, string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        RequireSafe(dir);
        var tmp = path + ".new";
        using (var src = _payload.Open(f.Rel) ?? throw new FileNotFoundException("not carried", f.Rel))
        using (var dst = File.Create(tmp))
            src.CopyTo(dst);
        if (!Matches(tmp, f.Sha256))
        {
            File.Delete(tmp);
            throw new InvalidOperationException(f.Rel + " doesn't match its recorded SHA-256");
        }
        File.Move(tmp, path, overwrite: true);
    }

    internal static bool Matches(string path, string sha256)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var s = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(s)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void CopyDirectory(string from, string to, string skip)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(from))
            if (!string.Equals(Path.GetFileName(dir), skip, StringComparison.OrdinalIgnoreCase))
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)), skip);
    }

    private void StartService(string name)
    {
        try { _sys.StartService(name); }
        catch (Exception ex) { Warn($"The {name} service didn't start again: {ex.Message}"); }
    }
}

/// <summary>Where the carried files come from. The real one reads them out of RigControl.Bundled.dll; tests use their own.</summary>
internal interface IBundledPayload
{
    IReadOnlyList<BundledFile> Files(string kind);
    Stream? Open(string rel);
}

internal sealed class AssemblyPayload : IBundledPayload
{
    private const string Prefix = "bundled/";
    private readonly Assembly _assembly;

    public AssemblyPayload(Assembly assembly) => _assembly = assembly;

    public IReadOnlyList<BundledFile> Files(string kind) => _assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(a => a.Key.StartsWith("bundled-sha256:" + kind + "/", StringComparison.Ordinal) && a.Value is { Length: 64 })
        .Select(a => new BundledFile(a.Key["bundled-sha256:".Length..], a.Value!.ToUpperInvariant()))
        .Where(f => _assembly.GetManifestResourceInfo(Prefix + f.Rel) is not null)
        .ToList();

    public Stream? Open(string rel) => _assembly.GetManifestResourceStream(Prefix + rel);
}

/// <summary>Everything the set-up asks of, or does to, Windows. Tests replace it so they never touch the real PC.</summary>
internal class BundledSystem
{
    private const string OpenRgbServiceKey = @"SYSTEM\CurrentControlSet\Services\OpenRGB";

    public virtual string Root => BundledApps.Root;

    public virtual bool Elevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public virtual bool PawnIoInstalled() => BundledApps.PawnIoInstalled();

    public virtual bool EqualizerApoInstalled() => BundledApps.EqualizerApoInstalled();

    public virtual bool OthersCanChange(string path) => FolderGuard.OthersCanChange(path);
    /// <summary>The folder on the path that is open to ordinary programs (see <see cref="FolderGuard.FirstOpen"/>), or null.</summary>
    public virtual string? OpenFolder(string path) => FolderGuard.FirstOpen(path);

    /// <summary>The OpenRGB service's command line, or null when there is no such service.</summary>
    public virtual string? OpenRgbServiceImagePath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(OpenRgbServiceKey);
            return key?.GetValue("ImagePath") as string;
        }
        catch { return null; }
    }

    public virtual void SetOpenRgbServiceImagePath(string commandLine)
    {
        using var writable = Registry.LocalMachine.OpenSubKey(OpenRgbServiceKey, writable: true)
                             ?? throw new InvalidOperationException("the service vanished");
        writable.SetValue("ImagePath", commandLine, RegistryValueKind.ExpandString);
    }

    /// <summary>Stops a service. True when it was running (so it should be started again).</summary>
    public virtual bool StopService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status == ServiceControllerStatus.Stopped) return false;
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            return true;
        }
        catch { return false; }
    }

    public virtual void StartService(string name)
    {
        using var sc = new ServiceController(name);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    /// <summary>Ends every process that runs this exact file (so its files can be replaced).</summary>
    public virtual void StopProcessesRunning(string exe)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (p)
            {
                try { if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } }
                catch { /* not ours, or gone */ }
            }
    }

    public virtual void OpenEqualizerApoDeviceSelector()
    {
        try
        {
            if (BundledApps.EqualizerApoFolder() is { } path && File.Exists(Path.Combine(path, "DeviceSelector.exe")))
                Process.Start(new ProcessStartInfo(Path.Combine(path, "DeviceSelector.exe")) { UseShellExecute = false })?.Dispose();
        }
        catch { /* the user can open it from the Start menu */ }
    }

    public virtual (int Code, string Output) Run(string exe, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start " + Path.GetFileName(exe));
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(true); } catch { /* ignore */ }
            return (-1, "it took too long");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
