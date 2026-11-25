using System.Text.Json;
using RigControl.Core.Rpc;

namespace RigControl.Core.Modules;

/// <summary>
/// The one way a feature says "this cannot work on this kind of system" (Linux, macOS, or a Windows build without it).
/// It uses the status message the UI already reads: a message that starts with
/// <see cref="NotOnThisSystem"/> means the page is left out of the menu or shows one calm panel.
/// (The older "Not available on this PC: ..." is for a feature that does not fit this particular PC's hardware.)
/// </summary>
public static class ModuleAvailability
{
    /// <summary>The start of every such message, and the whole message when there is nothing to add.</summary>
    public const string NotOnThisSystem = "Not available on this system";

    /// <summary>"Not available on this system" or "Not available on this system: it needs Windows."</summary>
    public static string Message(string? why = null)
        => string.IsNullOrWhiteSpace(why) ? NotOnThisSystem : NotOnThisSystem + ": " + why.Trim();

    /// <summary>The status of a module that cannot work here at all.</summary>
    public static ModuleStatus Status(string? why = null) => new(ModuleState.Failed, Message(why));

    /// <summary>True for a status made by <see cref="Status"/> (any state, as long as the message starts that way).</summary>
    public static bool IsNotOnThisSystem(ModuleStatus status)
        => status.Message is { } m && m.StartsWith(NotOnThisSystem, StringComparison.Ordinal);

    /// <summary>The error to throw from a single RPC method that cannot work here while the rest of the module does.</summary>
    public static RpcException Error(string? why = null) => RpcException.Unavailable(Message(why) + (string.IsNullOrWhiteSpace(why) ? "." : string.Empty));
}

/// <summary>
/// Optional: a module that runs on more than one system says which of its parts work here, e.g. "lighting.effects"
/// but not "lighting.screen". The names are sent to the UI in app.getInfo ("capabilities") so it can hide or explain
/// controls. A module that does not implement this is taken to offer everything the host lists for it.
/// </summary>
public interface IModuleCapabilities
{
    /// <summary>Capability names, each starting with the module's own name and a dot. Safe to read from any thread.</summary>
    IReadOnlyCollection<string> Capabilities { get; }
}

/// <summary>
/// Stands in for a module that has no version for this system. It starts at once, reports
/// "Not available on this system", and answers every call with the "unavailable" error, so the UI gets the same
/// calm answer for any page instead of "unknown module".
/// </summary>
public sealed class UnavailableModule : IAppModule
{
    private readonly string? _why;

    public UnavailableModule(string name, string displayName, string? why = null)
    {
        Name = name;
        DisplayName = displayName;
        _why = why;
        Status = ModuleAvailability.Status(why);
    }

    public string Name { get; }
    public string DisplayName { get; }
    public ModuleStatus Status { get; }

    public Task StartAsync(ModuleContext context, CancellationToken ct) => Task.CompletedTask;

    public Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct)
        => Task.FromException<object?>(ModuleAvailability.Error(_why));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
