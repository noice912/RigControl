using System.IO;
using System.Text.Json.Nodes;
using RigControl.App;
using RigControl.Bundled;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Settings;

namespace RigControl.App.Tests;

public class FirstRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-firstrun-" + Guid.NewGuid().ToString("N"));

    public FirstRunTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private SettingsStore Store() => new(SettingsPath, NullLog.Instance);

    [Fact]
    public void A_new_data_folder_shows_the_setup_and_keeps_showing_it_until_it_is_finished()
    {
        Assert.False(FirstRunState.SettingsExist(SettingsPath));
        var settings = Store();
        Assert.False(FirstRunState.Initialise(settings, settingsExistedAtStart: false, NullLog.Instance));

        // Something else saved settings and the app was closed before the setup was finished: still not done.
        settings.Update<AppSettings>(AppSettings.Section, s => s.StartMinimized = true);
        Assert.True(FirstRunState.SettingsExist(SettingsPath));
        var again = Store();
        Assert.False(FirstRunState.Initialise(again, settingsExistedAtStart: true, NullLog.Instance));

        FirstRunState.MarkDone(again);
        Assert.True(FirstRunState.Initialise(Store(), settingsExistedAtStart: true, NullLog.Instance));
    }

    [Fact]
    public void A_PC_that_already_used_RigControl_never_sees_the_setup()
    {
        // The settings file of an older version: sections, but nothing about a first run.
        File.WriteAllText(SettingsPath, """{ "app": { "autostartRegistered": true }, "lighting": { } }""");
        Assert.True(FirstRunState.SettingsExist(SettingsPath));

        var settings = Store();
        Assert.True(FirstRunState.Initialise(settings, settingsExistedAtStart: true, NullLog.Instance));
        Assert.True(FirstRunState.IsDone(settings));
        Assert.True(FirstRunState.IsDone(Store())); // and it was written down
        Assert.True(Store().Get<AppSettings>(AppSettings.Section).AutostartRegistered); // the other sections are untouched
    }

    [Fact]
    public void An_empty_settings_file_counts_as_new()
    {
        File.WriteAllText(SettingsPath, "{}");
        Assert.False(FirstRunState.SettingsExist(SettingsPath));
        Assert.False(FirstRunState.Decide(null, settingsExistedAtStart: false));
        Assert.True(FirstRunState.Decide(true, settingsExistedAtStart: false));
        Assert.False(FirstRunState.Decide(false, settingsExistedAtStart: true));
    }

    private static ModuleInfo Mod(string name, ModuleState state, string? message = null) => new(name, name, state, message);

    private static FirstRunScan.Answers FullPc() => new(
        new[]
        {
            Mod("sensors", ModuleState.Ready), Mod("nvidia", ModuleState.Ready), Mod("cooling", ModuleState.Ready),
            Mod("lighting", ModuleState.Ready), Mod("capture", ModuleState.Ready), Mod("drivers", ModuleState.Ready),
            Mod("bios", ModuleState.Ready), Mod("presence", ModuleState.Ready), Mod("cpu", ModuleState.Ready),
        },
        Sensors: JsonNode.Parse("""{ "cpu": { "name": "Intel Core i7-13700K" }, "gpu": { "name": "NVIDIA GeForce RTX 4070 Ti" }, "memory": { "totalGB": 31.8 }, "board": { "name": "Some board" } }"""),
        Nvidia: JsonNode.Parse("""{ "available": true, "gpus": [ { "index": 0, "name": "NVIDIA GeForce RTX 4070 Ti" } ] }"""),
        Cooling: JsonNode.Parse("""{ "devices": [ { "name": "NZXT Kraken Elite" }, { "name": "NZXT RGB & Fan Controller" } ] }"""),
        Lighting: JsonNode.Parse("""{ "openRgb": { "installed": true }, "devices": [ { "name": "Keyboard" }, { "name": "Memory" } ] }"""),
        Gcc: JsonNode.Parse("""{ "installed": true }"""),
        Bios: JsonNode.Parse("""{ "available": true, "reason": null, "board": "ROG STRIX Z790-E", "biosVersion": "2301" }"""),
        Presence: JsonNode.Parse("""{ "discordState": "attached" }"""),
        AudioState: "running", RichPresenceFound: true, Windows: "Windows 11 24H2 (build 26100)", MemoryGb: 16);

    [Fact]
    public void The_hardware_list_is_built_from_what_the_modules_already_know()
    {
        var hardware = FirstRunScan.Hardware(FullPc()).ToDictionary(h => h.Label, h => h.Value);

        Assert.Equal("Intel Core i7-13700K", hardware["Processor"]);
        Assert.Equal("ROG STRIX Z790-E (BIOS 2301)", hardware["Motherboard"]);
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti", hardware["Graphics card"]);
        Assert.Equal("32 GB", hardware["Memory"]); // the figure from the sensors wins over the fallback
        Assert.Equal("Windows 11 24H2 (build 26100)", hardware["Windows"]);
        Assert.Equal("NZXT Kraken Elite, NZXT RGB & Fan Controller", hardware["NZXT devices"]);
        Assert.Equal("2 devices: Keyboard, Memory", hardware["Lights"]);
        Assert.Equal("Running", hardware["Discord"]);
    }

    [Fact]
    public void The_built_in_hardware_list_adds_the_generation_and_the_card_family()
    {
        var pc = FullPc() with
        {
            Cpu = JsonNode.Parse("""{ "name": "Intel(R) Core(TM) i7-13700K", "known": true, "summary": "13th gen Core (Raptor Lake), desktop" }"""),
            Nvidia = JsonNode.Parse("""{ "available": true, "gpus": [ { "index": 0, "name": "NVIDIA GeForce RTX 4070 Ti", "family": "GeForce RTX 40 series" } ] }"""),
        };
        var hardware = FirstRunScan.Hardware(pc).ToDictionary(h => h.Label, h => h.Value);
        Assert.Equal("Intel(R) Core(TM) i7-13700K (13th gen Core (Raptor Lake), desktop)", hardware["Processor"]);
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti (GeForce RTX 40 series)", hardware["Graphics card"]);

        // A card only the sensors found: its family comes from the hardware id Windows keeps for a card of that name.
        var other = FullPc() with
        {
            Nvidia = null,
            Sensors = JsonNode.Parse("""{ "gpu": { "name": "NVIDIA GeForce RTX 4070 Ti" } }"""),
            Adapters = new[]
            {
                new DisplayAdapter(@"PCI\VEN_8086&DEV_A780", "Some other card"),
                new DisplayAdapter(@"PCI\VEN_10DE&DEV_2782&SUBSYS_00000000", "nvidia geforce rtx 4070  ti"),
            },
        };
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti (GeForce RTX 40 series)", FirstRunScan.Hardware(other).Single(h => h.Label == "Graphics card").Value);

        // A processor or card the list does not know is shown under its own name, with nothing added.
        var unknown = FullPc() with
        {
            Cpu = JsonNode.Parse("""{ "name": "Some new processor", "known": false, "summary": "Some new processor: newer than this version knows" }"""),
            Adapters = new[] { new DisplayAdapter(@"PCI\VEN_10DE&DEV_FFFE", "NVIDIA GeForce RTX 4070 Ti") },
        };
        hardware = FirstRunScan.Hardware(unknown).ToDictionary(h => h.Label, h => h.Value);
        Assert.Equal("Some new processor", hardware["Processor"]);
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti", hardware["Graphics card"]);
    }

    [Fact]
    public void Every_feature_fits_on_a_PC_that_has_everything()
    {
        var features = FirstRunScan.Features(FullPc());
        Assert.All(features, f => Assert.True(f.Available, f.Id));
        Assert.All(features, f => Assert.Null(f.Reason));
        Assert.Equal(features.Count, features.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void Features_that_do_not_fit_another_PC_say_why_in_plain_words()
    {
        var other = new FirstRunScan.Answers(
            new[]
            {
                Mod("sensors", ModuleState.Degraded, "Running without administrator rights."),
                Mod("nvidia", ModuleState.Ready), Mod("cooling", ModuleState.Ready), Mod("lighting", ModuleState.Ready),
                Mod("capture", ModuleState.Failed, "The recorder could not start."),
                Mod("drivers", ModuleState.Starting),
                Mod("bios", ModuleState.Degraded, RigControl.Bios.BiosModule.NotAvailablePrefix + "this is not the board it was made for."),
            },
            Nvidia: JsonNode.Parse("""{ "available": false, "gpus": [] }"""),
            Cooling: JsonNode.Parse("""{ "devices": [] }"""),
            Lighting: JsonNode.Parse("""{ "openRgb": { "installed": false }, "devices": [] }"""),
            Gcc: JsonNode.Parse("""{ "installed": false }"""),
            Bios: JsonNode.Parse("""{ "available": false, "reason": "This is not the board it was made for.", "board": "B650", "biosVersion": "1.0" }"""),
            AudioState: "failed", AudioMessage: "No sound devices.");
        var features = FirstRunScan.Features(other).ToDictionary(f => f.Id);

        Assert.True(features["sensors"].Available); // works with less, so it still applies
        Assert.False(features["nvidia"].Available);
        Assert.Contains("NVIDIA", features["nvidia"].Reason);
        Assert.False(features["cooling"].Available);
        Assert.Contains("NZXT", features["cooling"].Reason);
        Assert.False(features["lighting"].Available);
        Assert.Contains("OpenRGB", features["lighting"].Reason);
        Assert.Equal("The recorder could not start.", features["capture"].Reason);
        Assert.Equal("RigControl is still checking this.", features["drivers"].Reason);
        Assert.False(features["bios"].Available);
        Assert.Equal("This is not the board it was made for.", features["bios"].Reason);
        Assert.Equal("Not part of this build.", features["presence"].Reason);
        Assert.Equal("No sound devices.", features["audio"].Reason);
        Assert.False(features["gcc"].Available);
        Assert.False(features["richpresence"].Available);
        Assert.True(features["overlay"].Available);

        var hardware = FirstRunScan.Hardware(other).ToDictionary(h => h.Label, h => h.Value);
        Assert.Equal("B650 (BIOS 1.0)", hardware["Motherboard"]);
        Assert.DoesNotContain("Graphics card", hardware.Keys);
        Assert.DoesNotContain("NZXT device", hardware.Keys);
    }

    [Fact]
    public void Recording_is_not_listed_as_working_without_an_NVIDIA_card_or_without_its_video_converter()
    {
        var amd = FullPc() with { Nvidia = JsonNode.Parse("""{ "available": false, "gpus": [] }""") };
        var capture = FirstRunScan.Features(amd).Single(f => f.Id == "capture");
        Assert.False(capture.Available);
        Assert.Contains("NVIDIA", capture.Reason);

        const string Missing = "The video converter inside RigControl could not be unpacked, so recording is unavailable.";
        var noConverter = FullPc();
        noConverter = noConverter with
        {
            Modules = noConverter.Modules.Select(m => m.Name == "capture" ? Mod("capture", ModuleState.Degraded, Missing) : m).ToArray(),
        };
        capture = FirstRunScan.Features(noConverter).Single(f => f.Id == "capture");
        Assert.False(capture.Available);
        Assert.Equal(Missing, capture.Reason);

        // When the NVIDIA part could not be asked, recording is not ruled out.
        Assert.True(FirstRunScan.Features(FullPc() with { Nvidia = null }).Single(f => f.Id == "capture").Available);
    }

    [Fact]
    public void Discord_status_is_not_listed_as_working_in_a_build_without_Discord_ids()
    {
        var pc = FullPc() with
        {
            Presence = JsonNode.Parse("""{ "discordState": "found", "setUp": false, "setupNote": "Discord status isn't set up in this build. Add your own ids." }"""),
        };
        var presence = FirstRunScan.Features(pc).Single(f => f.Id == "presence");
        Assert.False(presence.Available);
        Assert.Equal("Discord status isn't set up in this build. Add your own ids.", presence.Reason);

        pc = FullPc() with { Presence = JsonNode.Parse("""{ "discordState": "found", "setUp": true }""") };
        Assert.True(FirstRunScan.Features(pc).Single(f => f.Id == "presence").Available);
    }

    [Fact]
    public void Restarting_the_PC_asks_Windows_for_a_30_second_countdown_with_a_reason()
    {
        var calls = new List<string[]>();
        var restart = new PcRestart(args =>
        {
            calls.Add(args.ToArray());
            return (0, "");
        });

        Assert.Equal(30, restart.Schedule());
        var sent = Assert.Single(calls);
        Assert.Equal(new[] { "/r", "/t", "30", "/c" }, sent.Take(4));
        Assert.Contains("RigControl", sent[4]);
        Assert.DoesNotContain("/s", sent); // a restart, never a plain shut down
        Assert.Equal(5, sent.Length);
    }

    [Fact]
    public void A_restart_is_refused_while_someone_else_is_signed_in_unless_the_user_was_told_and_said_yes()
    {
        var calls = new List<string[]>();
        var restart = new PcRestart(args =>
        {
            calls.Add(args.ToArray());
            return (0, "");
        });

        var refused = Assert.Throws<OtherUsersSignedInException>(() => restart.Schedule(otherUsersSignedIn: 1));
        Assert.Contains("Someone else is signed in", refused.Message);
        Assert.Empty(calls); // Windows was never asked

        Assert.Equal(30, restart.Schedule(otherUsersSignedIn: 1, evenWithOtherUsers: true));
        Assert.Single(calls);
        Assert.Equal(30, restart.Schedule(otherUsersSignedIn: 0));
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void Cancelling_a_restart_runs_the_abort_and_copes_with_nothing_to_cancel()
    {
        var code = 0;
        var calls = new List<string[]>();
        var restart = new PcRestart(args =>
        {
            calls.Add(args.ToArray());
            return (code, code == 5 ? "Access is denied." : "");
        });

        Assert.True(restart.Cancel());
        Assert.Equal(new[] { "/a" }, Assert.Single(calls));

        code = 1116; // Windows: no restart was counting down
        Assert.False(restart.Cancel());

        code = 5;
        Assert.Equal("Access is denied.", Assert.Throws<InvalidOperationException>(() => restart.Cancel()).Message);
        Assert.Equal("Access is denied.", Assert.Throws<InvalidOperationException>(() => restart.Schedule()).Message);
    }
}
