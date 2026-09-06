using Nami.Sdk;
using System.Reflection;

namespace Nami.Core.Logging;

/// <summary>A logger that fans out to multiple <see cref="ILogSink"/>s with a global minimum level.</summary>
public sealed class LogHub : ILog
{
    private readonly List<ILogSink> _sinks = new();
    private readonly Lock _lock = new();

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public void AddSink(ILogSink sink)
    {
        lock (_lock)
        {
            _sinks.Add(sink);
        }
    }

    public void Log(LogLevel level, string message) => Log("nami", level, message);

    public void Log(string source, LogLevel level, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.UtcNow, level, source, message);
        ILogSink[] snapshot;
        lock (_lock)
        {
            snapshot = _sinks.ToArray();
        }

        foreach (var sink in snapshot)
        {
            try
            {
                sink.Emit(entry);
            }
            catch
            {
                // A broken sink must never take the loader down.
            }
        }
    }
}

/// <summary>A single formatted log record.</summary>
/// <param name="Timestamp">UTC timestamp.</param>
/// <param name="Level">Severity.</param>
/// <param name="Source">Component or plugin that produced the record.</param>
/// <param name="Message">Formatted message.</param>
public readonly record struct LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Source, string Message);

/// <summary>Receives formatted log records.</summary>
public interface ILogSink
{
    void Emit(LogEntry entry);
}

/// <summary>Console sink with simple level coloring.</summary>
public sealed class ConsoleSink : ILogSink
{
    private static readonly Dictionary<LogLevel, ConsoleColor> Colors = new()
    {
        [LogLevel.Trace] = ConsoleColor.DarkGray,
        [LogLevel.Debug] = ConsoleColor.Gray,
        [LogLevel.Info] = ConsoleColor.White,
        [LogLevel.Warn] = ConsoleColor.Yellow,
        [LogLevel.Error] = ConsoleColor.Red,
        [LogLevel.Fatal] = ConsoleColor.DarkRed
    };

    public void Emit(LogEntry entry)
    {
        var color = Colors.TryGetValue(entry.Level, out var c) ? c : ConsoleColor.White;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{entry.Timestamp:HH:mm:ss} {entry.Level.ToString().ToUpperInvariant()[..4],4}] [{entry.Source}] {entry.Message}");
        Console.ResetColor();
    }
}

/// <summary>Sink that appends formatted records to a log file (used when booting inside a game process).</summary>
public sealed class FileSink(string path) : ILogSink, IDisposable
{
    private readonly StreamWriter _writer = new(path, append: true) { AutoFlush = true };
    private readonly Lock _lock = new();

    public void Emit(LogEntry entry)
    {
        lock (_lock)
        {
            _writer.WriteLine($"[{entry.Timestamp:HH:mm:ss.fff} {entry.Level.ToString().ToUpperInvariant()[..4],4}] [{entry.Source}] {entry.Message}");
        }
    }

    public void Dispose() => _writer.Dispose();
}
