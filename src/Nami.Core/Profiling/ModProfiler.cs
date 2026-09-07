using Nami.Sdk;

namespace Nami.Core.Profiling;

/// <summary>
/// Lightweight per-plugin metrics collector. Timings are bucketed into a fixed log-scale
/// histogram (O(1), allocation-free on the hot path) so p50/p95/max can be reported without
/// storing every sample. All members are thread-safe; reads take a lock-free snapshot.
/// </summary>
public sealed class ModProfiler : IModMetrics, ITideOpSink
{
    // Log-scale buckets: 0..255 covering ~1 µs .. ~10 s (bucket b covers roughly
    // 2^(b/16) µs). Good enough for tick timings (sub-ms .. seconds).
    private const int Buckets = 256;
    private const double Base = 1.06;   // growth per bucket: ~6% steps
    private const double StartUs = 1.0; // bucket 0 starts at ~1 µs

    private readonly long[] _buckets = new long[Buckets];
    private readonly Lock _tideLock = new();
    private long _tickCount;
    private long _tideOpCount;
    private double _tideTotalMs;
    private double _lastMs;

    private static int BucketOf(double ms)
    {
        if (ms <= 0)
        {
            return 0;
        }

        var us = ms * 1000.0;
        var b = (int)(Math.Log(us / StartUs) / Math.Log(Base));
        return Math.Clamp(b, 0, Buckets - 1);
    }

    private static double MsOfBucket(int b) => (StartUs * Math.Pow(Base, b)) / 1000.0;

    /// <summary>Records one OnUpdate duration (milliseconds).</summary>
    public void RecordTick(double ms)
    {
        Interlocked.Increment(ref _tickCount);
        Interlocked.Increment(ref _buckets[BucketOf(ms)]);
        Volatile.Write(ref _lastMs, ms);
    }

    /// <summary>Records one Tide (game-bridge) operation duration (milliseconds).</summary>
    public void RecordTideOp(double ms)
    {
        lock (_tideLock)
        {
            _tideOpCount++;
            _tideTotalMs += ms;
        }
    }

    /// <summary>True once at least one tick has been recorded.</summary>
    public bool HasSamples => Volatile.Read(ref _tickCount) > 0;

    private int PercentileBucket(double p)
    {
        var total = Volatile.Read(ref _tickCount);
        if (total == 0)
        {
            return 0;
        }

        var target = (long)(total * p);
        long running = 0;
        for (var b = 0; b < Buckets; b++)
        {
            running += Volatile.Read(ref _buckets[b]);
            if (running >= target)
            {
                return b;
            }
        }

        return Buckets - 1;
    }

    public long TickCount => Volatile.Read(ref _tickCount);
    public double LastMs => Volatile.Read(ref _lastMs);
    public double AvgMs
    {
        get
        {
            var total = Volatile.Read(ref _tickCount);
            if (total == 0)
            {
                return 0;
            }

            double sum = 0;
            for (var b = 0; b < Buckets; b++)
            {
                sum += Volatile.Read(ref _buckets[b]) * MsOfBucket(b);
            }

            return sum / total;
        }
    }

    public double P95Ms => MsOfBucket(PercentileBucket(0.95));

    public double MaxMs
    {
        get
        {
            for (var b = Buckets - 1; b >= 0; b--)
            {
                if (Volatile.Read(ref _buckets[b]) > 0)
                {
                    return MsOfBucket(b);
                }
            }

            return 0;
        }
    }

    public long TideOpCount
    {
        get { lock (_tideLock) { return _tideOpCount; } }
    }

    public double TideOpAvgMs
    {
        get
        {
            lock (_tideLock)
            {
                return _tideOpCount == 0 ? 0 : _tideTotalMs / _tideOpCount;
            }
        }
    }

    public string ToSummaryLine()
    {
        var ticks = TickCount;
        var line = ticks == 0
            ? "no ticks recorded"
            : $"ticks={ticks} last={LastMs,6:F3}ms avg={AvgMs,6:F3}ms p95={P95Ms,6:F3}ms max={MaxMs,6:F3}ms";

        return TideOpCount > 0
            ? line + $" | tide ops={TideOpCount} avg={TideOpAvgMs,5:F2}ms"
            : line;
    }
}
