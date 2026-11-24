using System.IO;
using System.Security.Cryptography;
using System.Text;
using RigControl.App;
using RigControl.Bundled;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.App.Tests;

/// <summary>The first-time setup's answers. Nothing here touches the real PC: pretend installers, a temp settings file.</summary>
public sealed class FirstRunServiceTests : IDisposable
{
    private sealed class Payload : IBundledPayload
    {
        private readonly Dictionary<string, byte[]> _content = new()
        {
            ["pawnio/PawnIO_setup.exe"] = Encoding.UTF8.GetBytes("pawnio setup"),
            ["equalizerapo/EqualizerAPO-x64-1.4.2.exe"] = Encoding.UTF8.GetBytes("equalizer setup"),
        };

        public IReadOnlyList<BundledFile> Files(string kind) => _content.Where(c => c.Key.StartsWith(kind + "/", StringComparison.Ordinal))
            .Select(c => new BundledFile(c.Key, Convert.ToHexString(SHA256.HashData(c.Value)))).ToList();

        public Stream? Open(string rel) => _content.TryGetValue(rel, out var bytes) ? new MemoryStream(bytes) : null;
    }

    private sealed class Pc : BundledSystem
    {
        private readonly string _root;
        public Pc(string root) => _root = root;
        public bool IsElevated { get; set; } = true;
        public bool PawnIo { get; set; }
        public bool EqualizerApo { get; set; }
        public bool Fail { get; set; }
        public List<string> Ran { get; } = new();

        public override string Root => _root;
        public override bool Elevated => IsElevated;
        public override bool PawnIoInstalled() => PawnIo;
        public override bool EqualizerApoInstalled() => EqualizerApo;
        public override bool OthersCanChange(string path) => false;
        public override string? OpenRgbServiceImagePath() => null;
        public override void OpenEqualizerApoDeviceSelector() { }

        public override (int Code, string Output) Run(string exe, string args, TimeSpan timeout)
        {
            var name = Path.GetFileName(exe);
            Ran.Add(name);
            if (Fail) return (5, "it did not work");
            if (name.StartsWith("PawnIO", StringComparison.Ordinal)) PawnIo = true;
            if (name.StartsWith("EqualizerAPO", StringComparison.Ordinal)) EqualizerApo = true;
            return (0, "");
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-setup-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _settings;
    private readonly Pc _pc;
    private readonly BundledApps _bundled;
    private sealed class ThisPc : FirstRunPc
    {
        public ThisPc() : base("") { }
        public int Others { get; set; }
        public bool Administrator { get; set; } = true;
        public bool AnotherAccount { get; set; }
        public bool FolderOpen { get; set; }
        public override int OtherUsersSignedIn() => Others;
        public override bool AccountIsAdministrator() => Administrator;
        public override bool RunsAsAnotherAccount() => AnotherAccount;
        public override bool ProgramFolderOpen() => FolderOpen;
        public override IReadOnlyList<DisplayAdapter> DisplayAdapters() => Array.Empty<DisplayAdapter>();
    }

    private readonly ThisPc _thisPc = new();
    private readonly List<bool> _autostartCalls = new();
    private bool _autostart;
    private RpcException? _autostartRefusal;

    public FirstRunServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance);
        FirstRunState.Initialise(_settings, settingsExistedAtStart: false, NullLog.Instance);
        _pc = new Pc(Path.Combine(_dir, "program files"));
        _bundled = new BundledApps(_ => { }, _ => { }, new Payload(), _pc, null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    private FirstRunService Service(bool elevated = true)
    {
        _pc.IsElevated = elevated;
        return new FirstRunService(_settings, new ModuleHost(NullLog.Instance), elevated, NullLog.Instance, () => _bundled,
            () => ("running", null), () => _autostart, on =>
            {
                _autostartCalls.Add(on);
                if (_autostartRefusal is not null) throw _autostartRefusal;
                _autostart = on;
            }, () => "Windows 11 (build 26100)", _thisPc);
    }

    [Fact]
    public async Task A_new_PC_is_shown_the_programs_RigControl_needs_and_nothing_is_installed_meanwhile()
    {
        var info = await Service().GetAsync(CancellationToken.None);

        Assert.False(info.Done);
        Assert.True(info.Elevated);
        Assert.False(info.RestartSuggested);
        Assert.True(info.Helpers.NeedsYes);
        Assert.False(info.Helpers.Consented);
        Assert.Equal(BundledIds.All, info.Helpers.Items.Select(h => h.Id));
        Assert.Equal(new[] { BundledIds.PawnIo, BundledIds.EqualizerApo }, info.Helpers.Items.Where(h => h.Carried).Select(h => h.Id));
        Assert.All(info.Helpers.Items, h => Assert.False(h.Installed));
        Assert.Equal(new[] { "OpenRGB" }, info.Helpers.NotCarried); // this pretend build has no OpenRGB inside
        Assert.False(info.Helpers.ServiceWillMove);
        Assert.Empty(_pc.Ran);
        Assert.False(_bundled.Consent.AllHelpers);
    }

    [Fact]
    public async Task Install_and_continue_is_the_one_yes_and_installs_every_carried_helper()
    {
        var saved = 0;
        _bundled.ConsentChanged = _ => saved++;

        var info = await Service().InstallHelpersAsync(CancellationToken.None);

        Assert.Equal(new[] { "PawnIO_setup.exe", "EqualizerAPO-x64-1.4.2.exe" }, _pc.Ran);
        Assert.Equal(1, saved);
        Assert.True(info.Helpers.Consented);
        Assert.False(info.Helpers.NeedsYes);
        Assert.False(info.Helpers.Pending);
        Assert.Empty(info.Helpers.Problems);
        Assert.All(info.Helpers.Items.Where(h => h.Carried), h => Assert.True(h.Installed));

        // Pressing it again installs nothing again.
        await Service().InstallHelpersAsync(CancellationToken.None);
        Assert.Equal(2, _pc.Ran.Count);
        Assert.Equal(1, saved);
    }

    [Fact]
    public async Task Without_administrator_rights_the_button_is_refused_and_no_yes_is_recorded()
    {
        var saved = 0;
        _bundled.ConsentChanged = _ => saved++;

        var refused = await Assert.ThrowsAsync<RpcException>(() => Service(elevated: false).InstallHelpersAsync(CancellationToken.None));

        Assert.Equal(RpcErrorCodes.NeedsAdmin, refused.Code);
        Assert.Contains("Restart RigControl as administrator", refused.Message);
        Assert.Empty(_pc.Ran);
        Assert.Equal(0, saved);
        Assert.False(_bundled.Consent.AllHelpers);
        Assert.True((await Service(elevated: false).GetAsync(CancellationToken.None)).Helpers.NeedsYes);
    }

    [Fact]
    public async Task A_PC_that_already_has_every_helper_is_not_asked_and_the_button_changes_nothing()
    {
        _pc.PawnIo = true;
        _pc.EqualizerApo = true;
        var saved = 0;
        _bundled.ConsentChanged = _ => saved++;

        var info = await Service().GetAsync(CancellationToken.None);
        Assert.False(info.Helpers.NeedsYes);
        Assert.True(info.Helpers.Consented);

        await Service().InstallHelpersAsync(CancellationToken.None);
        Assert.Empty(_pc.Ran);
        Assert.Equal(0, saved);
    }

    [Fact]
    public async Task A_helper_that_fails_to_install_is_told_and_can_be_tried_again()
    {
        _pc.Fail = true;
        var failed = await Service().InstallHelpersAsync(CancellationToken.None);
        Assert.True(failed.Helpers.Consented);   // the yes stands
        Assert.False(failed.Helpers.NeedsYes);   // so the setup can go on
        Assert.True(failed.Helpers.Pending);
        Assert.Equal(2, failed.Helpers.Problems.Count);
        Assert.False(failed.RestartForEqualiser);

        _pc.Fail = false;
        var again = await Service().InstallHelpersAsync(CancellationToken.None);
        Assert.False(again.Helpers.Pending);
        Assert.Empty(again.Helpers.Problems);
    }

    [Fact]
    public async Task Leaving_the_setup_without_the_yes_installs_nothing_and_the_step_is_still_due()
    {
        var service = Service();
        await service.CompleteAsync(autostart: false, CancellationToken.None);

        Assert.Empty(_pc.Ran);
        Assert.False(_bundled.Consent.AllHelpers);
        Assert.True((await service.GetAsync(CancellationToken.None)).Helpers.NeedsYes); // shown again, also on a PC whose setup is done
    }

    [Fact]
    public async Task Skipping_changes_nothing_and_installs_nothing()
    {
        var info = await Service().CompleteAsync(autostart: false, CancellationToken.None);

        Assert.True(info.Done);
        Assert.False(info.RestartSuggested);
        Assert.Empty(_autostartCalls);
        Assert.Empty(_pc.Ran);
        Assert.True(FirstRunState.IsDone(_settings));
    }

    [Fact]
    public async Task Finishing_with_start_with_Windows_turns_it_on_once_and_offers_a_restart()
    {
        var info = await Service().CompleteAsync(autostart: true, CancellationToken.None);

        Assert.Equal(new[] { true }, _autostartCalls);
        Assert.True(info.Done);
        Assert.True(info.Autostart);
        Assert.True(info.RestartSuggested);

        // Going through the setup again with the same choice changes nothing and does not nag about a restart.
        var again = await Service().CompleteAsync(autostart: true, CancellationToken.None);
        Assert.Equal(new[] { true }, _autostartCalls);
        Assert.False(again.RestartSuggested);
    }

    [Fact]
    public async Task When_Windows_refuses_start_with_Windows_the_setup_is_not_marked_as_finished()
    {
        _autostartRefusal = new RpcException(RpcErrorCodes.Cancelled, "Administrator permission was not granted, so nothing changed.");

        var refused = await Assert.ThrowsAsync<RpcException>(() => Service(elevated: false).CompleteAsync(autostart: true, CancellationToken.None));

        Assert.Equal(RpcErrorCodes.Cancelled, refused.Code);
        Assert.False(FirstRunState.IsDone(_settings));
    }

    [Fact]
    public async Task Equalizer_APO_installed_during_the_setup_makes_a_restart_worth_offering()
    {
        var service = Service();
        var installed = await service.InstallHelpersAsync(CancellationToken.None);
        // Told straight away too: on a PC whose setup is done this step is shown by itself.
        Assert.True(installed.RestartForEqualiser);
        Assert.True(installed.RestartSuggested);
        Assert.False(installed.RestartForAutostart);

        var info = await service.CompleteAsync(autostart: false, CancellationToken.None);

        Assert.True(info.RestartSuggested);
        Assert.Empty(_autostartCalls);
        // The reason is told, so the question does not promise "starts with Windows" to someone who switched that off.
        Assert.True(info.RestartForEqualiser);
        Assert.False(info.RestartForAutostart);
    }

    [Fact]
    public async Task The_restart_question_knows_both_reasons_when_both_apply()
    {
        var service = Service();
        await service.InstallHelpersAsync(CancellationToken.None);

        var info = await service.CompleteAsync(autostart: true, CancellationToken.None);

        Assert.True(info.RestartSuggested);
        Assert.True(info.RestartForAutostart);
        Assert.True(info.RestartForEqualiser);
    }

    [Fact]
    public async Task A_standard_Windows_account_is_not_promised_a_start_as_administrator()
    {
        _thisPc.Administrator = false;

        var before = await Service(elevated: false).GetAsync(CancellationToken.None);
        Assert.False(before.AccountIsAdministrator);

        var info = await Service(elevated: false).CompleteAsync(autostart: true, CancellationToken.None);

        Assert.Equal(new[] { true }, _autostartCalls); // it still starts with Windows, as the user asked
        Assert.False(info.RestartForAutostart);       // but a restart would not make it administrator
        Assert.False(info.RestartSuggested);
        Assert.False(info.AccountIsAdministrator);
    }

    [Fact]
    public async Task Running_as_another_account_other_signed_in_users_and_an_open_folder_are_told()
    {
        var plain = await Service().GetAsync(CancellationToken.None);
        Assert.False(plain.OtherAccount);
        Assert.Equal(0, plain.OtherUsersSignedIn);
        Assert.False(plain.ProgramFolderOpen);
        Assert.True(plain.AccountIsAdministrator);

        _thisPc.AnotherAccount = true;
        _thisPc.Others = 2;
        _thisPc.FolderOpen = true;
        var info = await Service().GetAsync(CancellationToken.None);
        Assert.True(info.OtherAccount);
        Assert.Equal(2, info.OtherUsersSignedIn);
        Assert.True(info.ProgramFolderOpen);

        // Not running as administrator, RigControl is the signed-in account itself.
        Assert.False((await Service(elevated: false).GetAsync(CancellationToken.None)).OtherAccount);
    }
}
