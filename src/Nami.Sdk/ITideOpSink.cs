namespace Nami.Sdk;

/// <summary>
/// Receives Tide (game-bridge) operation timings. Implemented by the chainloader's per-mod
/// profiler; Tide calls it once per completed op.
/// </summary>
public interface ITideOpSink
{
    /// <summary>Records one completed Tide operation's latency in milliseconds.</summary>
    void RecordTideOp(double milliseconds);
}

/// <summary>
/// Global hookup between the Tide bridge and the profiler subsystem. Nami.Tide has no
/// dependency on the chainloader, so timing flows through this indirection: the chainloader
/// registers one sink, and every completed Tide op reports its latency through it.
/// Ops fired while a plugin's <c>OnUpdate</c> runs are attributed to that plugin's profiler.
/// </summary>
public static class TideMetrics
{
    private static ITideOpSink? _sink;

    /// <summary>True when a sink is registered (cheap guard for the timing hot path).</summary>
    public static bool HasSink => Volatile.Read(ref _sink) is not null;

    /// <summary>Registers (or replaces) the process-wide Tide timing sink. Thread-safe.</summary>
    public static void Register(ITideOpSink? sink) => Volatile.Write(ref _sink, sink);

    /// <summary>Records an op latency through the registered sink, if any. Never throws.</summary>
    public static void Record(double milliseconds)
    {
        var sink = Volatile.Read(ref _sink);
        try
        {
            sink?.RecordTideOp(milliseconds);
        }
        catch
        {
            // A broken metrics sink must never break a game call.
        }
    }

    /// <summary>Removes the sink, but only if it is still the registered one. Thread-safe.</summary>
    public static void Unregister(ITideOpSink? sink)
    {
        if (sink is not null && ReferenceEquals(Volatile.Read(ref _sink), sink))
        {
            Volatile.Write(ref _sink, null);
        }
    }
}
