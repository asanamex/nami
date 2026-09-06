namespace Nami.Sdk;

/// <summary>
/// Runtime services exposed to a loaded plugin through <see cref="NamiPlugin.Context"/>.
/// </summary>
public interface IPluginContext
{
    /// <summary>Metadata for the owning plugin.</summary>
    PluginInfo Info { get; }

    /// <summary>Mod logger; writes to the Nami log with this plugin's tag.</summary>
    ILog Log { get; }
}

/// <summary>Levels for <see cref="ILog"/>.</summary>
public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

/// <summary>Minimal logging surface used by plugins.</summary>
public interface ILog
{
    void Log(LogLevel level, string message);
    void Trace(string message) => Log(LogLevel.Trace, message);
    void Debug(string message) => Log(LogLevel.Debug, message);
    void Info(string message) => Log(LogLevel.Info, message);
    void Warn(string message) => Log(LogLevel.Warn, message);
    void Error(string message) => Log(LogLevel.Error, message);
    void Fatal(string message) => Log(LogLevel.Fatal, message);
}
