using RigControl.Core.Conflicts;

namespace RigControl.Core.Tests.Conflicts;

/// <summary>Several Windows users signed in at once: whose programs count, and what a start-up task alone means.</summary>
public class ConflictSessionTests
{
    private static IReadOnlyList<ConflictFinding> Scan(FakeProbe probe) => new ConflictScanner().Scan(probe).Findings;

    [Fact]
    public void Another_Windows_users_RichPresence_or_Discord_is_not_reported_as_in_this_users_way()
    {
        var probe = new FakeProbe { SessionProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer" } };
        foreach (var other in new[] { "RichPresence", "PreMiD", "CustomRP", "Discord", "steam", "DS4Windows" }) probe.Processes.Add(other);

        var found = Scan(probe);

        Assert.DoesNotContain(found, f => f.Module == ConflictModules.Presence);
        Assert.DoesNotContain(found, f => f.Module == ConflictModules.Overlay);
        // Steam's recording is not a per-user entry: it still shows.
        Assert.Contains(found, f => f.Id == "capture.steam-recording");
    }

    [Fact]
    public void This_users_own_RichPresence_still_blocks_the_Discord_status()
    {
        var probe = new FakeProbe { SessionProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "richpresence" } };
        probe.Processes.Add("RichPresence");

        var f = Assert.Single(Scan(probe), x => x.Id == "presence.richpresence");
        Assert.Equal(ConflictSeverity.Blocks, f.Severity);
    }

    [Fact]
    public void A_probe_that_cannot_tell_sessions_apart_counts_every_program()
    {
        var probe = new FakeProbe();
        probe.Processes.Add("RichPresence");
        Assert.Contains(Scan(probe), f => f.Id == "presence.richpresence");
    }

    [Fact]
    public void A_hardware_rival_counts_whichever_user_runs_it()
    {
        var probe = new FakeProbe { SessionProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };
        probe.Processes.Add("NZXT CAM");
        Assert.Contains(Scan(probe), f => f.Id == "cooling.nzxt-cam");
    }

    [Fact]
    public void An_ASUS_start_up_task_alone_is_a_note_and_never_the_Armoury_Crate_finding_with_its_button()
    {
        var probe = new FakeProbe();
        probe.Tasks.Add(ConflictCatalog.ArmouryTaskFolder);

        var found = Scan(probe);

        // The take-over button closes programs and services only, so it must clear the finding it sits on.
        Assert.DoesNotContain(found, f => f.Id == "sensors.armoury-crate");
        var note = Assert.Single(found, f => f.Id == "sensors.asus-startup-tasks");
        Assert.Equal(ConflictSeverity.Info, note.Severity);
        Assert.Null(note.Fix);
    }

    [Fact]
    public void Armoury_Crate_running_gives_the_finding_and_closing_it_clears_it_even_with_its_tasks_still_on()
    {
        var probe = new FakeProbe();
        probe.Tasks.Add(ConflictCatalog.ArmouryTaskFolder);
        probe.Services.Add("ArmouryCrateService");
        var before = Assert.Single(Scan(probe), f => f.Id == "sensors.armoury-crate");
        Assert.Equal("sensors.closeArmouryCrate", before.Fix!.Method);

        probe.Services.Clear(); // what the button does
        Assert.DoesNotContain(Scan(probe), f => f.Id == "sensors.armoury-crate");
    }
}
