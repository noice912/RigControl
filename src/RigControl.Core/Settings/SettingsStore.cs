using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.Core.Json;
using RigControl.Core.Logging;

namespace RigControl.Core.Settings;

/// <summary>
/// One JSON file holding a section per module. Thread-safe. Writes are atomic (temp file + replace).
/// A corrupt file is moved aside and replaced by an empty one instead of crashing the app.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly ILog _log;
    private readonly object _gate = new();
    private JsonObject _root;

    public SettingsStore(string path, ILog log)
    {
        _path = path;
        _log = log;
        _root = Load();
    }

    public string FilePath => _path;

    /// <summary>The names of the sections in the file as it was read or last saved (for the start-up log).</summary>
    public IReadOnlyList<string> SectionNames
    {
        get { lock (_gate) return _root.Select(p => p.Key).ToList(); }
    }

    private JsonObject Load()
    {
        try
        {
            if (!File.Exists(_path)) return new JsonObject();
            var text = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            _log.Error("Settings file is unreadable; starting with defaults.", ex);
            try
            {
                var backup = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(_path, backup, overwrite: true);
            }
            catch
            {
                // If we cannot move it aside we will simply overwrite it on the next save.
            }
            return new JsonObject();
        }
    }

    /// <summary>Returns the stored section, or a new default instance when missing or malformed.</summary>
    public T Get<T>(string section) where T : class, new()
    {
        lock (_gate)
        {
            try
            {
                if (_root.TryGetPropertyValue(section, out var node) && node is not null)
                    return node.Deserialize<T>(JsonDefaults.Options) ?? new T();
            }
            catch (Exception ex)
            {
                _log.Warn($"Settings section '{section}' is malformed; using defaults.", ex);
            }
            return new T();
        }
    }

    /// <summary>Replaces the section and saves the file. Returns false (and logs) if the save failed.</summary>
    public bool Set<T>(string section, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _root[section] = JsonSerializer.SerializeToNode(value, JsonDefaults.Options);
            return Save();
        }
    }

    /// <summary>Reads the section, lets <paramref name="change"/> modify it, then saves.</summary>
    public T Update<T>(string section, Action<T> change) where T : class, new()
    {
        lock (_gate)
        {
            var current = Get<T>(section);
            change(current);
            Set(section, current);
            return current;
        }
    }

    private bool Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, _root.ToJsonString(JsonDefaults.Indented));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Could not save settings.", ex);
            return false;
        }
    }
}
