using System.IO;
using System.Security.Cryptography;
using System.Text;
using RigControl.Bundled;

namespace RigControl.App.Tests;

/// <summary>
/// The helper programs are set up together after one yes, never before. Nothing here touches the real PC: pretend
/// installers, files in a temp folder.
/// </summary>
public sealed class BundledConsentTests : IDisposable
{
    private sealed class FakePayload : IBundledPayload
    {
        public Dictionary<string, byte[]> Content { get; } = new()
        {
            ["openrgb/OpenRGB.exe"] = Encoding.UTF8.GetBytes("openrgb program"),
            ["openrgb/plugins/Qt.dll"] = Encoding.UTF8.GetBytes("a library"),
            ["pawnio/PawnIO_setup.exe"] = Encoding.UTF8.GetBytes("pawnio setup"),
            ["equalizerapo/EqualizerAPO-x64-1.4.2.exe"] = Encoding.UTF8.GetBytes("equalizer setup"),
        };

        public IReadOnlyList<BundledFile> Files(string kind) => Content.Where(c => c.Key.StartsWith(kind + "/", StringComparison.Ordinal))
            .Select(c => new BundledFile(c.Key, Convert.ToHexString(SHA256.HashData(c.Value)))).ToList();

        public Stream? Open(string rel) => Content.TryGetValue(rel, out var bytes) ? new MemoryStream(bytes) : null;
    }

    private sealed class FakeSystem : BundledSystem
    {
        public FakeSystem(string root) => RootFolder = root;
        public string RootFolder { get; }
        public bool IsElevated { get; set; } = true;
        public bool PawnIo { get; set; }
        public bool EqualizerApo { get; set; }
        public bool Unsafe { get; set; }
        public string? ServiceImagePath { get; set; }
        public List<string> Ran { get; } = new();
        public List<string> Did { get; } = new();

        public override string Root => RootFolder;
        public override bool Elevated => IsElevated;
        public override bool PawnIoInstalled() => PawnIo;
        public override bool EqualizerApoInstalled() => EqualizerApo;
        public override bool OthersCanChange(string path) => Unsafe;
        public string? OpenAt { get; set; }
        public override string? OpenFolder(string path) => Unsafe ? OpenAt ?? path : null;
        public override string? OpenRgbServiceImagePath() => ServiceImagePath;
        public override void SetOpenRgbServiceImagePath(string commandLine) { Did.Add("service path"); ServiceImagePath = commandLine; }
        public override bool StopService(string name) { Did.Add("stop " + name); return true; }
        public override void StartService(string name) => Did.Add("start " + name);
        public override void StopProcessesRunning(string exe) => Did.Add("end " + Path.GetFileName(exe));
        public override void OpenEqualizerApoDeviceSelector() => Did.Add("device selector");

        public override (int Code, string Output) Run(string exe, string args, TimeSpan timeout)
        {
            Assert.True(File.Exists(exe)); // the installer is on disk (and checked) while it runs
            var name = Path.GetFileName(exe);
            Ran.Add(name + " " + args);
            if (name.StartsWith("PawnIO", StringComparison.Ordinal)) PawnIo = true;
            if (name.StartsWith("EqualizerAPO", StringComparison.Ordinal)) EqualizerApo = true;
            return (0, "");
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rigcontrol-bundled-" + Guid.NewGuid().ToString("N"));
    private readonly FakePayload _payload = new();
    private readonly FakeSystem _sys;
    private readonly List<string> _log = new();

    public BundledConsentTests() => _sys = new FakeSystem(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* never created, or best effort */ }
    }

    private string OwnExe => Path.Combine(_root, "OpenRGB", "OpenRGB.exe");

    private BundledApps Apps(bool yes = false) =>
        new(s => _log.Add(s), s => _log.Add("WARN " + s), _payload, _sys, new BundledConsent { AllHelpers = yes });

    /// <summary>The state of a PC where an earlier RigControl set everything up.</summary>
    private void EverythingIsAlreadyThere()
    {
        Directory.CreateDirectory(Path.Combine(_root, "OpenRGB", "plugins"));
        File.WriteAllText(OwnExe, "openrgb program");
        File.WriteAllText(Path.Combine(_root, "OpenRGB", "plugins", "Qt.dll"), "a library");
        _sys.ServiceImagePath = $"\"{OwnExe}\"";
        _sys.PawnIo = true;
        _sys.EqualizerApo = true;
    }

    [Fact]
    public void Nothing_is_installed_unpacked_or_changed_before_the_user_pressed_the_button()
    {
        _sys.ServiceImagePath = "\"C:\\Program Files\\OpenRGB\\OpenRGB.exe\" --server";
        var apps = Apps();
        apps.EnsureAll();

        Assert.Empty(_sys.Ran);
        Assert.Empty(_sys.Did);
        Assert.False(Directory.Exists(_root));
        Assert.Equal("\"C:\\Program Files\\OpenRGB\\OpenRGB.exe\" --server", _sys.ServiceImagePath);
        Assert.False(_sys.PawnIo);
        Assert.False(_sys.EqualizerApo);
        // It says so in plain words, once, however often it runs.
        apps.EnsureAll();
        Assert.Single(_log, l => l.Contains("RigControl needs OpenRGB, PawnIO, Equalizer APO") && l.Contains("Install and continue"));
        Assert.Single(_log, l => l.Contains("OpenRGB service") && l.Contains("leaves it as it is"));

        var summary = apps.Summary();
        Assert.True(summary.NeedsYes);
        Assert.True(summary.Pending);
        Assert.False(summary.Consented);
        Assert.True(summary.ServiceWillMove);
        Assert.All(summary.Items, i => Assert.False(i.Installed));
    }

    [Fact]
    public void One_yes_installs_every_helper_and_removes_the_installers_afterwards()
    {
        var apps = Apps();
        apps.GiveConsent();
        apps.EnsureAll();

        Assert.Equal(new[] { "PawnIO_setup.exe -install -silent", "EqualizerAPO-x64-1.4.2.exe /S" }, _sys.Ran);
        Assert.True(_sys.PawnIo);
        Assert.True(_sys.EqualizerApo);
        Assert.Equal("openrgb program", File.ReadAllText(OwnExe));
        Assert.Equal("a library", File.ReadAllText(Path.Combine(_root, "OpenRGB", "plugins", "Qt.dll")));
        Assert.Equal(new[] { "device selector" }, _sys.Did); // Equalizer APO's device picker; no service on this PC to move
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "installers")));

        var summary = apps.Summary();
        Assert.All(summary.Items, i => Assert.True(i.Installed));
        Assert.True(summary.Consented);
        Assert.False(summary.NeedsYes);
        Assert.False(summary.Pending);
        Assert.Empty(summary.Problems);

        // Another start installs nothing again.
        _sys.Ran.Clear();
        _sys.Did.Clear();
        apps.EnsureAll();
        Assert.Empty(_sys.Ran);
        Assert.Empty(_sys.Did);
    }

    [Fact]
    public void The_same_yes_moves_an_existing_OpenRGB_service_to_RigControls_copy_and_its_settings_come_along()
    {
        var old = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(old, "service_config", "logs"));
        File.WriteAllText(Path.Combine(old, "service_config", "OpenRGB.json"), "{}");
        File.WriteAllText(Path.Combine(old, "service_config", "logs", "a.log"), "log");
        _sys.ServiceImagePath = $"\"{Path.Combine(old, "OpenRGB.exe")}\" --server --server-port 6742";
        _sys.PawnIo = true;
        _sys.EqualizerApo = true;

        var apps = Apps();
        Assert.True(apps.Summary().ServiceWillMove);
        apps.EnsureAll();
        Assert.DoesNotContain("service path", _sys.Did); // not before the yes

        apps.GiveConsent();
        apps.EnsureAll();
        Assert.Equal($"\"{OwnExe}\" --server --server-port 6742", _sys.ServiceImagePath);
        Assert.Equal(new[] { "stop OpenRGB", "service path", "start OpenRGB" }, _sys.Did);
        Assert.True(File.Exists(Path.Combine(_root, "OpenRGB", "service_config", "OpenRGB.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "OpenRGB", "service_config", "logs")));
        Assert.True(File.Exists(Path.Combine(old, "service_config", "OpenRGB.json"))); // the old files stay
        Assert.False(apps.Summary().ServiceWillMove);
        Assert.False(apps.Summary().Pending);
        Assert.True(apps.Status().OpenRgbServiceUsesIt);
    }

    [Fact]
    public void A_PC_that_is_already_set_up_counts_as_a_yes_is_never_asked_and_nothing_is_installed_again()
    {
        EverythingIsAlreadyThere();
        File.WriteAllText(Path.Combine(_root, "OpenRGB", "plugins", "Qt.dll"), "an older library");

        var apps = Apps(); // no yes stored at all
        var summary = apps.Summary();
        Assert.True(summary.Consented);
        Assert.False(summary.NeedsYes);
        Assert.False(summary.Pending);
        Assert.False(summary.ServiceWillMove);
        Assert.All(summary.Items, i => Assert.True(i.Installed));

        // RigControl's own copy is still brought up to date; no installer runs and the service is not re-pointed.
        apps.EnsureAll();
        Assert.Empty(_sys.Ran);
        Assert.Equal("a library", File.ReadAllText(Path.Combine(_root, "OpenRGB", "plugins", "Qt.dll")));
        Assert.Equal(new[] { "stop OpenRGB", "end OpenRGB.exe", "start OpenRGB" }, _sys.Did);
        Assert.Equal($"\"{OwnExe}\"", _sys.ServiceImagePath);
        Assert.DoesNotContain(_log, l => l.Contains("Install and continue"));
        Assert.False(apps.Consent.AllHelpers); // looking at the PC never writes a yes

        // And when everything matches, a start changes nothing at all.
        _sys.Did.Clear();
        apps.EnsureAll();
        Assert.Empty(_sys.Did);
    }

    [Fact]
    public void A_PC_that_has_some_helpers_is_still_asked_and_the_missing_one_is_not_installed_before_the_yes()
    {
        EverythingIsAlreadyThere();
        _sys.EqualizerApo = false;

        var apps = Apps();
        Assert.True(apps.Summary().NeedsYes);
        apps.EnsureAll();
        Assert.Empty(_sys.Ran);
        Assert.Single(_log, l => l.Contains("RigControl needs Equalizer APO, which is not set up"));

        apps.GiveConsent();
        apps.EnsureAll();
        Assert.Equal(new[] { "EqualizerAPO-x64-1.4.2.exe /S" }, _sys.Ran); // only what was missing
        Assert.False(apps.Summary().Pending);
    }

    [Fact]
    public void Nothing_is_written_or_trusted_in_a_folder_that_ordinary_programs_can_change()
    {
        _sys.Unsafe = true;
        var apps = Apps(yes: true);
        apps.EnsureAll();
        Assert.Empty(_sys.Ran);
        Assert.False(File.Exists(OwnExe));
        Assert.False(_sys.PawnIo);
        Assert.Equal(3, _log.Count(l => l.StartsWith("WARN ") && l.Contains("without administrator rights")));
        Assert.Equal(3, apps.Summary().Problems.Count);
        Assert.True(apps.Summary().Pending);
        Assert.False(apps.Summary().NeedsYes); // the yes was given; it is the set-up that failed

        // Files that are already there (and even match) are not trusted either.
        _sys.Unsafe = false;
        apps.EnsureOpenRgbFiles();
        Assert.True(apps.VerifyOpenRgbCopy(OwnExe));
        _sys.Unsafe = true;
        Assert.False(apps.VerifyOpenRgbCopy(OwnExe));
        Assert.Throws<InvalidOperationException>(() => apps.EnsureOpenRgbFiles());
    }

    [Fact]
    public void What_went_wrong_is_told_until_a_later_try_works()
    {
        _sys.Unsafe = true;
        var apps = Apps(yes: true);
        apps.EnsureAll();
        Assert.Contains(apps.Summary().Problems, p => p.StartsWith("PawnIO: ", StringComparison.Ordinal));

        _sys.Unsafe = false;
        apps.EnsureAll();
        Assert.Empty(apps.Summary().Problems);
        Assert.False(apps.Summary().Pending);
    }

    [Fact]
    public void RigControls_OpenRGB_copy_only_passes_the_check_when_every_carried_file_is_unchanged()
    {
        var apps = Apps(yes: true);
        Assert.False(apps.VerifyOpenRgbCopy(OwnExe)); // not unpacked yet
        Assert.True(apps.EnsureOpenRgbFiles());
        Assert.True(apps.VerifyOpenRgbCopy(OwnExe));
        Assert.True(apps.VerifyOpenRgbCopy(OwnExe.ToUpperInvariant()));
        Assert.False(apps.VerifyOpenRgbCopy(Path.Combine(_root, "Other", "OpenRGB.exe")));

        File.WriteAllText(Path.Combine(_root, "OpenRGB", "plugins", "Qt.dll"), "swapped");
        Assert.False(apps.VerifyOpenRgbCopy(OwnExe));
        File.WriteAllText(Path.Combine(_root, "OpenRGB", "plugins", "Qt.dll"), "a library");
        Assert.True(apps.VerifyOpenRgbCopy(OwnExe));
        File.Delete(Path.Combine(_root, "OpenRGB", "plugins", "Qt.dll"));
        Assert.False(apps.VerifyOpenRgbCopy(OwnExe));
    }

    [Fact]
    public void A_build_that_carries_nothing_skips_the_step_installs_nothing_and_says_what_is_missing()
    {
        _payload.Content.Clear();
        var apps = Apps(yes: true);
        apps.EnsureAll();
        Assert.Empty(_sys.Ran);
        Assert.Empty(_sys.Did);
        Assert.False(Directory.Exists(_root));

        var unanswered = Apps().Summary();
        Assert.False(unanswered.NeedsYes); // nothing to say yes to: the setup step is skipped
        Assert.False(unanswered.Pending);
        Assert.All(unanswered.Items, i => Assert.False(i.Carried));
        Assert.Equal(new[] { "OpenRGB", "PawnIO", "Equalizer APO" }, unanswered.NotCarried);

        // What is on the PC anyway is not reported as missing.
        _sys.PawnIo = true;
        _sys.ServiceImagePath = "C:\\Tools\\OpenRGB.exe --server";
        Assert.Equal(new[] { "Equalizer APO" }, apps.Summary().NotCarried);
        Assert.False(apps.Summary().ServiceWillMove); // no copy to move the service to
        apps.EnsureAll();
        Assert.Equal("C:\\Tools\\OpenRGB.exe --server", _sys.ServiceImagePath);

        // Such a build cannot vouch for a copy an earlier build left, so it hands the lights no check at all: that
        // copy is still started (and still held to the "only administrators can change its folder" rule there).
        Directory.CreateDirectory(Path.GetDirectoryName(OwnExe)!);
        File.WriteAllText(OwnExe, "openrgb program");
        Assert.False(apps.VerifyOpenRgbCopy(OwnExe));
        Assert.Null(apps.OwnCopyVerifier);
    }

    [Fact]
    public void Without_administrator_rights_the_yes_is_not_taken_and_nothing_is_set_up()
    {
        _sys.IsElevated = false;
        var apps = Apps();
        var saved = false;
        apps.ConsentChanged = _ => saved = true;

        var refused = Assert.Throws<InvalidOperationException>(apps.GiveConsent);
        Assert.Contains("administrator", refused.Message);
        Assert.False(apps.Consent.AllHelpers);
        Assert.False(saved);

        // Even a yes that an elevated copy stored earlier installs nothing from a copy that is not elevated.
        var withYes = Apps(yes: true);
        withYes.EnsureAll();
        Assert.Empty(_sys.Ran);
        Assert.False(Directory.Exists(_root));
        Assert.Contains(_log, l => l.Contains("administrator"));
    }

    [Fact]
    public void A_carried_file_that_does_not_match_its_recorded_hash_is_never_written_or_run()
    {
        var real = _payload.Files("pawnio")[0];
        var apps = new BundledApps(s => _log.Add(s), s => _log.Add("WARN " + s), new Tampered(_payload, real.Rel), _sys, new BundledConsent { AllHelpers = true });
        apps.EnsureAll();
        Assert.DoesNotContain(_sys.Ran, r => r.StartsWith("PawnIO", StringComparison.Ordinal));
        Assert.False(_sys.PawnIo);
        Assert.Contains(_log, l => l.StartsWith("WARN PawnIO") && l.Contains("SHA-256"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "installers")));
    }

    private sealed class Tampered(IBundledPayload inner, string rel) : IBundledPayload
    {
        public IReadOnlyList<BundledFile> Files(string kind) => inner.Files(kind);
        public Stream? Open(string r) => r == rel ? new MemoryStream(Encoding.UTF8.GetBytes("something else")) : inner.Open(r);
    }

    [Fact]
    public void Every_program_says_what_it_is_and_under_which_licence()
    {
        var items = Apps().Items();
        Assert.Equal(BundledIds.All, items.Select(i => i.Id));
        Assert.Equal(new[] { "OpenRGB", "PawnIO", "Equalizer APO" }, items.Select(i => i.Name));
        Assert.All(items, i =>
        {
            Assert.EndsWith(".", i.What);
            Assert.Contains("GPL-2.0", i.Licence);
            Assert.True(i.Carried);
        });
    }

    [Fact]
    public void The_yes_is_handed_to_the_app_to_save_once_and_installs_nothing_by_itself()
    {
        var apps = Apps();
        var saved = new List<BundledConsent>();
        apps.ConsentChanged = saved.Add;

        apps.GiveConsent();
        apps.GiveConsent();

        Assert.Same(apps.Consent, Assert.Single(saved));
        Assert.True(apps.Consent.AllHelpers);
        Assert.Empty(_sys.Ran);
        Assert.False(Directory.Exists(_root));
        Assert.True(apps.Summary().Pending); // EnsureAll does the installing
    }

    [Fact]
    public void A_folder_of_the_user_counts_as_changeable_and_a_Windows_folder_does_not()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "file.txt"), "x");
        Assert.True(FolderGuard.OthersCanChange(_root));
        Assert.True(FolderGuard.OthersCanChange(Path.Combine(_root, "file.txt")));
        Assert.True(FolderGuard.OthersCanChange(Path.Combine(_root, "missing")));
        Assert.True(FolderGuard.OthersCanChange(@"\\server\share\folder"));
        Assert.False(FolderGuard.OthersCanChange(Environment.SystemDirectory));
        Assert.False(FolderGuard.OthersCanChange(Path.Combine(Environment.SystemDirectory, "kernel32.dll")));
        Assert.False(FolderGuard.OthersCanChange(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
    }

    [Fact]
    public void The_folder_that_is_really_open_is_the_one_named_also_when_it_is_above_the_one_asked_about()
    {
        var inner = Path.Combine(_root, "installers");
        Directory.CreateDirectory(inner);

        // The folder nearest the drive that ordinary programs can change: the asked one, or one above it.
        var open = FolderGuard.FirstOpen(inner);
        Assert.NotNull(open);
        Assert.StartsWith(open!.TrimEnd('\\') + "\\", inner + "\\", StringComparison.OrdinalIgnoreCase);
        Assert.Null(FolderGuard.FirstOpen(Environment.SystemDirectory));
        Assert.Equal(Path.Combine(_root, "missing"), FolderGuard.FirstOpen(Path.Combine(_root, "missing")));

        // A RigControl folder that was moved into Program Files: the message names it, not the folder inside it
        // that RigControl has just made, and does not suggest deleting the program's own folder.
        var message = BundledApps.UnsafeFolderMessage(@"C:\Program Files\RigControl\installers", @"C:\Program Files\RigControl");
        Assert.Contains(@"can change the folder C:\Program Files\RigControl,", message);
        Assert.Contains(@"put nothing in C:\Program Files\RigControl\installers", message);
        Assert.Contains("owner", message);
        Assert.DoesNotContain("delete", message);

        // When the folder asked about is itself the open one, the advice stays as it was.
        var same = BundledApps.UnsafeFolderMessage(@"C:\Program Files\RigControl\OpenRGB", @"C:\Program Files\RigControl\OpenRGB\");
        Assert.Contains(@"can change the folder C:\Program Files\RigControl\OpenRGB,", same);
        Assert.Contains("(or delete it)", same);
        Assert.Equal(same, BundledApps.UnsafeFolderMessage(@"C:\Program Files\RigControl\OpenRGB", null));
    }
}
