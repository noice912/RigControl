namespace RigControl.Core.Conflicts;

/// <summary>The outcome of one scan: what was found, and the snapshot it was found in.</summary>
public sealed record ConflictScan(IReadOnlyList<ConflictFinding> Findings, ConflictSnapshot Snapshot);

/// <summary>
/// Compares one snapshot of the PC with the catalog. It never changes anything and never throws:
/// a probe that fails becomes an "info" finding that says what could not be checked.
/// </summary>
public sealed class ConflictScanner
{
    private readonly IReadOnlyList<ConflictEntry> _catalog;

    public ConflictScanner(IReadOnlyList<ConflictEntry>? catalog = null)
    {
        _catalog = catalog ?? ConflictCatalog.All;
        Query = BuildQuery(_catalog);
    }

    public IReadOnlyList<ConflictEntry> Catalog => _catalog;

    /// <summary>What the probe is asked for; worked out once from the catalog.</summary>
    public ConflictQuery Query { get; }

    /// <summary>Takes ONE snapshot and checks every catalog entry against it.</summary>
    public ConflictScan Scan(IConflictProbe probe)
    {
        ConflictSnapshot snapshot;
        try
        {
            snapshot = probe.Take(Query) ?? throw new InvalidOperationException("The check gave no answer.");
        }
        catch (Exception ex)
        {
            snapshot = new ConflictSnapshot { Failed = new[] { new ConflictProbeFailure("this PC", ex.Message) } };
        }
        return new ConflictScan(Evaluate(snapshot), snapshot);
    }

    /// <summary>The findings for a snapshot: most serious first, then by module and name.</summary>
    public IReadOnlyList<ConflictFinding> Evaluate(ConflictSnapshot snapshot)
    {
        var found = new List<ConflictFinding>();
        foreach (var entry in _catalog)
        {
            string? evidence;
            try { evidence = Match(entry, snapshot); }
            catch { evidence = null; } // one bad entry must not hide the others
            if (evidence is null) continue;
            found.Add(new ConflictFinding(entry.Id, entry.Module, entry.Name, entry.Kind, entry.Severity,
                entry.WhatItBreaks, entry.WhatToDo, entry.Verified, FixFor(entry), evidence));
        }
        foreach (var failure in snapshot.Failed ?? Array.Empty<ConflictProbeFailure>())
            found.Add(CouldNotCheck(failure));

        return found
            .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Module, StringComparer.Ordinal)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when the catalog entry with this id is met by the snapshot.</summary>
    public bool IsPresent(string entryId, ConflictSnapshot snapshot)
    {
        var entry = _catalog.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.OrdinalIgnoreCase));
        return entry is not null && Match(entry, snapshot) is not null;
    }

    /// <summary>A fix is only ever passed on for a verified entry that is not explain-only.</summary>
    private static ConflictFix? FixFor(ConflictEntry entry)
        => entry.Verified && !entry.ExplainOnly ? entry.Fix : null;

    private static ConflictFinding CouldNotCheck(ConflictProbeFailure failure)
    {
        var slug = new string(failure.Area.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return new ConflictFinding(
            "health.check." + slug,
            ConflictModules.System,
            "Couldn't check " + failure.Area,
            ConflictKind.Setting,
            ConflictSeverity.Info,
            $"RigControl couldn't look at {failure.Area}, so a conflict there may be missed.",
            "Press Check again. If it stays, start RigControl as administrator.",
            Verified: true,
            Fix: null,
            Evidence: failure.Reason);
    }

    /// <summary>Returns what gave the rival away, or null when it is not there.</summary>
    private static string? Match(ConflictEntry entry, ConflictSnapshot s)
    {
        var d = entry.Detect;
        var evidence = new List<string>();

        // A program each user runs for themselves only counts when it runs in this user's own session.
        var running = d.ThisUserOnly ? s.SessionProcesses ?? s.Processes : s.Processes;
        foreach (var name in d.Processes)
        {
            var hit = Find(running, name);
            if (hit is null) continue;
            if (d.ProcessPathMustNotContain is { Length: > 0 } own)
            {
                if (!s.ProcessPaths.TryGetValue(hit, out var paths)) continue;
                var foreign = paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && !p.Contains(own, StringComparison.OrdinalIgnoreCase));
                if (foreign is null) continue;
                evidence.Add($"{hit}.exe is running from {foreign}");
                break;
            }
            evidence.Add($"{hit}.exe is running");
            break;
        }

        foreach (var name in d.Services)
        {
            if (Find(s.Services, name) is not { } hit) continue;
            evidence.Add($"The {hit} service is running");
            break;
        }

        foreach (var name in d.Drivers)
        {
            if (Find(s.Drivers, name) is not { } hit) continue;
            evidence.Add($"The {hit} driver is loaded");
            break;
        }

        foreach (var task in d.Tasks)
        {
            if (!s.Tasks.Contains(task)) continue;
            evidence.Add(task.EndsWith('\\') ? $"It has start-up tasks switched on ({task})" : $"Its start-up task is switched on ({task})");
            break;
        }

        if (d.Registry.Count > 0 && d.Registry.All(t => t.Matches(s.Registry.TryGetValue(t.Place, out var r) ? r : null)))
            evidence.Add("Read from Windows settings");

        if (d.Facts.Count > 0 && d.Facts.All(f => s.Facts.TryGetValue(f, out var on) && on))
        {
            // What it was seen on, when the check could say (which network, for one).
            var notes = d.Facts.Select(f => s.FactNotes is { } all && all.TryGetValue(f, out var note) ? note.Trim().TrimEnd('.') : "")
                .Where(n => n.Length > 0).ToList();
            if (notes.Count > 0) evidence.AddRange(notes);
            else evidence.Add("Checked by RigControl");
        }

        return evidence.Count == 0 ? null : string.Join(". ", evidence) + ".";
    }

    /// <summary>Finds a name in a set; a pattern ending in "*" matches by its start. Returns the real name found.</summary>
    private static string? Find(IReadOnlySet<string> names, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            var start = pattern[..^1];
            if (start.Length == 0) return null; // "*" alone would match everything
            return names.FirstOrDefault(n => n.StartsWith(start, StringComparison.OrdinalIgnoreCase));
        }
        // The set may have been built with a different comparer, so don't rely on Contains alone.
        if (names.Contains(pattern)) return pattern;
        return names.FirstOrDefault(n => string.Equals(n, pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static ConflictQuery BuildQuery(IReadOnlyList<ConflictEntry> catalog)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registry = new Dictionary<string, RegistryTest>(StringComparer.OrdinalIgnoreCase);
        var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in catalog)
        {
            if (e.Detect.ProcessPathMustNotContain is { Length: > 0 })
                foreach (var p in e.Detect.Processes) paths.Add(p);
            foreach (var t in e.Detect.Registry) registry.TryAdd(t.Place, t);
            foreach (var t in e.Detect.Tasks) tasks.Add(t);
        }
        return new ConflictQuery
        {
            ProcessPathsFor = paths.ToList(),
            Registry = registry.Values.ToList(),
            Tasks = tasks.ToList(),
        };
    }
}
