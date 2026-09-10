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

    /// <summary>
    /// Generation-scoped Wave hook surface for this plugin (see <see cref="IGenerationHooks"/>).
    /// Kept flat beside <see cref="Info"/>, <see cref="Log"/>, <see cref="Profiler"/>,
    /// <see cref="RequestReload"/> and <see cref="Config"/> by convention.
    /// </summary>
    IGenerationHooks Hooks { get; }

    /// <summary>Host-owned persisted JSON state for this mod (survives generation replacement).</summary>
    IModState State { get; }

    /// <summary>
    /// Lifetime token for the CALLING generation: canceled when its retirement starts. These
    /// members bind the calling generation's resources (not the mod id across reloads) — hand
    /// the token to cooperative work in <c>OnLoad</c>/<c>OnUpdate</c> and track everything the
    /// generation owns below so retirement releases it deterministically. Reading this during
    /// teardown throws <see cref="InvalidOperationException"/> naming teardown: capture it early.
    /// </summary>
    CancellationToken Lifetime { get; }

    /// <summary>
    /// Tracks a background task against the CALLING generation so retirement waits for it (up
    /// to budget) and observes its outcome. Tracking never cancels the task: combine its body
    /// with <see cref="Lifetime"/> for cooperative shutdown. Throws
    /// <see cref="InvalidOperationException"/> naming teardown when the calling generation is
    /// retiring (accepted work would outlive it).
    /// </summary>
    void Track(Task task, string? name = null);

    /// <summary>
    /// Registers a cleanup callback against the CALLING generation (runs on its retirement path,
    /// exactly once). Throws <see cref="InvalidOperationException"/> naming teardown when the
    /// calling generation is retiring.
    /// </summary>
    void OnCleanup(Func<ValueTask> cleanup, string? name = null);

    /// <summary>
    /// Takes ownership of a disposable (e.g. a <c>Timer</c>) for the CALLING generation; disposed
    /// on its retirement path. Throws <see cref="InvalidOperationException"/> naming teardown
    /// when the calling generation is retiring.
    /// </summary>
    void Own(IDisposable disposable);

    /// <summary>
    /// Takes ownership of an async disposable for the CALLING generation; disposed on its
    /// retirement path. Throws <see cref="InvalidOperationException"/> naming teardown when the
    /// calling generation is retiring.
    /// </summary>
    void Own(IAsyncDisposable disposable);
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
