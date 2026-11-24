using RigControl.Core.Conflicts;

namespace RigControl.Core.Tests.Conflicts;

/// <summary>A pretend PC: tests say what is running and what the registry holds.</summary>
internal sealed class FakeProbe : IConflictProbe
{
    public HashSet<string> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The programs in this user's own session. Null = the pretend PC cannot tell sessions apart.</summary>
    public HashSet<string>? SessionProcesses { get; set; }
    public Dictionary<string, IReadOnlyList<string>> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Drivers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegistryReading> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Facts { get; } = new();
    public List<ConflictProbeFailure> Failed { get; } = new();
    public Exception? Throw { get; set; }
    public int Taken { get; private set; }
    public ConflictQuery? LastQuery { get; private set; }

    public void SetNumber(RegistryRoot root, string key, string value, long number)
        => Registry[new RegistryTest(root, key, value, RegistryCheck.Is).Place] = new RegistryReading(true, true, number);

    public void SetMissing(RegistryRoot root, string key, string value)
        => Registry[new RegistryTest(root, key, value, RegistryCheck.Is).Place] = new RegistryReading(false, false, null);

    public ConflictSnapshot Take(ConflictQuery query)
    {
        Taken++;
        LastQuery = query;
        if (Throw is not null) throw Throw;
        return new ConflictSnapshot
        {
            Processes = Processes, SessionProcesses = SessionProcesses, ProcessPaths = Paths, Services = Services, Drivers = Drivers, Tasks = Tasks,
            Registry = Registry, Facts = Facts, Failed = Failed,
        };
    }
}
