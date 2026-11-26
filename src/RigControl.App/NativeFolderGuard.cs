using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RigControl.App.Install;
using RigControl.Bundled;

namespace RigControl.App;

/// <summary>
/// Keeps the administrator's RigControl from loading native DLLs out of a folder other programs can write to.
///
/// Why: the release is one exe. Its native parts (WPF's vcruntime140_cor3.dll, PresentationNative_cor3.dll,
/// D3DCompiler_47_cor3.dll, PenImc_cor3.dll, WebView2Loader.dll…) are unpacked by .NET's start-up code before any
/// of RigControl's code runs, by default into %TEMP%\.net\RigControl\&lt;id&gt;, and files already there are used as
/// they are. %TEMP% belongs to the user, so any program the user runs could put its own DLL there and have it loaded
/// with administrator rights. .NET unpacks into DOTNET_BUNDLE_EXTRACT_BASE_DIR instead when that is set, so an
/// administrator's copy that finds itself unpacked into an open folder starts itself once more with that variable
/// pointing at a folder only administrators can change (&lt;install&gt;\runtime), and ends. The check runs from
/// <see cref="Program.Main"/> before any WPF or WebView2 type is touched, so nothing has been loaded from the open
/// folder yet. A copy without administrator rights gains nothing from a planted DLL and is left alone.
///
/// The same first check also covers two more ways in for a program running as the same user:
/// - Environment variables .NET reads at start to load extra code (a profiler DLL, a startup hook, another GC or
///   JIT, a diagnostics connection). An administrator's copy that finds one starts itself again without them (see
///   <see cref="LoadsCode"/>). A profiler named that way has already been loaded into this first process by .NET
///   before Main runs; nothing here can undo that, it only makes sure no administrator's RigControl keeps running
///   with it (SECURITY.md, "Known limits").
/// - Setup, update or uninstall run as administrator from a folder other programs can change (setup started from
///   Downloads): Windows looks for DLLs in the exe's own folder first, and the file itself could be swapped. Such a
///   copy copies itself into a new folder only administrators can change (&lt;install&gt;\updates\setup-&lt;random&gt;),
///   checks the copy, and carries on from there (see <see cref="StageCopy"/>).
/// All of these are done in one restart, and a copy started by this check never starts another one.
/// </summary>
internal static class NativeFolderGuard
{
    /// <summary>Prints where the native parts were unpacked and whether that folder is open, then ends (testing aid).</summary>
    public const string PrintSwitch = "--print-extract-dir";

    /// <summary>Read by .NET's own start-up code: where a one-file exe unpacks its native parts.</summary>
    public const string ExtractBaseVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

    /// <summary>Set on the copy started by this check, so it never starts another one.</summary>
    public const string RelaunchedVariable = "RIGCONTROL_NATIVE_RELAUNCHED";

    /// <summary>The name of a setup's admin-only copy folder under &lt;install&gt;\updates starts with this.</summary>
    public const string StagePrefix = "setup-";

    public enum Step { Continue, Relaunch, Refuse }

    /// <param name="Reason">Why, for the log (and the user, when refused).</param>
    public sealed record Decision(Step Step, string? Reason = null);

    /// <summary>
    /// What to do, from what was found. Pure, so the rules can be tested.
    /// </summary>
    /// <param name="elevated">Running with administrator rights.</param>
    /// <param name="bundle">Running as the one-file exe (only then are native parts unpacked).</param>
    /// <param name="extractDirs">The folders native DLLs are loaded from, other than the exe's own folder.</param>
    /// <param name="firstOpen">FolderGuard.FirstOpen: the open folder on a path, or null.</param>
    /// <param name="relaunched">This copy was started by the check already.</param>
    /// <param name="baseFromEnvironment">DOTNET_BUNDLE_EXTRACT_BASE_DIR as this copy sees it.</param>
    /// <param name="expectedBase">The admin-only folder this check unpacks into, or null when there is none.</param>
    public static Decision Decide(bool elevated, bool bundle, IReadOnlyList<string> extractDirs, Func<string, string?> firstOpen,
        bool relaunched, string? baseFromEnvironment, string? expectedBase)
    {
        if (!elevated || !bundle) return new Decision(Step.Continue);
        string? open = null;
        foreach (var dir in extractDirs)
            if (firstOpen(dir) is { } o) { open = o; break; }

        if (relaunched)
        {
            // Started by the check: everything must now be where it was sent, and closed to other programs. If not,
            // stop rather than load DLLs from an open folder or start yet another copy.
            if (expectedBase is null) return new Decision(Step.Refuse, "there is no folder only administrators can change to unpack into");
            if (!InstallLayout.SamePath(baseFromEnvironment, expectedBase))
                return new Decision(Step.Refuse, $"{ExtractBaseVariable} is \"{baseFromEnvironment}\", not {expectedBase}");
            foreach (var dir in extractDirs)
                if (!new InstallLayout(expectedBase).Contains(dir))
                    return new Decision(Step.Refuse, $"the native parts were unpacked to {dir}, outside {expectedBase}");
            return open is null ? new Decision(Step.Continue) : new Decision(Step.Refuse, "programs without administrator rights can change " + open);
        }

        if (open is null) return new Decision(Step.Continue);
        if (expectedBase is null) return new Decision(Step.Refuse, "programs without administrator rights can change " + open + ", and there is no safe folder to use instead");
        return new Decision(Step.Relaunch, "programs without administrator rights can change " + open);
    }

    // Read by .NET itself (the runtime or its start-up code) as DOTNET_<name> or COMPlus_<name>, and each one makes it
    // load or connect to code from elsewhere: startup hooks, extra dependency files, a package store, a profiler, a
    // different garbage collector or JIT, a diagnostics connection that can attach a profiler. EventPipe tracing is
    // blocked too: it writes a trace of the process (its own choice of events) to a file the variable names. So are
    // the JIT's text output file and the runtime's crash dump settings, which write files to a path the variable names.
    private static readonly string[] RuntimeKnobs = { "STARTUP_HOOKS", "ADDITIONAL_DEPS", "SHARED_STORE", "GCName", "GCPath", "JitPath", "JitName", "DiagnosticPorts", "EnableEventPipe", "JitStdOutFile" };
    private static readonly string[] RuntimeKnobPrefixes = { "PROFILER", "AltJit", "EventPipe", "DbgMiniDump", "DbgEnableMiniDump", "CreateDumpDiagnostics", "EnableCrashReport" };
    private static readonly string[] RuntimePrefixes = { "DOTNET_", "COMPlus_" };

    /// <summary>
    /// True for an environment variable that makes .NET load extra code into this process at start (case does not
    /// matter, as in Windows): every CORECLR_* (CORECLR_ENABLE_PROFILING, CORECLR_PROFILER, CORECLR_PROFILER_PATH…),
    /// COR_ENABLE_PROFILING and COR_PROFILER*, and DOTNET_/COMPlus_ STARTUP_HOOKS, ADDITIONAL_DEPS, SHARED_STORE,
    /// PROFILER*, GCName, GCPath, AltJit*, JitPath, JitName, DiagnosticPorts, EnableEventPipe and EventPipe*.
    /// </summary>
    public static bool LoadsCode(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        const StringComparison i = StringComparison.OrdinalIgnoreCase;
        if (name.StartsWith("CORECLR_", i) || name.Equals("COR_ENABLE_PROFILING", i) || name.StartsWith("COR_PROFILER", i)) return true;
        foreach (var prefix in RuntimePrefixes)
        {
            if (!name.StartsWith(prefix, i)) continue;
            var knob = name[prefix.Length..];
            if (RuntimeKnobs.Any(k => knob.Equals(k, i)) || RuntimeKnobPrefixes.Any(k => knob.StartsWith(k, i))) return true;
        }
        return false;
    }

    /// <summary>The names among these that <see cref="LoadsCode"/>, sorted, each once.</summary>
    public static IReadOnlyList<string> CodeLoadingVariables(IEnumerable<string> names)
        => names.Where(LoadsCode).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Takes every variable that <see cref="LoadsCode"/> out of an environment for a program about to be started.</summary>
    public static void RemoveCodeLoading(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(LoadsCode).ToList()) environment.Remove(name);
    }

    /// <summary>
    /// What to do about code-loading variables (see <see cref="LoadsCode"/>). Pure, so the rules can be tested.
    /// </summary>
    /// <param name="found">The ones this process was started with.</param>
    /// <param name="relaunched">This copy was started by the check already (and so without them).</param>
    public static Decision DecideEnvironment(bool elevated, IReadOnlyList<string> found, bool relaunched)
    {
        if (!elevated || found.Count == 0) return new Decision(Step.Continue);
        var names = string.Join(", ", found);
        if (relaunched) return new Decision(Step.Refuse, "it was started again without " + names + ", but they were set once more");
        return new Decision(Step.Relaunch, "it was started with " + names + " set, which make .NET load other code");
    }

    /// <summary>
    /// Whether an administrator's setup, update or uninstall runs from a folder other programs can change, and so must
    /// carry on from an admin-only copy of itself (see <see cref="StageCopy"/>). Pure, so the rules can be tested.
    /// RigControl itself (not setup) started from such a folder is left as it was: it warns in the log and on the
    /// Health page, and "Start with Windows" must keep pointing at the exe the user chose.
    /// </summary>
    /// <param name="installAction">Started as setup, update or uninstall (<see cref="InstallCommand.ActionAt"/>).</param>
    /// <param name="exeFolderOpen">FolderGuard.FirstOpen of the exe's own folder.</param>
    /// <param name="safeFolder">An admin-only folder to copy into exists (or can be made).</param>
    public static Decision DecideStage(bool elevated, bool bundle, bool installAction, string? exeFolderOpen, bool relaunched, bool safeFolder)
    {
        // A build that is not one file can't be copied as one file; development builds are never run like this.
        if (!elevated || !bundle || !installAction || exeFolderOpen is null) return new Decision(Step.Continue);
        if (relaunched) return new Decision(Step.Refuse, "its copy in a safe folder still runs from " + exeFolderOpen + ", which programs without administrator rights can change");
        if (!safeFolder) return new Decision(Step.Refuse, "it runs from " + exeFolderOpen + ", which programs without administrator rights can change, and there is no safe folder to copy it to");
        return new Decision(Step.Relaunch, "it runs from " + exeFolderOpen + ", which programs without administrator rights can change");
    }

    /// <summary>One answer from several: any refusal wins, then any restart (one restart fixes them all), else carry on.</summary>
    public static Decision Combine(params Decision[] decisions)
    {
        if (decisions.FirstOrDefault(d => d.Step == Step.Refuse) is { } refuse) return refuse;
        var restarts = decisions.Where(d => d.Step == Step.Relaunch).ToList();
        if (restarts.Count == 0) return new Decision(Step.Continue);
        return new Decision(Step.Relaunch, string.Join("; ", restarts.Select(d => d.Reason).Where(r => !string.IsNullOrEmpty(r))));
    }

    /// <summary>
    /// The folder an administrator's copy unpacks into: &lt;install&gt;\runtime when RigControl is installed (the
    /// install folder comes from Windows' Installed apps entry, which only administrators can write), otherwise
    /// %ProgramFiles%\RigControl\runtime.
    /// </summary>
    public static string ExpectedBase(InstallLayout? installed, string programFiles)
        => installed?.RuntimeDir ?? new InstallLayout(InstallLayout.DefaultRoot(programFiles)).RuntimeDir;

    /// <summary>
    /// Runs the check. Returns an exit code when this process must end now (a copy was started in its place, the
    /// folder is not safe, or <see cref="PrintSwitch"/> was asked for), or null to carry on starting RigControl.
    /// Touches no WPF or WebView2 type.
    /// </summary>
    public static int? Check(string[] args)
    {
        var elevated = IsElevated();
        var bundle = IsBundle();
        var extractDirs = ExtractDirs();
        var relaunched = Environment.GetEnvironmentVariable(RelaunchedVariable) == "1";
        var fromEnvironment = Environment.GetEnvironmentVariable(ExtractBaseVariable);
        var found = CodeLoadingVariables(Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(k => k.ToString() ?? string.Empty));
        // Whatever is decided below, nothing this process starts (the elevated setup, the installed copy, an update,
        // the copy started by this check) inherits them. A profiler named by them is already loaded into this
        // process: .NET did that before Main, and only a restart without them gets rid of it.
        foreach (var name in found) Environment.SetEnvironmentVariable(name, null);

        InstalledCopy? installed = null;
        try { installed = UninstallEntry.Find(); }
        catch { /* not installed, as far as this copy can tell */ }
        InstallLayout? safeLayout = null;
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (installed is not null || !string.IsNullOrEmpty(programFiles))
                safeLayout = installed?.Layout ?? new InstallLayout(InstallLayout.DefaultRoot(programFiles));
        }
        catch { /* no Program Files: nothing safe to offer */ }
        var expected = safeLayout?.RuntimeDir;

        var exe = Environment.ProcessPath;
        var installAction = InstallCommand.ActionAt(args, exe, installed) != StartAction.RunApp;
        string? exeFolderOpen = null;
        if (elevated && bundle && installAction)
            exeFolderOpen = Path.GetDirectoryName(exe) is { Length: > 0 } exeDir ? FolderGuard.FirstOpen(exeDir) : "(an unknown folder)";

        var envDecision = DecideEnvironment(elevated, found, relaunched);
        var stageDecision = DecideStage(elevated, bundle, installAction, exeFolderOpen, relaunched, safeLayout is not null);
        var nativeDecision = Decide(elevated, bundle, extractDirs, FolderGuard.FirstOpen, relaunched, fromEnvironment, expected);
        var decision = Combine(envDecision, stageDecision, nativeDecision);

        if (args.Any(a => string.Equals(a, PrintSwitch, StringComparison.OrdinalIgnoreCase)))
            return Print(elevated, bundle, extractDirs, expected, found, exeFolderOpen, decision);

        if (found.Count > 0)
            Log((elevated ? "WARNING: started as administrator with " : "Started with ") + string.Join(", ", found) +
                " set. These make .NET load other code into RigControl; a profiler named by them was already loaded into this process" +
                (elevated ? ", so RigControl starts again without them." : ". They are removed for every program RigControl starts."), warn: elevated);

        switch (decision.Step)
        {
            case Step.Continue:
                // Later programs RigControl starts (an update, the installed copy) make their own check.
                if (relaunched)
                {
                    Environment.SetEnvironmentVariable(RelaunchedVariable, null);
                    Environment.SetEnvironmentVariable(ExtractBaseVariable, null);
                }
                return null;
            case Step.Relaunch:
                // A one-file exe started again always unpacks into the admin-only folder, whatever the reason for the
                // restart: the copy started here checks that strictly and refuses rather than start yet another one.
                if (bundle && expected is null) return Refuse("there is no folder only administrators can change to unpack into");
                return Relaunch(args, bundle ? expected : null, stageDecision.Step == Step.Relaunch ? safeLayout : null, decision.Reason);
            default:
                return Refuse(decision.Reason);
        }
    }

    /// <summary>The folders this process's native parts were unpacked to (empty when nothing was unpacked).</summary>
    public static IReadOnlyList<string> CurrentExtractDirs() => ExtractDirs();

    /// <param name="baseDir">Where the new copy unpacks its native parts, or null (not a one-file exe).</param>
    /// <param name="stageIn">When set, the new copy runs from an admin-only copy of this exe under its updates folder.</param>
    private static int Relaunch(string[] args, string? baseDir, InstallLayout? stageIn, string? reason)
    {
        if (baseDir is not null && PrepareFolder(baseDir) is { } problem) return Refuse(problem);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return Refuse("RigControl could not tell where its own exe is");
        var runFrom = exe;
        if (stageIn is not null)
        {
            if (StageCopy(exe, stageIn.UpdatesDir, out var stageProblem) is not { } staged) return Refuse(stageProblem);
            runFrom = staged;
        }
        try
        {
            // UseShellExecute=false: the new copy gets this process's administrator rights without another prompt.
            var psi = new ProcessStartInfo(runFrom)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(runFrom) ?? Environment.SystemDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // Check already took them out of this process; done again here so no restart can ever pass one on.
            RemoveCodeLoading(psi.Environment);
            if (baseDir is not null) psi.Environment[ExtractBaseVariable] = baseDir;
            else psi.Environment.Remove(ExtractBaseVariable);
            psi.Environment[RelaunchedVariable] = "1";
            using var child = Process.Start(psi);
            if (child is null) return Refuse("RigControl could not start itself again");
            Log($"Started again (process {child.Id}) because {reason}" +
                (baseDir is null ? "" : $"; unpacking into {baseDir}") +
                (stageIn is null ? "" : $"; running from {runFrom}") + ".");
            // The start-up task helpers report success through their exit code, so their caller gets the real one.
            if (args.Any(a => string.Equals(a, Autostart.InstallSwitch, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(a, Autostart.RemoveSwitch, StringComparison.OrdinalIgnoreCase)))
            {
                child.WaitForExit();
                return child.ExitCode;
            }
            return 0;
        }
        catch (Exception ex)
        {
            return Refuse("RigControl could not start itself again: " + ex.Message);
        }
    }

    /// <summary>
    /// Copies the running exe into a new folder of its own under <paramref name="updatesDir"/> (which only
    /// administrators can change), checks that the copy has the same bytes and is closed to other programs, and
    /// returns its path; null with <paramref name="problem"/> when that did not work. The copy is named RigControl.exe
    /// so it unpacks its native parts into the same folder as the installed exe. The installed copy removes these
    /// folders once they are no longer in use (<see cref="StartupCleanup"/>); the uninstaller removes updates\ whole.
    /// </summary>
    public static string? StageCopy(string exe, string updatesDir, out string? problem)
    {
        problem = PrepareFolder(updatesDir);
        if (problem is not null) return null;
        try
        {
            // The source is held open from here on without letting anyone write to, rename or delete it. Between the
            // admin prompt and this line the file at that path could have been renamed and another put in its place
            // (Windows lets a running exe be renamed, not changed), so the file held open must be the very file this
            // process runs from; it is then copied from that same handle.
            using var source = OpenStageSource(exe);
            if (RunningImageNtPath() is not { } image || !SameFile(source.SafeFileHandle, image))
            {
                problem = exe + " is no longer the file this setup was started from";
                return null;
            }

            var dir = Path.Combine(updatesDir, StagePrefix + RandomNumberGenerator.GetHexString(16, lowercase: true));
            // A folder made here and now, so nothing can have been put in it beforehand.
            if (Directory.Exists(dir) || File.Exists(dir)) { problem = dir + " is there already"; return null; }
            var info = Directory.CreateDirectory(dir);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { problem = dir + " is a link to another folder"; return null; }
            if (FolderGuard.FirstOpen(dir) is { } openDir) { problem = "programs without administrator rights can change " + openDir; return null; }

            var target = Path.Combine(dir, InstallLayout.ExeName);
            byte[] sourceHash;
            using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    copy.Write(buffer, 0, n);
                }
                sourceHash = hash.GetHashAndReset();
            }
            byte[] copyHash;
            using (var check = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)) copyHash = SHA256.HashData(check);
            if (!sourceHash.AsSpan().SequenceEqual(copyHash))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* removed with the next clean-up */ }
                problem = "its copy in " + dir + " does not match the original";
                return null;
            }
            if (FolderGuard.FirstOpen(target) is { } openFile) { problem = "programs without administrator rights can change " + openFile; return null; }
            return target;
        }
        catch (Exception ex)
        {
            problem = "it could not copy itself to " + updatesDir + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Opens a setup file to copy it: others may read it, but nobody may write to, rename or delete it while it is
    /// open.
    /// </summary>
    internal static FileStream OpenStageSource(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);

    /// <summary>
    /// True when the file open on <paramref name="source"/> is the file named by <paramref name="runningImageNtPath"/>
    /// (the running program's image, from <see cref="RunningImageNtPath"/>). Windows keeps an image's name up to date
    /// when the file is renamed, so a file renamed away and replaced by another at the same path does not match.
    /// When the two names are written differently (an 8.3 short name, say), the image's name is opened and the two
    /// files' volume and file ids are compared instead.
    /// </summary>
    internal static bool SameFile(SafeFileHandle source, string runningImageNtPath)
    {
        if (string.IsNullOrEmpty(runningImageNtPath) || NtPath(source) is not { } held) return false;
        if (string.Equals(held, runningImageNtPath, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var image = File.OpenHandle(@"\\?\GLOBALROOT" + runningImageNtPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return FileId(source) is { } a && FileId(image) is { } b && a == b;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The NT path (\Device\HarddiskVolumeN\…) of the file open on <paramref name="handle"/>, or null.</summary>
    internal static string? NtPath(SafeFileHandle handle)
    {
        const uint volumeNameNt = 0x2; // FILE_NAME_NORMALIZED | VOLUME_NAME_NT
        var buffer = new char[512];
        for (var tries = 0; tries < 3; tries++)
        {
            var n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, volumeNameNt);
            if (n == 0) return null;
            if (n < buffer.Length) return new string(buffer, 0, (int)n);
            buffer = new char[n + 1];
        }
        return null;
    }

    /// <summary>The NT path of the file mapped at <paramref name="address"/> in this process, or null.</summary>
    internal static string? MappedNtPath(IntPtr address)
    {
        var buffer = new char[1024];
        for (var tries = 0; tries < 3; tries++)
        {
            var n = K32GetMappedFileNameW(GetCurrentProcess(), address, buffer, (uint)buffer.Length);
            if (n == 0) return null;
            // A full buffer may mean the name was cut short.
            if (n < buffer.Length - 1) return new string(buffer, 0, (int)n);
            buffer = new char[buffer.Length * 4];
        }
        return null;
    }

    /// <summary>The NT path of the file this process runs from (its mapped exe image), or null.</summary>
    internal static string? RunningImageNtPath()
    {
        var module = GetModuleHandleW(null);
        return module == IntPtr.Zero ? null : MappedNtPath(module);
    }

    private static (uint Volume, ulong Index)? FileId(SafeFileHandle handle)
        => GetFileInformationByHandle(handle, out var info)
            ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
            : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint K32GetMappedFileNameW(IntPtr process, IntPtr address, [Out] char[] name, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    /// <summary>
    /// Makes the folder (as administrator) and checks it. When RigControl's own folder is made new here, it is limited
    /// to administrators the way the installer does it. Null when the folder is fine, otherwise why not.
    /// </summary>
    public static string? PrepareFolder(string baseDir)
    {
        try
        {
            var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir)));
            if (string.IsNullOrEmpty(root)) return "the folder " + baseDir + " can't be used";
            var existed = Directory.Exists(root);
            Directory.CreateDirectory(root);
            if (!existed && FolderGuard.FirstOpen(root) is not null) InstallFolderAccess.LockToAdministrators(root);
            Directory.CreateDirectory(baseDir);
            // FolderGuard also counts a link (reparse point) anywhere on the path as open.
            if (new DirectoryInfo(baseDir).Attributes.HasFlag(FileAttributes.ReparsePoint)) return baseDir + " is a link to another folder";
            if (FolderGuard.FirstOpen(baseDir) is { } open) return "programs without administrator rights can change " + open;
            return null;
        }
        catch (Exception ex)
        {
            return baseDir + " could not be made: " + ex.Message;
        }
    }

    /// <summary>
    /// A folder only administrators can change, for short-lived files an administrator's RigControl hands to another
    /// program (the start-up task's definition). Null when there is none; <paramref name="problem"/> says why.
    /// </summary>
    public static string? AdminFolder(out string? problem)
    {
        string? baseDir;
        try { baseDir = ExpectedBase(UninstallEntry.Find()?.Layout, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)); }
        catch (Exception ex) { problem = ex.Message; return null; }
        problem = PrepareFolder(baseDir);
        return problem is null ? baseDir : null;
    }

    private static int Refuse(string? reason)
    {
        var text = "RigControl did not start, because it runs as administrator and " + (reason ?? "its files are not safe") + ".\n\n" +
                   "Reinstall RigControl (it goes to C:\\Program Files\\RigControl by default) and try again.";
        Log("Not started: " + text);
        try { MessageBoxW(IntPtr.Zero, text, "RigControl", 0x10 /* MB_ICONERROR */); } catch { /* no desktop to show it on */ }
        return 3;
    }

    private static int Print(bool elevated, bool bundle, IReadOnlyList<string> extractDirs, string? expected,
        IReadOnlyList<string> codeLoading, string? exeFolderOpen, Decision decision)
    {
        try { AttachConsole(-1); } catch { /* output may be redirected instead */ }
        var lines = new List<string>
        {
            "Elevated: " + elevated,
            "Single-file bundle: " + bundle,
            "NATIVE_DLL_SEARCH_DIRECTORIES: " + (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "(none)"),
            ExtractBaseVariable + ": " + (Environment.GetEnvironmentVariable(ExtractBaseVariable) ?? "(not set)"),
            "Admin-only folder this copy would use: " + (expected ?? "(none)"),
            "Variables that load other code (removed for every program this copy starts): " + (codeLoading.Count == 0 ? "(none)" : string.Join(", ", codeLoading)),
            "Setup, update or uninstall from a folder other programs can change: " + (exeFolderOpen ?? "no"),
        };
        if (extractDirs.Count == 0) lines.Add("Native parts unpacked to: (nowhere; nothing is unpacked)");
        foreach (var dir in extractDirs)
            lines.Add($"Native parts unpacked to: {dir} | open to other programs: {(FolderGuard.FirstOpen(dir) is { } o ? "YES (" + o + ")" : "no")}");
        // Proof that the check runs first: nothing should have been loaded from the unpack folder yet.
        var loaded = new List<string>();
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (ProcessModule m in self.Modules)
                if (extractDirs.Any(d => new InstallLayout(d).Contains(m.FileName))) loaded.Add(Path.GetFileName(m.FileName));
        }
        catch (Exception ex) { loaded.Add("(could not be listed: " + ex.Message + ")"); }
        lines.Add("Loaded from the unpack folder so far: " + (loaded.Count == 0 ? "(nothing)" : string.Join(", ", loaded)));
        lines.Add("Decision: " + decision.Step + (decision.Reason is null ? "" : " (" + decision.Reason + ")"));
        Console.Out.WriteLine(string.Join(Environment.NewLine, lines));
        Console.Out.Flush();
        return 0;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return true; // unknown: the safe answer is to check
        }
    }

    private static bool IsBundle()
    {
        // A one-file exe's assemblies have no file of their own.
#pragma warning disable IL3000
        return string.IsNullOrEmpty(typeof(NativeFolderGuard).Assembly.Location);
#pragma warning restore IL3000
    }

    /// <summary>The folders .NET loads native DLLs from, other than the exe's own folder.</summary>
    private static IReadOnlyList<string> ExtractDirs()
    {
        var own = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var list = new List<string>();
        foreach (var raw in (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? string.Empty).Split(Path.PathSeparator))
        {
            var dir = raw.Trim();
            if (dir.Length == 0 || InstallLayout.SamePath(dir, own) || InstallLayout.SamePath(dir, AppContext.BaseDirectory)) continue;
            if (!list.Any(d => InstallLayout.SamePath(d, dir))) list.Add(Path.TrimEndingDirectorySeparator(dir));
        }
        return list;
    }

    private static void Log(string message, bool warn = false)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigControl", "logs");
            var log = new RigControl.Core.Logging.FileLog(dir).For("native");
            if (warn) log.Warn(message);
            else log.Info(message);
        }
        catch
        {
            // Logging is a courtesy here.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
