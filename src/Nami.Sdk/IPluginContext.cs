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

    /// <summary>
    /// Built-in performance metrics for this plugin (tick timings and Tide-op latency).
    /// Always available; cheap to read.
    /// </summary>
    IModMetrics Profiler { get; }

    /// <summary>
    /// Requests a hot reload of this plugin (and any plugins that depend on it) at the
    /// next safe point. Returns true if the request was queued.
    /// </summary>
    bool RequestReload();

    /// <summary>
    /// This plugin's configuration section (<c>pluginConfig.&lt;pluginId&gt;</c> from
    /// <c>nami.json</c>). Always non-null; missing sections behave as empty.
    /// </summary>
    IPluginConfig Config { get; }
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
