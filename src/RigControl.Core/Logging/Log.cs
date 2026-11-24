using System.Globalization;
using System.Text;

namespace RigControl.Core.Logging;

public enum LogLevel { Info, Warn, Error }

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message);

public interface ILog
{
    void Info(string message);
    void Warn(string message, Exception? ex = null);
    void Error(string message, Exception? ex = null);

    /// <summary>Returns a logger that tags its lines with <paramref name="source"/> (usually a module name).</summary>
    ILog For(string source);
}

/// <summary>Discards everything. Handy for tests.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message, Exception? ex = null) { }
    public void Error(string message, Exception? ex = null) { }
    public ILog For(string source) => this;
}

/// <summary>
/// Appends to a daily log file and keeps the most recent lines in memory so the UI can show them.
/// Logging never throws: a failure to write the file is swallowed.
/// </summary>
public sealed class FileLog : ILog
{
    private readonly Sink _sink;
    private readonly string _source;

    public FileLog(string logDirectory, int memoryLines = 500, int keepFiles = 7)
        : this(new Sink(logDirectory, memoryLines, keepFiles), "app")
    {
    }

    private FileLog(Sink sink, string source)
    {
        _sink = sink;
        _source = source;
    }

    public void Info(string message) => _sink.Write(LogLevel.Info, _source, message, null);
    public void Warn(string message, Exception? ex = null) => _sink.Write(LogLevel.Warn, _source, message, ex);
    public void Error(string message, Exception? ex = null) => _sink.Write(LogLevel.Error, _source, message, ex);
    public ILog For(string source) => new FileLog(_sink, source);

    /// <summary>The most recent entries, oldest first.</summary>
    public IReadOnlyList<LogEntry> Recent() => _sink.Recent();

    public string Directory => _sink.Directory;

    private sealed class Sink
    {
        private readonly object _gate = new();
        private readonly Queue<LogEntry> _recent = new();
        private readonly int _memoryLines;
        public string Directory { get; }

        public Sink(string directory, int memoryLines, int keepFiles)
        {
            Directory = directory;
            _memoryLines = Math.Max(1, memoryLines);
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                var old = new DirectoryInfo(directory).GetFiles("rigcontrol-*.log")
                    .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                    .Skip(Math.Max(1, keepFiles));
                foreach (var f in old)
                {
                    try { f.Delete(); } catch { /* best effort */ }
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        public void Write(LogLevel level, string source, string message, Exception? ex)
        {
            var now = DateTimeOffset.Now;
            var text = ex is null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message;
            var entry = new LogEntry(now, level, source, text);
            lock (_gate)
            {
                _recent.Enqueue(entry);
                while (_recent.Count > _memoryLines) _recent.Dequeue();
                try
                {
                    var path = Path.Combine(Directory, "rigcontrol-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                    var sb = new StringBuilder();
                    sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                      .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5))
                      .Append(" [").Append(source).Append("] ").AppendLine(text);
                    if (ex is not null) sb.AppendLine(ex.ToString());
                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
                catch
                {
                    // Disk full, folder removed, etc. Keep going with the in-memory copy.
                }
            }
        }

        public IReadOnlyList<LogEntry> Recent()
        {
            lock (_gate) return _recent.ToArray();
        }
    }
}
