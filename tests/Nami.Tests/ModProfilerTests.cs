using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Core.Profiling;
using Nami.Sdk;

namespace Nami.Tests;

public class ModProfilerTests
{
    [Fact]
    public void Profiler_StartsEmpty()
    {
        var profiler = new ModProfiler();

        Assert.False(profiler.HasSamples);
        Assert.Equal(0, profiler.TickCount);
        Assert.Equal(0, profiler.LastMs);
        Assert.Equal(0, profiler.TideOpCount);
        Assert.Equal("no ticks recorded", profiler.ToSummaryLine());
    }

    [Fact]
    public void Profiler_RecordsTickStatistics()
    {
        var profiler = new ModProfiler();

        // A single repeated value keeps the log-scale bucket rounding deterministic.
        for (var i = 0; i < 10; i++)
        {
            profiler.RecordTick(5.0);
        }

        Assert.True(profiler.HasSamples);
        Assert.Equal(10, profiler.TickCount);
        Assert.Equal(5.0, profiler.LastMs);
        Assert.InRange(profiler.AvgMs, 4.0, 5.5);
        Assert.InRange(profiler.P95Ms, 4.0, 5.5);
        Assert.InRange(profiler.MaxMs, 4.0, 5.5);

        // A slow tick must move the max to the worst sample's bucket.
        profiler.RecordTick(50.0);
        Assert.Equal(50.0, profiler.LastMs);
        Assert.InRange(profiler.MaxMs, 40.0, 55.0);
        Assert.Contains("ticks=11", profiler.ToSummaryLine());
    }

    [Fact]
    public void Profiler_TracksTideOpLatency()
    {
        var profiler = new ModProfiler();

        profiler.RecordTideOp(2.0);
        profiler.RecordTideOp(4.0);

        Assert.Equal(2, profiler.TideOpCount);
        Assert.Equal(3.0, profiler.TideOpAvgMs, 5);
        Assert.Contains("tide ops=2", profiler.ToSummaryLine());
    }

    private sealed class CaptureSink : ILogSink
    {
        public List<LogEntry> Entries { get; } = new();

        public void Emit(LogEntry entry)
        {
            lock (Entries)
            {
                Entries.Add(entry);
            }
        }
    }

    [Fact]
    public void UpdateAll_RecordsTickMetricsAndLogsSummaries()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        Directory.CreateDirectory(fixture.ModsDir);
        var outputDir = AppContext.BaseDirectory;
        foreach (var dll in new[] { "AlphaPlugin.dll", "BetaPlugin.dll", "Nami.Sdk.dll" })
        {
            File.Copy(Path.Combine(outputDir, dll), Path.Combine(fixture.ModsDir, dll));
        }

        var config = new NamiConfig { RootPath = fixture.Root };
        config.Profiler.SummaryIntervalSeconds = 0.001; // summarize on every tick

        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        var sink = new CaptureSink();
        hub.AddSink(sink);

        var chainloader = new Chainloader(fixture.Root, config, hub);
        chainloader.LoadAll();
        chainloader.UpdateAll();
        chainloader.UpdateAll();

        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
        Assert.True(alpha.Profiler.HasSamples);
        Assert.True(alpha.Profiler.TickCount >= 2);
        Assert.Contains(sink.Entries, e =>
            e.Source == "profiler" &&
            e.Message.Contains("dev.nami.fixtures.alpha") &&
            e.Message.Contains("ticks="));
        chainloader.Shutdown();
    }

    [Fact]
    public void UpdateAll_SkipsRecordingWhenProfilerDisabled()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        Directory.CreateDirectory(fixture.ModsDir);
        var outputDir = AppContext.BaseDirectory;
        foreach (var dll in new[] { "AlphaPlugin.dll", "Nami.Sdk.dll" })
        {
            File.Copy(Path.Combine(outputDir, dll), Path.Combine(fixture.ModsDir, dll));
        }

        var config = new NamiConfig { RootPath = fixture.Root };
        config.Profiler.Enabled = false;

        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        var chainloader = new Chainloader(fixture.Root, config, hub);
        chainloader.LoadAll();
        chainloader.UpdateAll();
        chainloader.UpdateAll();

        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
        Assert.False(alpha.Profiler.HasSamples);
        chainloader.Shutdown();
    }
}
