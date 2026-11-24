using RigControl.Core.Conflicts;

namespace RigControl.Core.Tests.Conflicts;

public class ConflictScannerTests
{
    private const string LightingKey = @"Software\Microsoft\Lighting";

    private static IReadOnlyList<ConflictFinding> Scan(FakeProbe probe, IReadOnlyList<ConflictEntry>? catalog = null)
        => new ConflictScanner(catalog).Scan(probe).Findings;

    private static ConflictEntry Entry(string id, ConflictDetect detect, ConflictSeverity severity = ConflictSeverity.Warn,
        bool verified = false, ConflictFix? fix = null, bool explainOnly = false, string module = ConflictModules.Lighting)
        => new(id, module, id, ConflictKind.Process, detect, severity, "breaks", "do", verified, fix, explainOnly);

    [Fact]
    public void A_clean_PC_has_no_findings()
    {
        Assert.Empty(Scan(new FakeProbe()));
    }

    [Fact]
    public void One_scan_takes_exactly_one_snapshot()
    {
        var probe = new FakeProbe();
        probe.Processes.Add("SignalRgb");
        Scan(probe);
        Assert.Equal(1, probe.Taken);
    }

    [Fact]
    public void A_running_program_is_found_whatever_the_letter_case()
    {
        var probe = new FakeProbe();
        probe.Processes.Add("signalrgb");
        var found = Scan(probe);
        var lighting = Assert.Single(found, f => f.Id == "lighting.signalrgb");
        Assert.Equal(ConflictSeverity.Warn, lighting.Severity);
        Assert.Equal("SignalRgb.exe is running.", lighting.Evidence, ignoreCase: true);
        Assert.Contains(found, f => f.Id == "cooling.signalrgb");
    }

    [Fact]
    public void A_running_service_alone_is_enough()
    {
        var probe = new FakeProbe();
        probe.Services.Add("CAMService");
        var cam = Assert.Single(Scan(probe), f => f.Id == "cooling.nzxt-cam");
        Assert.Equal(ConflictSeverity.Blocks, cam.Severity);
        Assert.Equal(ConflictKind.Process, cam.Kind);
        Assert.Contains("CAMService service is running", cam.Evidence);
    }

    [Fact]
    public void A_loaded_driver_is_found()
    {
        var probe = new FakeProbe();
        probe.Drivers.Add("WinRing0x64");
        var f = Assert.Single(Scan(probe));
        Assert.Equal("sensors.winring0", f.Id);
        Assert.Equal(ConflictKind.Driver, f.Kind);
    }

    [Fact]
    public void A_start_up_task_that_is_switched_on_is_found_without_the_program_running()
    {
        var probe = new FakeProbe();
        probe.Tasks.Add(@"\GCC");
        var f = Assert.Single(Scan(probe));
        Assert.Equal("lighting.gcc", f.Id);
        Assert.Contains(@"\GCC", f.Evidence);
    }

    [Fact]
    public void The_scanner_asks_the_probe_for_the_tasks_registry_places_and_paths_the_catalog_needs()
    {
        var probe = new FakeProbe();
        Scan(probe);
        var q = probe.LastQuery!;
        Assert.Contains(@"\GCC", q.Tasks);
        Assert.Contains(@"\ASUS\", q.Tasks);
        Assert.Contains("OpenRGB", q.ProcessPathsFor);
        Assert.Contains(q.Registry, t => t.Value == "HiberbootEnabled");
        Assert.Equal(q.Registry.Count, q.Registry.Select(t => t.Place).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_registry_setting_is_found_only_when_it_has_the_expected_number()
    {
        var probe = new FakeProbe();
        probe.SetNumber(RegistryRoot.CurrentUser, LightingKey, "AmbientLightingEnabled", 0);
        Assert.DoesNotContain(Scan(probe), f => f.Id == "lighting.dynamic-lighting");

        probe.SetNumber(RegistryRoot.CurrentUser, LightingKey, "AmbientLightingEnabled", 1);
        var found = Assert.Single(Scan(probe), f => f.Id == "lighting.dynamic-lighting");
        Assert.Equal(ConflictKind.Setting, found.Kind);
        Assert.Null(found.Fix);
    }

    [Fact]
    public void An_is_not_test_matches_a_missing_value_but_never_one_that_could_not_be_read()
    {
        var test = new RegistryTest(RegistryRoot.CurrentUser, "K", "V", RegistryCheck.IsNot, 0);
        Assert.True(test.Matches(new RegistryReading(false, false, null)));
        Assert.True(test.Matches(new RegistryReading(true, true, 1)));
        Assert.False(test.Matches(new RegistryReading(true, true, 0)));
        Assert.False(test.Matches(null));
    }

    [Fact]
    public void All_registry_tests_of_an_entry_must_match()
    {
        const string dvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
        var probe = new FakeProbe();
        probe.SetNumber(RegistryRoot.CurrentUser, dvr, "HistoricalCaptureEnabled", 1);
        probe.SetNumber(RegistryRoot.CurrentUser, dvr, "AppCaptureEnabled", 1);
        // The policy value has not been read yet, so nothing may be claimed.
        Assert.DoesNotContain(Scan(probe), f => f.Id == "capture.game-bar-background");

        probe.SetMissing(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
        Assert.Contains(Scan(probe), f => f.Id == "capture.game-bar-background" && f.Severity == ConflictSeverity.Warn);

        probe.SetNumber(RegistryRoot.CurrentUser, dvr, "AppCaptureEnabled", 0);
        Assert.DoesNotContain(Scan(probe), f => f.Id == "capture.game-bar-background");
    }

    [Fact]
    public void A_fact_is_found_only_when_the_probe_says_it_is_true()
    {
        var probe = new FakeProbe();
        Assert.DoesNotContain(Scan(probe), f => f.Id == "system.not-elevated");
        probe.Facts[ConflictFacts.NotElevated] = false;
        Assert.DoesNotContain(Scan(probe), f => f.Id == "system.not-elevated");
        probe.Facts[ConflictFacts.NotElevated] = true;
        Assert.Contains(Scan(probe), f => f.Id == "system.not-elevated" && f.Fix is null);
    }

    [Fact]
    public void RigControls_own_OpenRGB_is_not_reported_but_someone_elses_is()
    {
        var probe = new FakeProbe();
        probe.Processes.Add("OpenRGB");
        probe.Paths["OpenRGB"] = new[] { @"C:\Program Files\RigControl\OpenRGB\OpenRGB.exe" };
        Assert.DoesNotContain(Scan(probe), f => f.Id == "lighting.openrgb-other");

        probe.Paths["OpenRGB"] = new[] { @"C:\Program Files\RigControl\OpenRGB\OpenRGB.exe", @"D:\Tools\OpenRGB\OpenRGB.exe" };
        var found = Assert.Single(Scan(probe), f => f.Id == "lighting.openrgb-other");
        Assert.Contains(@"D:\Tools\OpenRGB\OpenRGB.exe", found.Evidence);
    }

    [Fact]
    public void An_OpenRGB_whose_path_cannot_be_read_is_not_reported()
    {
        var probe = new FakeProbe();
        probe.Processes.Add("OpenRGB");
        Assert.DoesNotContain(Scan(probe), f => f.Id == "lighting.openrgb-other");
    }

    [Fact]
    public void A_name_ending_in_a_star_matches_by_its_start()
    {
        var catalog = new[] { Entry("x.star", new ConflictDetect { Services = new[] { "BcastDVRUserService_*" } }) };
        var probe = new FakeProbe();
        probe.Services.Add("BcastDVRUserService_1a2b3");
        var found = Assert.Single(Scan(probe, catalog));
        Assert.Contains("BcastDVRUserService_1a2b3", found.Evidence);
    }

    [Fact]
    public void Findings_come_most_serious_first()
    {
        var probe = new FakeProbe();
        probe.Processes.Add("HWiNFO64");      // info
        probe.Processes.Add("NZXT CAM");      // blocks (cooling) + warn (lighting)
        probe.Processes.Add("FanControl");    // warn
        var severities = Scan(probe).Select(f => f.Severity).ToList();
        Assert.Equal(ConflictSeverity.Blocks, severities[0]);
        Assert.Equal(severities.OrderByDescending(s => s).ToList(), severities);
    }

    [Fact]
    public void A_probe_that_throws_becomes_one_info_finding_instead_of_a_crash()
    {
        var probe = new FakeProbe { Throw = new InvalidOperationException("no access") };
        var found = Assert.Single(Scan(probe));
        Assert.Equal(ConflictSeverity.Info, found.Severity);
        Assert.Equal(ConflictModules.System, found.Module);
        Assert.StartsWith("Couldn't check", found.Name);
        Assert.Equal("no access", found.Evidence);
        Assert.Null(found.Fix);
    }

    [Fact]
    public void A_part_that_could_not_be_checked_becomes_an_info_finding_next_to_the_real_ones()
    {
        var probe = new FakeProbe();
        probe.Processes.Add("iCUE");
        probe.Failed.Add(new ConflictProbeFailure("Windows services", "The service list is not available."));
        var found = Scan(probe);
        Assert.Contains(found, f => f.Id == "lighting.icue");
        var failure = Assert.Single(found, f => f.Id == "health.check.windows-services");
        Assert.Equal("Couldn't check Windows services", failure.Name);
        Assert.Equal(ConflictSeverity.Info, failure.Severity);
    }

    [Fact]
    public void A_fix_is_dropped_when_the_entry_is_not_verified_or_is_explain_only()
    {
        var fix = new ConflictFix("Do it", "lighting.takeOverGcc");
        var detect = new ConflictDetect { Processes = new[] { "rival" } };
        var catalog = new[]
        {
            Entry("a.unverified", detect, fix: fix),
            Entry("b.explain", detect, verified: true, fix: fix, explainOnly: true),
            Entry("c.ok", detect, verified: true, fix: fix),
        };
        var probe = new FakeProbe();
        probe.Processes.Add("rival");
        var found = Scan(probe, catalog).ToDictionary(f => f.Id);
        Assert.Null(found["a.unverified"].Fix);
        Assert.Null(found["b.explain"].Fix);
        Assert.Equal("lighting.takeOverGcc", found["c.ok"].Fix!.Method);
    }

    [Fact]
    public void GIGABYTE_Control_Center_carries_the_existing_take_over_button()
    {
        var probe = new FakeProbe();
        probe.Processes.Add("GCC");
        var found = Assert.Single(Scan(probe), f => f.Id == "lighting.gcc");
        Assert.True(found.Verified);
        Assert.Equal("lighting.takeOverGcc", found.Fix!.Method);
        Assert.False(string.IsNullOrWhiteSpace(found.Fix.Confirm));
    }
}
