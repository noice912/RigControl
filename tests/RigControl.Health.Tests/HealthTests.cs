using System.Text.Json;
using RigControl.Core.Conflicts;
using RigControl.Core.Json;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;

namespace RigControl.Health.Tests;

/// <summary>A pretend PC: tests say what is running and what the registry holds.</summary>
internal sealed class FakeProbe : IConflictProbe
{
    public HashSet<string> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegistryReading> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Facts { get; } = new();
    public Dictionary<string, string> FactNotes { get; } = new();
    public Dictionary<string, int> Counts { get; } = new();
    public List<ConflictProbeFailure> Failed { get; } = new();
    public Exception? Throw { get; set; }
    public int Taken { get; private set; }

    public void SetNumber(RegistryRoot root, string key, string value, long number)
        => Registry[new RegistryTest(root, key, value, RegistryCheck.Is).Place] = new RegistryReading(true, true, number);

    public ConflictSnapshot Take(ConflictQuery query)
    {
        Taken++;
        if (Throw is not null) throw Throw;
        return new ConflictSnapshot
        {
            Processes = new HashSet<string>(Processes, StringComparer.OrdinalIgnoreCase),
            Services = new HashSet<string>(Services, StringComparer.OrdinalIgnoreCase),
            Registry = new Dictionary<string, RegistryReading>(Registry, StringComparer.OrdinalIgnoreCase),
            Facts = new Dictionary<string, bool>(Facts),
            FactNotes = new Dictionary<string, string>(FactNotes),
            Counts = new Dictionary<string, int>(Counts),
            Failed = Failed.ToList(),
        };
    }
}

internal sealed class RecordingBus : IEventBus
{
    public List<(string Topic, object? Payload)> Events { get; } = new();
    public void Publish(string topic, object? payload) => Events.Add((topic, payload));
}

public sealed class HealthModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-health-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProbe _probe = new();
    private readonly RecordingBus _bus = new();

    public HealthModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<HealthModule> StartAsync(bool elevated = true)
    {
        var module = new HealthModule(_probe, runLoop: false, now: () => new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var ctx = new ModuleContext(_bus, NullLog.Instance, new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, elevated, "1.0.0", "RigControl.exe"));
        await module.StartAsync(ctx, CancellationToken.None);
        return module;
    }

    private static async Task<HealthReportDto> Call(HealthModule module, string method)
        => (HealthReportDto)(await module.InvokeAsync(method, null, CancellationToken.None))!;

    [Fact]
    public async Task It_is_the_health_module_and_is_ready_after_start()
    {
        var module = await StartAsync();
        Assert.Equal("health", module.Name);
        Assert.Equal("Health", module.DisplayName);
        Assert.Equal(ModuleState.Ready, module.Status.State);
    }

    [Fact]
    public async Task A_clean_PC_gives_an_empty_report()
    {
        var module = await StartAsync();
        var report = await Call(module, "getReport");
        Assert.Empty(report.Findings);
        Assert.Equal(new HealthCountsDto(0, 0, 0), report.Counts);
        Assert.Equal(2026, report.ScannedAt.Year);
    }

    [Fact]
    public async Task The_report_counts_findings_by_severity()
    {
        _probe.Processes.Add("NZXT CAM");   // blocks cooling, warns lighting
        _probe.Processes.Add("HWiNFO64");   // info
        var module = await StartAsync();
        var report = await Call(module, "getReport");
        Assert.Equal(new HealthCountsDto(1, 1, 1), report.Counts);
        Assert.Equal("cooling.nzxt-cam", report.Findings[0].Id);
    }

    [Fact]
    public async Task getReport_reuses_the_last_scan_and_rescan_looks_again()
    {
        var module = await StartAsync();
        await Call(module, "getReport");
        await Call(module, "getReport");
        Assert.Equal(1, _probe.Taken);

        _probe.Processes.Add("FanControl");
        Assert.Empty((await Call(module, "getReport")).Findings);
        var again = await Call(module, "rescan");
        Assert.Equal(2, _probe.Taken);
        Assert.Contains(again.Findings, f => f.Id == "sensors.fancontrol");
    }

    [Fact]
    public async Task The_event_is_published_only_when_the_findings_change()
    {
        var module = await StartAsync();
        module.ScanNow();
        Assert.Single(_bus.Events);
        Assert.Equal("health.report", _bus.Events[0].Topic);

        module.ScanNow();
        Assert.Single(_bus.Events);

        _probe.Processes.Add("iCUE");
        module.ScanNow();
        Assert.Equal(2, _bus.Events.Count);
        var sent = Assert.IsType<HealthReportDto>(_bus.Events[1].Payload);
        Assert.Contains(sent.Findings, f => f.Id == "lighting.icue");

        _probe.Processes.Remove("iCUE");
        module.ScanNow();
        Assert.Equal(3, _bus.Events.Count);
    }

    [Fact]
    public async Task A_probe_that_throws_gives_a_report_with_one_info_finding()
    {
        _probe.Throw = new InvalidOperationException("Windows said no.");
        var module = await StartAsync();
        var report = await Call(module, "rescan");
        var f = Assert.Single(report.Findings);
        Assert.Equal(ConflictSeverity.Info, f.Severity);
        Assert.StartsWith("Couldn't check", f.Name);
        Assert.Equal(new HealthCountsDto(0, 0, 1), report.Counts);
    }

    [Fact]
    public async Task The_system_block_reflects_the_Windows_settings_that_were_read()
    {
        _probe.Facts[ConflictFacts.NotElevated] = false;
        _probe.Facts[ConflictFacts.PendingRestart] = true;
        _probe.Facts[ConflictFacts.OtherSessionRunsRigControl] = true;
        _probe.Counts[ConflictCounts.Sessions] = 2;
        _probe.SetNumber(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1);
        _probe.SetNumber(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0);
        _probe.SetNumber(RegistryRoot.CurrentUser, @"Software\Microsoft\Lighting", "AmbientLightingEnabled", 1);

        var module = await StartAsync();
        var system = (await Call(module, "getReport")).System;

        Assert.True(system.Elevated);
        Assert.True(system.FastStartup);
        Assert.False(system.MemoryIntegrity);
        Assert.True(system.DynamicLighting);
        Assert.True(system.PendingRestart);
        Assert.Equal(2, system.SessionCount);
        Assert.True(system.OtherSessionRunsRigControl);
        // Not read at all: unknown, not "off".
        Assert.Null(system.DriverBlocklist);
        Assert.Null(system.ControlledFolderAccess);
        Assert.Null(system.GameBarBackgroundRecording);
    }

    [Fact]
    public async Task The_finding_names_the_network_that_is_set_to_Public()
    {
        var module = await StartAsync();
        _probe.Facts[ConflictFacts.PublicNetwork] = true;
        _probe.FactNotes[ConflictFacts.PublicNetwork] = "The network \"Home Wi-Fi\" is set to Public";
        var finding = Assert.Single((await Call(module, "rescan")).Findings, f => f.Id == "lighting.public-network");
        Assert.Equal("Your network is set to Public in Windows", finding.Name);
        Assert.Equal("The network \"Home Wi-Fi\" is set to Public.", finding.Evidence);
        Assert.Contains("the connection named above", finding.WhatToDo);
    }

    [Fact]
    public async Task A_network_set_to_Public_is_explained_for_lights_on_the_network_and_never_gets_a_button()
    {
        var module = await StartAsync();
        Assert.DoesNotContain((await Call(module, "rescan")).Findings, f => f.Id == "lighting.public-network");

        _probe.Facts[ConflictFacts.PublicNetwork] = true;
        var finding = Assert.Single((await Call(module, "rescan")).Findings, f => f.Id == "lighting.public-network");

        Assert.Equal(ConflictSeverity.Info, finding.Severity);
        Assert.Null(finding.Fix);
        Assert.Contains("Private", finding.WhatToDo);
        Assert.Contains("never changes", finding.WhatToDo);
    }

    [Fact]
    public async Task Without_an_answer_from_the_probe_elevated_comes_from_the_app()
    {
        Assert.True((await Call(await StartAsync(elevated: true), "getReport")).System.Elevated);
        Assert.False((await Call(await StartAsync(elevated: false), "getReport")).System.Elevated);
    }

    [Fact]
    public async Task An_unknown_method_is_refused()
    {
        var module = await StartAsync();
        var ex = await Assert.ThrowsAsync<RpcException>(() => module.InvokeAsync("fix", null, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.UnknownMethod, ex.Code);
    }

    [Fact]
    public async Task The_report_reaches_the_UI_with_these_field_names()
    {
        _probe.Processes.Add("GCC");
        _probe.SetNumber(RegistryRoot.CurrentUser, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 1);
        var module = await StartAsync();
        using var doc = JsonDocument.Parse(JsonDefaults.Serialize(await Call(module, "getReport")));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("scannedAt", out _));
        var counts = root.GetProperty("counts");
        Assert.Equal(0, counts.GetProperty("blocks").GetInt32());
        Assert.Equal(2, counts.GetProperty("warn").GetInt32());
        Assert.Equal(1, counts.GetProperty("info").GetInt32());

        var system = root.GetProperty("system");
        foreach (var name in new[]
                 {
                     "elevated", "fastStartup", "memoryIntegrity", "driverBlocklist", "controlledFolderAccess", "pendingRestart",
                     "gameBarRecording", "gameBarBackgroundRecording", "gameBarLogoButton", "gameMode", "dynamicLighting",
                     "windowsUpdateDrivers", "sessionCount", "otherSessionRunsRigControl",
                 })
            Assert.True(system.TryGetProperty(name, out _), name);

        var gcc = root.GetProperty("findings").EnumerateArray().First(f => f.GetProperty("id").GetString() == "lighting.gcc");
        Assert.Equal("lighting", gcc.GetProperty("module").GetString());
        Assert.Equal("GIGABYTE Control Center", gcc.GetProperty("name").GetString());
        Assert.Equal("process", gcc.GetProperty("kind").GetString());
        Assert.Equal("warn", gcc.GetProperty("severity").GetString());
        Assert.False(string.IsNullOrEmpty(gcc.GetProperty("whatItBreaks").GetString()));
        Assert.False(string.IsNullOrEmpty(gcc.GetProperty("whatToDo").GetString()));
        Assert.True(gcc.GetProperty("verified").GetBoolean());
        Assert.Equal("GCC.exe is running.", gcc.GetProperty("evidence").GetString());
        var fix = gcc.GetProperty("fix");
        Assert.Equal("lighting.takeOverGcc", fix.GetProperty("method").GetString());
        Assert.False(string.IsNullOrEmpty(fix.GetProperty("label").GetString()));
        Assert.Equal(JsonValueKind.Null, fix.GetProperty("args").ValueKind);
        Assert.False(string.IsNullOrEmpty(fix.GetProperty("confirm").GetString()));

        var logo = root.GetProperty("findings").EnumerateArray().First(f => f.GetProperty("id").GetString() == "overlay.game-bar-logo-button");
        Assert.Equal("setting", logo.GetProperty("kind").GetString());
        Assert.Equal("info", logo.GetProperty("severity").GetString());
        Assert.Equal("overlay.setSettings", logo.GetProperty("fix").GetProperty("method").GetString());
        Assert.True(logo.GetProperty("fix").GetProperty("args").GetProperty("gameBarButtonOff").GetBoolean());
    }

    [Fact]
    public async Task Stopping_the_module_is_safe_to_do_twice()
    {
        var module = await StartAsync();
        await module.DisposeAsync();
        await module.DisposeAsync();
        Assert.Equal(ModuleState.Stopped, module.Status.State);
    }
}

public sealed class TaskFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rigcontrol-health-tasks-" + Guid.NewGuid().ToString("N"));

    public TaskFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relative, string? enabled)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var setting = enabled is null ? "" : $"<Enabled>{enabled}</Enabled>";
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>" +
            $"<Settings>{setting}<Hidden>false</Hidden></Settings>" +
            "<Actions><Exec><Command>x.exe</Command></Exec></Actions></Task>",
            System.Text.Encoding.Unicode);
    }

    [Fact]
    public void A_VPN_or_a_virtual_machines_network_set_to_Public_does_not_count_when_the_home_network_is_Private()
    {
        // Home Wi-Fi is Private; VirtualBox's host-only network and a VPN are "Public" by Windows' default.
        var (isPublic, names) = WindowsConflictProbe.Judge(new[]
        {
            new WindowsConflictProbe.SeenNetwork("Home Wi-Fi", IsPublic: false, CarriesHomeNetwork: true),
            new WindowsConflictProbe.SeenNetwork("Unidentified network", IsPublic: true, CarriesHomeNetwork: false),
            new WindowsConflictProbe.SeenNetwork("NordLynx", IsPublic: true, CarriesHomeNetwork: false),
        });
        Assert.False(isPublic);
        Assert.Empty(names);
    }

    [Fact]
    public void A_home_network_set_to_Public_is_named()
    {
        var (isPublic, names) = WindowsConflictProbe.Judge(new[]
        {
            new WindowsConflictProbe.SeenNetwork("Home Wi-Fi", IsPublic: true, CarriesHomeNetwork: true),
            new WindowsConflictProbe.SeenNetwork("Unidentified network", IsPublic: true, CarriesHomeNetwork: false),
        });
        Assert.True(isPublic);
        Assert.Equal(new[] { "Home Wi-Fi" }, names);
        Assert.Equal("The network \"Home Wi-Fi\" is set to Public", WindowsConflictProbe.PublicNetworkNote(names));
        Assert.Equal("These networks are set to Public: \"Cable\", \"Wi-Fi\"", WindowsConflictProbe.PublicNetworkNote(new[] { "Cable", "Wi-Fi" }));
        Assert.Null(WindowsConflictProbe.PublicNetworkNote(Array.Empty<string>()));
    }

    [Fact]
    public void With_only_VPN_or_virtual_networks_there_is_nothing_to_judge()
    {
        var (isPublic, _) = WindowsConflictProbe.Judge(new[] { new WindowsConflictProbe.SeenNetwork("Unidentified network", true, false) });
        Assert.Null(isPublic);
        Assert.Null(WindowsConflictProbe.Judge(Array.Empty<WindowsConflictProbe.SeenNetwork>()).IsPublic);
    }

    [Theory]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "Intel(R) Ethernet Controller I226-V", true, true)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, "Intel(R) Wi-Fi 6E AX211", true, true)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "Intel(R) Ethernet Controller I226-V", false, false)] // no router
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "VirtualBox Host-Only Ethernet Adapter", true, false)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter", true, false)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "TAP-Windows Adapter V9", true, false)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Tunnel, "WireGuard Tunnel", true, false)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ppp, "VPN connection", true, false)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Loopback, "Loopback", true, false)]
    public void Only_a_cable_or_wifi_adapter_with_a_router_carries_the_home_network(
        System.Net.NetworkInformation.NetworkInterfaceType type, string description, bool hasRouter, bool expected)
    {
        Assert.Equal(expected, WindowsConflictProbe.IsHomeAdapter(type, description, hasRouter));
    }

    [Fact]
    public void Asking_Windows_whether_the_network_is_Public_only_reads_and_never_throws()
    {
        // Whatever this PC is connected to (yes, no, or "can't tell"), it must never throw.
        var error = Record.Exception(() => { _ = WindowsConflictProbe.OnPublicNetwork(); });
        Assert.Null(error);
    }

    [Fact]
    public void A_task_that_does_not_exist_is_not_on()
    {
        Assert.False(WindowsConflictProbe.TaskIsOn(_root, @"\GCC"));
        Assert.False(WindowsConflictProbe.TaskIsOn(_root, @"\ASUS\"));
    }

    [Fact]
    public void A_task_is_on_unless_its_own_file_says_it_is_switched_off()
    {
        Write("GCC", "true");
        Assert.True(WindowsConflictProbe.TaskIsOn(_root, @"\GCC"));
        Write("GCC", null);
        Assert.True(WindowsConflictProbe.TaskIsOn(_root, @"\GCC"));
        Write("GCC", "false");
        Assert.False(WindowsConflictProbe.TaskIsOn(_root, @"\GCC"));
    }

    [Fact]
    public void A_switched_off_task_with_a_switched_on_trigger_is_still_off()
    {
        // The trigger has its own <Enabled>true</Enabled>; only the one under <Settings> counts.
        Write("GCC", "false");
        Assert.False(WindowsConflictProbe.TaskIsOn(_root, @"\GCC"));
    }

    [Fact]
    public void A_folder_is_on_when_any_task_in_it_is_on()
    {
        Write(Path.Combine("ASUS", "one"), "false");
        Assert.False(WindowsConflictProbe.TaskIsOn(_root, @"\ASUS\"));
        Write(Path.Combine("ASUS", "deeper", "two"), "true");
        Assert.True(WindowsConflictProbe.TaskIsOn(_root, @"\ASUS\"));
    }

    [Fact]
    public void A_path_that_tries_to_leave_the_task_folder_is_ignored()
    {
        Assert.False(WindowsConflictProbe.TaskIsOn(_root, @"\..\..\Windows\win.ini"));
        Assert.False(WindowsConflictProbe.TaskIsOn(_root, @"\"));
    }
}

public class LiveProbeTests
{
    /// <summary>
    /// Looks at THIS PC (read-only) and writes what it found to a file. It only runs when asked for:
    /// set RIGCONTROL_HEALTH_LIVE to the file to write. Otherwise it does nothing, so normal test runs never touch the PC.
    /// </summary>
    [Fact]
    public void The_real_probe_can_look_at_this_PC_when_asked_to()
    {
        var output = Environment.GetEnvironmentVariable("RIGCONTROL_HEALTH_LIVE");
        if (string.IsNullOrWhiteSpace(output)) return;

        var scanner = new ConflictScanner();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var scan = scanner.Scan(new WindowsConflictProbe());
        watch.Stop();

        var lines = new List<string> { $"took {watch.ElapsedMilliseconds} ms; {scan.Snapshot.Processes.Count} programs, {scan.Snapshot.Services.Count} services, {scan.Snapshot.Drivers.Count} drivers" };
        lines.AddRange(scan.Findings.Select(f => $"{f.Severity,-6} {f.Id,-34} {f.Name} | {f.Evidence}"));
        File.WriteAllLines(output, lines);
        Assert.NotEmpty(scan.Snapshot.Processes);
    }
}
