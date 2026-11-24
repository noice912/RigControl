using RigControl.Core.Conflicts;
using RigControl.Core.Logging;
using RigControl.Core.Modules;
using RigControl.Core.Settings;

namespace RigControl.Health.Tests;

/// <summary>"RigControl starts as administrator from a folder any program can change" is told on the Health page.</summary>
public sealed class ProgramFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-health-folder-" + Guid.NewGuid().ToString("N"));

    public ProgramFolderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<HealthReportDto> Report(Func<bool>? open)
    {
        var module = new HealthModule(new FakeProbe(), runLoop: false) { StartsAsAdminFromOpenFolder = open };
        await module.StartAsync(new ModuleContext(new RecordingBus(), NullLog.Instance,
            new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance),
            new AppEnvironment(_dir, _dir, true, "1.0.0", "RigControl.exe")), CancellationToken.None);
        return (HealthReportDto)(await module.InvokeAsync("rescan", null, CancellationToken.None))!;
    }

    [Fact]
    public async Task Starting_as_administrator_from_a_folder_any_program_can_change_is_explained_with_no_button()
    {
        var f = Assert.Single((await Report(() => true)).Findings, x => x.Id == "system.program-folder-open");
        Assert.Null(f.Fix);
        Assert.Contains("administrator", f.WhatItBreaks);
        Assert.Contains("Program Files", f.WhatToDo);
    }

    [Fact]
    public async Task A_safe_folder_or_an_answer_that_could_not_be_worked_out_says_nothing()
    {
        Assert.DoesNotContain((await Report(() => false)).Findings, x => x.Id == "system.program-folder-open");
        Assert.DoesNotContain((await Report(null)).Findings, x => x.Id == "system.program-folder-open");
        Assert.DoesNotContain((await Report(() => throw new InvalidOperationException("no"))).Findings, x => x.Id == "system.program-folder-open");
    }
}
