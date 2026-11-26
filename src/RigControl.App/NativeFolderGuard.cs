using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
/// </summary>
internal static class NativeFolderGuard
{
    /// <summary>Prints where the native parts were unpacked and whether that folder is open, then ends (testing aid).</summary>
    public const string PrintSwitch = "--print-extract-dir";

    /// <summary>Read by .NET's own start-up code: where a one-file exe unpacks its native parts.</summary>
    public const string ExtractBaseVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

    /// <summary>Set on the copy started by this check, so it never starts another one.</summary>
    public const string RelaunchedVariable = "RIGCONTROL_NATIVE_RELAUNCHED";

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
        string? expected = null;
        try { expected = ExpectedBase(UninstallEntry.Find()?.Layout, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)); }
        catch { /* no Program Files: nothing safe to offer */ }

        if (args.Any(a => string.Equals(a, PrintSwitch, StringComparison.OrdinalIgnoreCase)))
            return Print(elevated, bundle, extractDirs, expected);

        var decision = Decide(elevated, bundle, extractDirs, FolderGuard.FirstOpen, relaunched, fromEnvironment, expected);
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
                return Relaunch(args, expected!, decision.Reason);
            default:
                return Refuse(decision.Reason);
        }
    }

    private static int Relaunch(string[] args, string baseDir, string? reason)
    {
        if (PrepareFolder(baseDir) is { } problem) return Refuse(problem);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return Refuse("RigControl could not tell where its own exe is");
        try
        {
            // UseShellExecute=false: the new copy gets this process's administrator rights without another prompt.
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.SystemDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment[ExtractBaseVariable] = baseDir;
            psi.Environment[RelaunchedVariable] = "1";
            using var child = Process.Start(psi);
            if (child is null) return Refuse("RigControl could not start itself again");
            Log($"Native parts were unpacked to a folder other programs can change ({reason}); started again (process {child.Id}) unpacking into {baseDir}.");
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

    private static int Print(bool elevated, bool bundle, IReadOnlyList<string> extractDirs, string? expected)
    {
        try { AttachConsole(-1); } catch { /* output may be redirected instead */ }
        var lines = new List<string>
        {
            "Elevated: " + elevated,
            "Single-file bundle: " + bundle,
            "NATIVE_DLL_SEARCH_DIRECTORIES: " + (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "(none)"),
            ExtractBaseVariable + ": " + (Environment.GetEnvironmentVariable(ExtractBaseVariable) ?? "(not set)"),
            "Admin-only folder this copy would use: " + (expected ?? "(none)"),
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
        var decision = Decide(elevated, bundle, extractDirs, FolderGuard.FirstOpen,
            Environment.GetEnvironmentVariable(RelaunchedVariable) == "1", Environment.GetEnvironmentVariable(ExtractBaseVariable), expected);
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

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigControl", "logs");
            new RigControl.Core.Logging.FileLog(dir).For("native").Info(message);
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
