using RigControl.Core.Conflicts;

namespace RigControl.Core.Tests.Conflicts;

public class ConflictCatalogTests
{
    private static ConflictEntry Get(string id) => ConflictCatalog.All.Single(e => e.Id == id);

    [Fact]
    public void Every_id_is_unique_and_starts_with_its_module()
    {
        var ids = ConflictCatalog.All.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ConflictCatalog.All, e =>
        {
            Assert.Contains(e.Module, ConflictModules.All);
            Assert.StartsWith(e.Module + ".", e.Id);
        });
    }

    [Fact]
    public void Every_entry_has_a_name_an_explanation_and_a_way_to_be_detected()
    {
        Assert.All(ConflictCatalog.All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.WhatItBreaks), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.WhatToDo), e.Id);
            Assert.EndsWith(".", e.WhatItBreaks);
            Assert.EndsWith(".", e.WhatToDo);
            var d = e.Detect;
            Assert.True(d.Processes.Count + d.Services.Count + d.Drivers.Count + d.Tasks.Count + d.Registry.Count + d.Facts.Count > 0, e.Id);
            Assert.All(d.Processes, p => Assert.False(p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), e.Id));
        });
    }

    [Fact]
    public void Every_feature_area_is_covered()
    {
        foreach (var module in ConflictModules.All)
            Assert.True(ConflictCatalog.For(module).Any(), module);
    }

    [Fact]
    public void A_fix_only_points_at_a_button_the_app_already_has_and_only_on_a_verified_entry()
    {
        var withFix = ConflictCatalog.All.Where(e => e.Fix is not null).ToList();
        Assert.NotEmpty(withFix);
        Assert.All(withFix, e =>
        {
            Assert.True(e.Verified, e.Id);
            Assert.False(e.ExplainOnly, e.Id);
            Assert.Contains(e.Fix!.Method, ConflictCatalog.AllowedFixMethods);
            Assert.False(string.IsNullOrWhiteSpace(e.Fix.Label), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.Fix.Confirm), e.Id);
        });
    }

    [Theory]
    [InlineData("system.memory-integrity")]
    [InlineData("system.driver-blocklist")]
    [InlineData("system.controlled-folder-access")]
    [InlineData("system.fast-startup")]
    [InlineData("system.game-mode")]
    [InlineData("system.not-elevated")]
    [InlineData("system.pending-restart")]
    [InlineData("system.other-session")]
    [InlineData("system.vpn")]
    [InlineData("cooling.nzxt-cam")]
    [InlineData("lighting.nzxt-cam")]
    [InlineData("lighting.public-network")]
    public void Security_settings_VPNs_and_NZXT_CAM_are_explained_and_never_get_a_fix(string id)
    {
        var e = Get(id);
        Assert.True(e.ExplainOnly);
        Assert.Null(e.Fix);
    }

    [Fact]
    public void Nothing_in_the_system_area_has_a_fix()
    {
        Assert.All(ConflictCatalog.For(ConflictModules.System), e => Assert.Null(e.Fix));
    }

    [Fact]
    public void The_lighting_rivals_use_the_same_program_names_as_the_lighting_page()
    {
        // LightingModule.Rivals
        var expected = new[] { "LightingService", "ArmouryCrate", "NZXT CAM", "GCC", "RazerAppEngine", "Razer Synapse 3", "iCUE", "SignalRgb" };
        Assert.Equal(expected, ConflictCatalog.LightingRivals.Select(r => r.Process));

        var verifiedLighting = ConflictCatalog.For(ConflictModules.Lighting).Where(e => e.Verified)
            .SelectMany(e => e.Detect.Processes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(expected, name => Assert.Contains(name, verifiedLighting));
    }

    [Fact]
    public void The_streaming_apps_are_the_same_as_the_ones_light_sync_shares_the_capture_card_with()
    {
        // LightingModule.Streamers
        var expected = new[] { "obs64", "obs32", "obs", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core" };
        var e = Get("lighting.streaming-app");
        Assert.Equal(expected, e.Detect.Processes);
        Assert.True(e.Verified);
        Assert.Equal(ConflictSeverity.Info, e.Severity);
    }

    [Fact]
    public void NZXT_CAM_uses_the_same_names_as_the_cooling_page_and_blocks_cooling()
    {
        var e = Get("cooling.nzxt-cam");
        Assert.Equal(new[] { "NZXT CAM", "cam_helper" }, e.Detect.Processes);
        Assert.Equal(new[] { "CAMService" }, e.Detect.Services);
        Assert.True(e.Verified);
        Assert.Equal(ConflictSeverity.Blocks, e.Severity);
    }

    [Fact]
    public void GIGABYTE_Control_Center_uses_the_same_program_and_task_as_the_take_over_code()
    {
        var e = Get("lighting.gcc");
        Assert.Equal(new[] { "GCC" }, e.Detect.Processes);
        Assert.Equal(new[] { @"\GCC" }, e.Detect.Tasks);
        Assert.True(e.Verified);
    }

    [Fact]
    public void Armoury_Crate_uses_the_same_programs_and_services_as_the_take_over_code()
    {
        var e = Get("sensors.armoury-crate");
        Assert.Equal(new[]
        {
            "ArmouryCrate", "ArmouryCrate.UserSessionHelper", "ArmouryCrate.Service", "ArmourySocketServer", "ArmourySwAgent",
            "ArmouryHtmlDebugServer", "asus_framework", "AcPowerNotification", "LightingService", "ROGLiveService",
        }, e.Detect.Processes);
        Assert.Equal(new[]
        {
            "ArmouryCrateService", "LightingService", "asComSvc", "AsusFanControlService", "ROG Live Service", "AsusCertService",
        }, e.Detect.Services);
        // The button only closes what is running, so a start-up task alone must not bring this finding back.
        Assert.Empty(e.Detect.Tasks);
        Assert.True(e.Verified);
        Assert.Equal("sensors.closeArmouryCrate", e.Fix!.Method);
    }

    [Fact]
    public void ASUS_start_up_tasks_are_a_note_of_their_own_with_no_button()
    {
        var e = Get("sensors.asus-startup-tasks");
        Assert.Equal(new[] { @"\ASUS\" }, e.Detect.Tasks);
        Assert.Empty(e.Detect.Processes);
        Assert.Empty(e.Detect.Services);
        Assert.Equal(ConflictSeverity.Info, e.Severity);
        Assert.Null(e.Fix);
        Assert.True(e.ExplainOnly);
    }

    [Theory]
    [InlineData("presence.richpresence")]
    [InlineData("presence.premid")]
    [InlineData("presence.other-tools")]
    [InlineData("overlay.discord")]
    [InlineData("overlay.steam")]
    public void Programs_each_user_runs_for_themselves_only_count_in_this_users_session(string id)
    {
        Assert.True(Get(id).Detect.ThisUserOnly);
    }

    [Theory]
    [InlineData("lighting.gcc")]
    [InlineData("cooling.nzxt-cam")]
    [InlineData("sensors.armoury-crate")]
    public void Programs_that_hold_hardware_count_in_every_session(string id)
    {
        Assert.False(Get(id).Detect.ThisUserOnly);
    }

    [Fact]
    public void RichPresence_uses_the_same_program_name_as_the_Discord_status_code_and_blocks_it()
    {
        var e = Get("presence.richpresence");
        Assert.Equal(new[] { "RichPresence" }, e.Detect.Processes);
        Assert.True(e.Verified);
        Assert.Equal(ConflictSeverity.Blocks, e.Severity);
        Assert.Null(e.Fix);
    }

    [Theory]
    // lights
    [InlineData("lighting.signalrgb")]
    [InlineData("lighting.icue")]
    [InlineData("lighting.razer-synapse")]
    [InlineData("lighting.razer-chroma")]
    [InlineData("lighting.armoury-crate")]
    [InlineData("lighting.msi-center")]
    [InlineData("lighting.rgb-fusion")]
    [InlineData("lighting.gcc")]
    [InlineData("lighting.nzxt-cam")]
    [InlineData("lighting.asrock-polychrome")]
    [InlineData("lighting.jacknet")]
    [InlineData("lighting.hue-sync")]
    [InlineData("lighting.openrgb-other")]
    [InlineData("lighting.dynamic-lighting")]
    // fans and sensors
    [InlineData("cooling.nzxt-cam")]
    [InlineData("sensors.fancontrol")]
    [InlineData("sensors.argus-monitor")]
    [InlineData("sensors.speedfan")]
    [InlineData("sensors.hwinfo")]
    [InlineData("sensors.aida64")]
    [InlineData("sensors.afterburner")]
    [InlineData("sensors.armoury-crate")]
    [InlineData("sensors.icue")]
    // graphics card tuning
    [InlineData("nvidia.afterburner")]
    [InlineData("nvidia.nvidia-app")]
    [InlineData("nvidia.gpu-tweak")]
    [InlineData("nvidia.evga-precision")]
    [InlineData("nvidia.gcc")]
    // recording
    [InlineData("capture.game-bar")]
    [InlineData("capture.game-bar-background")]
    [InlineData("capture.shadowplay")]
    [InlineData("capture.amd-relive")]
    [InlineData("capture.obs")]
    [InlineData("capture.medal")]
    [InlineData("capture.overwolf")]
    [InlineData("capture.steam-recording")]
    // sound
    [InlineData("audio.voicemeeter")]
    [InlineData("audio.sonar")]
    [InlineData("audio.nahimic")]
    [InlineData("audio.dolby")]
    [InlineData("audio.dts")]
    [InlineData("audio.fxsound")]
    [InlineData("audio.nvidia-broadcast")]
    [InlineData("audio.peace")]
    // Discord status
    [InlineData("presence.richpresence")]
    [InlineData("presence.premid")]
    [InlineData("presence.other-tools")]
    // overlay, controller, shortcuts
    [InlineData("overlay.game-bar-logo-button")]
    [InlineData("overlay.steam")]
    [InlineData("overlay.ds4windows")]
    [InlineData("overlay.dsx")]
    [InlineData("overlay.discord")]
    [InlineData("overlay.nvidia-overlay")]
    // Windows itself
    [InlineData("system.fast-startup")]
    [InlineData("system.memory-integrity")]
    [InlineData("system.driver-blocklist")]
    [InlineData("system.controlled-folder-access")]
    [InlineData("system.game-mode")]
    [InlineData("system.pending-restart")]
    [InlineData("drivers.windows-update")]
    [InlineData("system.not-elevated")]
    [InlineData("system.other-session")]
    // what gets in the way of lights on headers, hubs and the home network
    [InlineData("lighting.tt-rgb-plus")]
    [InlineData("lighting.l-connect")]
    [InlineData("lighting.public-network")]
    public void The_catalog_covers(string id)
    {
        Assert.Contains(ConflictCatalog.All, e => e.Id == id);
    }

    [Fact]
    public void Registry_places_are_written_without_a_leading_slash_and_always_name_a_value()
    {
        var tests = ConflictCatalog.All.SelectMany(e => e.Detect.Registry).ToList();
        Assert.NotEmpty(tests);
        Assert.All(tests, t =>
        {
            Assert.False(t.Key.StartsWith('\\'));
            Assert.False(string.IsNullOrWhiteSpace(t.Value));
        });
    }
}
