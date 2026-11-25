namespace RigControl.App.Install;

internal enum TaskChangeKind
{
    /// <summary>The task stays, but starts the installed exe from now on.</summary>
    Repoint,
    /// <summary>A new task for this user ("Start with Windows" ticked and none there yet).</summary>
    Create,
    /// <summary>The task is removed.</summary>
    Remove,
}

/// <param name="OldCommand">The program the task started before, when it could be read.</param>
internal sealed record TaskChange(string TaskName, TaskChangeKind Kind, string? OldCommand);

/// <summary>
/// Which start-up tasks the installer and the uninstaller change. Only this user's own task, and the old shared
/// "RigControl" task when it was made for them, are ever moved; another user's task is never touched by an install.
/// </summary>
internal static class TaskPlan
{
    /// <param name="startWithWindows">The setup window's tick. Null (an update) only moves what is already there.</param>
    public static IReadOnlyList<TaskChange> ForInstall(string userSid, Autostart.TaskAnswer own, Autostart.TaskAnswer legacy,
        string installedExe, bool? startWithWindows)
    {
        var existing = new List<(string Name, Autostart.TaskInfo? Info)>();
        if (own.Exists) existing.Add((Autostart.TaskNameFor(userSid), own.Info));
        if (legacy.Exists && Autostart.BelongsTo(legacy.Info, userSid)) existing.Add((Autostart.TaskName, legacy.Info));

        var changes = new List<TaskChange>();
        if (startWithWindows == false)
        {
            foreach (var (name, info) in existing) changes.Add(new TaskChange(name, TaskChangeKind.Remove, info?.Command));
            return changes;
        }
        foreach (var (name, info) in existing)
        {
            // A task switched off by hand in Task Scheduler stays as the user left it, unless they ticked the box now.
            if (startWithWindows is null && info is { Enabled: false }) continue;
            if (info is not null && InstallLayout.SamePath(info.Command, installedExe) && info.Enabled) continue;
            changes.Add(new TaskChange(name, TaskChangeKind.Repoint, info?.Command));
        }
        if (startWithWindows == true && existing.Count == 0)
            changes.Add(new TaskChange(Autostart.TaskNameFor(userSid), TaskChangeKind.Create, null));
        return changes;
    }

    /// <summary>
    /// What the uninstaller removes: this user's own task, and the old shared task when it starts RigControl from
    /// the install folder being removed (whoever it was made for: it could only fail from now on).
    /// </summary>
    public static IReadOnlyList<TaskChange> ForUninstall(string userSid, Autostart.TaskAnswer own, Autostart.TaskAnswer legacy, InstallLayout layout)
    {
        var changes = new List<TaskChange>();
        if (own.Exists || !own.Known) changes.Add(new TaskChange(Autostart.TaskNameFor(userSid), TaskChangeKind.Remove, own.Info?.Command));
        if (legacy.Exists && layout.Contains(legacy.Info?.Command)) changes.Add(new TaskChange(Autostart.TaskName, TaskChangeKind.Remove, legacy.Info?.Command));
        return changes;
    }

    /// <summary>
    /// The old copies of RigControl these changes leave on disk (the installer never deletes them; the finish
    /// screen names them so the user can).
    /// </summary>
    public static IReadOnlyList<string> OldCopies(IEnumerable<TaskChange> changes, InstallLayout layout, Func<string, bool> fileExists)
    {
        var list = new List<string>();
        foreach (var c in changes)
        {
            var old = c.OldCommand?.Trim().Trim('"');
            if (string.IsNullOrEmpty(old) || layout.Contains(old)) continue;
            if (!fileExists(old) || list.Any(x => InstallLayout.SamePath(x, old))) continue;
            list.Add(old);
        }
        return list;
    }
}
