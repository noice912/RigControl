using System.Text.Json;
using RigControl.Core.Json;
using RigControl.Core.Logging;

namespace RigControl.Core.Rpc;

/// <summary>
/// A group of methods the UI can call. A request for "cooling.setPumpDuty" is routed to the module
/// whose <see cref="Name"/> is "cooling" with method "setPumpDuty".
/// </summary>
public interface IRpcModule
{
    /// <summary>Lower-case module name used as the method prefix, e.g. "sensors".</summary>
    string Name { get; }

    /// <summary>
    /// Runs one method. Return any JSON-serialisable object (or null). Throw <see cref="RpcException"/>
    /// for expected failures the UI should show to the user; any other exception is reported as "internal".
    /// </summary>
    Task<object?> InvokeAsync(string method, JsonElement? args, CancellationToken ct);
}

/// <summary>An expected failure with a stable machine-readable code and a message fit to show the user.</summary>
public sealed class RpcException : Exception
{
    public string Code { get; }

    public RpcException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }

    public static RpcException UnknownMethod(string module, string method)
        => new(RpcErrorCodes.UnknownMethod, $"Unknown method '{module}.{method}'.");

    public static RpcException InvalidArgs(string message)
        => new(RpcErrorCodes.InvalidArgs, message);

    public static RpcException Unavailable(string message)
        => new(RpcErrorCodes.Unavailable, message);

    public static RpcException NeedsAdmin(string message)
        => new(RpcErrorCodes.NeedsAdmin, message);

    public static RpcException Unsafe(string message)
        => new(RpcErrorCodes.Unsafe, message);
}

public static class RpcErrorCodes
{
    public const string BadRequest = "badRequest";
    public const string UnknownModule = "unknownModule";
    public const string UnknownMethod = "unknownMethod";
    public const string InvalidArgs = "invalidArgs";
    /// <summary>The hardware / service behind the call is not present or not ready.</summary>
    public const string Unavailable = "unavailable";
    /// <summary>The call needs the app to be running as administrator.</summary>
    public const string NeedsAdmin = "needsAdmin";
    /// <summary>The request was refused because it would violate a safety limit.</summary>
    public const string Unsafe = "unsafe";
    public const string Cancelled = "cancelled";
    public const string Internal = "internal";
}

/// <summary>Helpers for reading the "params" object of a request.</summary>
public static class RpcArgs
{
    /// <summary>Deserialises the params object into <typeparamref name="T"/>; throws invalidArgs when missing or malformed.</summary>
    public static T Require<T>(JsonElement? args) where T : class
    {
        if (args is null || args.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw RpcException.InvalidArgs("This method requires parameters.");
        try
        {
            return args.Value.Deserialize<T>(JsonDefaults.Options)
                   ?? throw RpcException.InvalidArgs("This method requires parameters.");
        }
        catch (JsonException ex)
        {
            throw new RpcException(RpcErrorCodes.InvalidArgs, "Parameters are malformed: " + ex.Message, ex);
        }
    }

    /// <summary>Deserialises the params object, or returns a default instance when no params were sent.</summary>
    public static T Optional<T>(JsonElement? args) where T : class, new()
    {
        if (args is null || args.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new T();
        return Require<T>(args);
    }
}

/// <summary>
/// Routes JSON requests from the UI to modules and wraps the outcome in a response envelope.
/// Request:  {"id": 7, "method": "module.method", "params": {...}}
/// Response: {"id": 7, "ok": true, "result": ...}  or  {"id": 7, "ok": false, "error": {"code": "...", "message": "..."}}
/// A failure in one module never throws out of <see cref="HandleAsync"/>.
/// </summary>
public sealed class RpcDispatcher
{
    private readonly Dictionary<string, IRpcModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILog _log;

    public RpcDispatcher(ILog log)
    {
        _log = log;
    }

    public void Register(IRpcModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.Name) || module.Name.Contains('.'))
            throw new ArgumentException("Module name must be non-empty and contain no '.'.", nameof(module));
        _modules[module.Name] = module;
    }

    public IReadOnlyCollection<string> ModuleNames => _modules.Keys;

    public async Task<string> HandleAsync(string requestJson, CancellationToken ct = default)
    {
        JsonElement? id = null;
        string methodForLog = "?";
        try
        {
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(requestJson);
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new RpcException(RpcErrorCodes.BadRequest, "Request is not valid JSON: " + ex.Message, ex);
            }

            if (root.ValueKind != JsonValueKind.Object)
                throw new RpcException(RpcErrorCodes.BadRequest, "Request must be a JSON object.");
            if (root.TryGetProperty("id", out var idEl)) id = idEl;

            if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
                throw new RpcException(RpcErrorCodes.BadRequest, "Request has no 'method' string.");
            var full = methodEl.GetString()!;
            methodForLog = full;
            var dot = full.IndexOf('.');
            if (dot <= 0 || dot == full.Length - 1)
                throw new RpcException(RpcErrorCodes.BadRequest, $"Method '{full}' must look like 'module.method'.");
            var moduleName = full[..dot];
            var method = full[(dot + 1)..];

            if (!_modules.TryGetValue(moduleName, out var module))
                throw new RpcException(RpcErrorCodes.UnknownModule, $"Unknown module '{moduleName}'.");

            JsonElement? args = null;
            if (root.TryGetProperty("params", out var p) && p.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                args = p;

            var result = await module.InvokeAsync(method, args, ct).ConfigureAwait(false);
            return JsonDefaults.Serialize(new RpcResponse(id, true, result, null));
        }
        catch (RpcException ex)
        {
            _log.Warn($"RPC {methodForLog} failed: [{ex.Code}] {ex.Message}");
            return Error(id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error(id, RpcErrorCodes.Cancelled, "The operation was cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error($"RPC {methodForLog} threw an unexpected exception.", ex);
            return Error(id, RpcErrorCodes.Internal, ex.Message);
        }
    }

    private static string Error(JsonElement? id, string code, string message)
        => JsonDefaults.Serialize(new RpcResponse(id, false, null, new RpcError(code, message)));

    private sealed record RpcResponse(JsonElement? Id, bool Ok, object? Result, RpcError? Error);

    private sealed record RpcError(string Code, string Message);
}
