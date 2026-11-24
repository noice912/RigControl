namespace RigControl.Core.Conflicts;

/// <summary>
/// Looks at the PC once and reports what is there. Read-only: an implementation must never change anything.
/// It should not throw; whatever it could not look at goes into <see cref="ConflictSnapshot.Failed"/>.
/// </summary>
public interface IConflictProbe
{
    ConflictSnapshot Take(ConflictQuery query);
}

/// <summary>What the catalog wants to know beyond the plain lists, so a probe reads only what is needed.</summary>
public sealed record ConflictQuery
{
    /// <summary>Program names whose file path is needed.</summary>
    public IReadOnlyList<string> ProcessPathsFor { get; init; } = Array.Empty<string>();

    /// <summary>Every registry place a catalog entry reads.</summary>
    public IReadOnlyList<RegistryTest> Registry { get; init; } = Array.Empty<RegistryTest>();

    /// <summary>Scheduled task paths (or folders ending in "\") to look for.</summary>
    public IReadOnlyList<string> Tasks { get; init; } = Array.Empty<string>();
}

/// <summary>Something the probe could not look at, e.g. Area "Windows services".</summary>
public sealed record ConflictProbeFailure(string Area, string Reason);

/// <summary>One look at the PC. Every name set ignores upper and lower case.</summary>
public sealed record ConflictSnapshot
{
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of running programs, without ".exe".</summary>
    public IReadOnlySet<string> Processes { get; init; } = NoNames;

    /// <summary>
    /// Names of the programs running in this Windows user's own session, without ".exe". Null when the probe cannot
    /// tell sessions apart; every running program then counts as this user's.
    /// </summary>
    public IReadOnlySet<string>? SessionProcesses { get; init; }

    /// <summary>File paths of the running programs asked for in <see cref="ConflictQuery.ProcessPathsFor"/> (name to paths).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ProcessPaths { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Service names of running Windows services.</summary>
    public IReadOnlySet<string> Services { get; init; } = NoNames;

    /// <summary>Service names of loaded kernel drivers.</summary>
    public IReadOnlySet<string> Drivers { get; init; } = NoNames;

    /// <summary>The asked-for scheduled tasks (or folders) that exist and are switched on, spelled as in the query.</summary>
    public IReadOnlySet<string> Tasks { get; init; } = NoNames;

    /// <summary>Registry readings by <see cref="RegistryTest.Place"/>. A place that could not be read is left out.</summary>
    public IReadOnlyDictionary<string, RegistryReading> Registry { get; init; }
        = new Dictionary<string, RegistryReading>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Facts by <see cref="ConflictFacts"/> name. A fact that could not be worked out is left out.</summary>
    public IReadOnlyDictionary<string, bool> Facts { get; init; } = new Dictionary<string, bool>();

    /// <summary>
    /// What a fact was seen on, by <see cref="ConflictFacts"/> name: one plain sentence without its full stop
    /// (for example which network is set to Public). Shown as the finding's evidence. Optional.
    /// </summary>
    public IReadOnlyDictionary<string, string> FactNotes { get; init; } = new Dictionary<string, string>();

    /// <summary>Numbers by <see cref="ConflictCounts"/> name.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();

    /// <summary>What could not be looked at.</summary>
    public IReadOnlyList<ConflictProbeFailure> Failed { get; init; } = Array.Empty<ConflictProbeFailure>();
}
