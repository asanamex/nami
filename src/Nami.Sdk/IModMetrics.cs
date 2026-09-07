namespace Nami.Sdk;

/// <summary>
/// Read-only performance snapshot for a plugin, collected by Nami's built-in profiler.
/// Values are updated by the chainloader each update tick; the snapshot is cheap and
/// thread-safe to read from plugin code (e.g. to render an FPS/ms overlay).
/// </summary>
public interface IModMetrics
{
    /// <summary>Total number of OnUpdate ticks recorded.</summary>
    long TickCount { get; }

    /// <summary>Most recent OnUpdate duration, in milliseconds.</summary>
    double LastMs { get; }

    /// <summary>Mean OnUpdate duration over the recorded window, in milliseconds.</summary>
    double AvgMs { get; }

    /// <summary>95th-percentile OnUpdate duration, in milliseconds (0 until enough samples).</summary>
    double P95Ms { get; }

    /// <summary>Worst OnUpdate duration seen in the recorded window, in milliseconds.</summary>
    double MaxMs { get; }

    /// <summary>Number of Tide (game-bridge) operations this plugin has completed.</summary>
    long TideOpCount { get; }

    /// <summary>Mean Tide operation latency, in milliseconds.</summary>
    double TideOpAvgMs { get; }

    /// <summary>Formats the metrics as a single human-readable line.</summary>
    string ToSummaryLine();
}
