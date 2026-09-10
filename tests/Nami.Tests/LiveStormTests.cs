using System.Reflection;
using System.Runtime.CompilerServices;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Generations;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Tests;

/// <summary>
/// Coordinator proofs: rapid reloads converge to the newest valid generation,
/// dependencies start forward and retire in reverse, rejected candidates keep
/// the last known-good generation serving, old load contexts stay collectible
/// across long reload chains, and the hostile fixture's slow shutdown parks and
/// then resolves instead of stranding the runtime.
/// </summary>
public class LiveStormTests
{
    private const string AlphaId = "dev.nami.fixtures.alpha";
    private const string BetaId = "dev.nami.fixtures.beta";
    private const string GammaId = "dev.nami.fixtures.gamma";
    private const string HostileId = "dev.nami.fixtures.hostile";

    private static void CopyPlugins(GameDirFixture fixture, params string[] pluginDlls)
    {
        Directory.CreateDirectory(fixture.ModsDir);
        var outputDir = AppContext.BaseDirectory;
        foreach (var pluginDll in pluginDlls)
        {
            File.Copy(Path.Combine(outputDir, pluginDll), Path.Combine(fixture.ModsDir, pluginDll));
        }

        File.Copy(Path.Combine(outputDir, "Nami.Sdk.dll"), Path.Combine(fixture.ModsDir, "Nami.Sdk.dll"));
    }

    private static Chainloader Create(GameDirFixture fixture)
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        return new Chainloader(fixture.Root, NamiConfig.Load(fixture.Root), hub);
    }

    [Fact]
    public void Storm_RapidReloads_ConvergeToNewestValid()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        // Gamma is the dependency leaf: reloading it touches only itself, so one
        // queued request is exactly one new generation (a D-only reload touches D).
        var before = chainloader.Plugins.Single(p => p.Manifest.Id == GammaId).Generation;

        for (var i = 0; i < 5; i++)
        {
            Assert.True(chainloader.RequestReload(GammaId));
        }

        // Queued, not applied: the drain thread owns the commit.
        Assert.Equal(before, chainloader.Plugins.Single(p => p.Manifest.Id == GammaId).Generation);

        chainloader.UpdateAll();

        var gamma = chainloader.Plugins.Single(p => p.Manifest.Id == GammaId);
        Assert.Equal(before + 5, gamma.Generation);
        Assert.Equal(5, gamma.ReloadCount);
        // Untouched siblings keep serving their pre-storm generations.
        Assert.Equal(LifetimeState.Running, chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId).Lifetime);
        Assert.All(chainloader.Plugins, p => Assert.Equal(LifetimeState.Running, p.Lifetime));
        chainloader.Shutdown();
    }

    [Fact]
    public void Storm_ConcurrentRequests_ConvergeWithoutLoss()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var before = chainloader.Plugins.Single(p => p.Manifest.Id == GammaId).Generation;

        const int threads = 4;
        const int perThread = 3;
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    Assert.True(chainloader.RequestReload(GammaId));
                }
            });
            workers[t].Start();
        }

        foreach (var worker in workers)
        {
            worker.Join(TimeSpan.FromSeconds(20));
            Assert.False(worker.IsAlive);
        }

        chainloader.UpdateAll();

        var gamma = chainloader.Plugins.Single(p => p.Manifest.Id == GammaId);
        Assert.Equal(before + (threads * perThread), gamma.Generation);
        Assert.All(chainloader.Plugins, p => Assert.Equal(LifetimeState.Running, p.Lifetime));

        // Dependency order survives the storm: alpha before beta before gamma.
        var ids = chainloader.Plugins.Select(p => p.Manifest.Id).ToArray();
        Assert.True(
            Array.IndexOf(ids, AlphaId) < Array.IndexOf(ids, BetaId) &&
            Array.IndexOf(ids, BetaId) < Array.IndexOf(ids, GammaId));
        chainloader.Shutdown();
    }

    [Fact]
    public void Dependencies_StartForward_RetireReverse_ShutdownCompletes()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();

        // Initialization order inside preparation stays dependency order A->B->C.
        var ids = chainloader.Plugins.Select(p => p.Manifest.Id).ToArray();
        Assert.True(
            Array.IndexOf(ids, AlphaId) < Array.IndexOf(ids, BetaId) &&
            Array.IndexOf(ids, BetaId) < Array.IndexOf(ids, GammaId));

        var result = chainloader.Reload(AlphaId);
        Assert.Equal(new[] { AlphaId, BetaId, GammaId }, result.Reloaded);

        // Retirement runs strictly post-publication in reverse dependency order C->B->A.
        var retired = LiveGates.RetirementLog(chainloader).Select(e => e.ModId).ToArray();
        Assert.Equal(new[] { GammaId, BetaId, AlphaId }, retired);

        chainloader.Shutdown();
        Assert.All(chainloader.Plugins, p => Assert.Equal(LifetimeState.UnloadRequested, p.Lifetime));

        // Shutdown retires in the same reverse dependency order (dependents first).
        var shutdownRetired = LiveGates.RetirementLog(chainloader).Select(e => e.ModId).Skip(3).ToArray();
        Assert.Equal(new[] { GammaId, BetaId, AlphaId }, shutdownRetired);
    }

    [Fact]
    public void Reload_UnknownId_FailsWithoutTouchingCurrent()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var before = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId).Generation;

        var result = chainloader.Reload("dev.nami.fixtures.does-not-exist");

        Assert.Empty(result.Reloaded);
        Assert.Empty(result.Unloaded);
        Assert.Equal(new[] { "dev.nami.fixtures.does-not-exist" }, result.Failed);

        // The failed candidate cannot destroy the last known-good generation.
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        Assert.Equal(before, alpha.Generation);
        Assert.Equal(LifetimeState.Running, alpha.Lifetime);
        chainloader.Shutdown();
    }

    [Fact]
    public void Leak_ReloadChain_OldContextsStayCollectible()
    {
        var iterations = Environment.GetEnvironmentVariable("NAMI_STRESS_500") == "1" ? 500 : 100;
        var tracked = BuildReloadWeaks(iterations);

        LiveGates.CollectUntilGone(tracked.Select(t => t.Weak).ToArray());

        var alive = tracked.Where(t => t.Weak.IsAlive).Select(t => t.Label).ToArray();
        Assert.True(
            alive.Length == 0,
            $"ALC leak: {alive.Length}/{tracked.Count} retired contexts survived collection: " +
            string.Join(", ", alive.Take(10)));
    }

    /// <summary>
    /// Runs the whole reload chain in a dead-on-return frame so no test local can
    /// root a retired generation across the collection loop. Returns weak handles
    /// plus string labels only (labels never root their context).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(WeakReference Weak, string Label)> BuildReloadWeaks(int iterations)
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll");

        var hub = new LogHub { MinimumLevel = LogLevel.Warn };
        var chainloader = new Chainloader(fixture.Root, NamiConfig.Load(fixture.Root), hub);
        chainloader.LoadAll();

        var tracked = new List<(WeakReference, string)>();
        for (var i = 0; i < iterations; i++)
        {
            var previous = chainloader.Plugins;
            var result = chainloader.Reload(AlphaId);
            if (result.Failed.Count != 0 || result.Reloaded.Count != 3)
            {
                throw new InvalidOperationException(
                    $"reload {i} did not converge: reloaded=[{string.Join(",", result.Reloaded)}] " +
                    $"failed=[{string.Join(",", result.Failed)}]");
            }

            foreach (var old in previous)
            {
                tracked.Add((LiveGates.AlcWeakOf(old), $"{old.Manifest.Id}#{old.Generation}"));
            }
        }

        chainloader.Shutdown();
        return tracked;
    }

    [Fact]
    public async Task Hostile_SlowShutdown_ParksAtBlocked_ThenResolves()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "HostilePlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var old = chainloader.Plugins.Single(p => p.Manifest.Id == HostileId);

        SetHostileStatic(old, "SlowUnload", true);
        SetHostileStatic(old, "SlowUnloadMs", 600);

        // Hold ordinary execution open: the quiescence drain cannot complete under it.
        var inFlight = LiveGates.EnterExecution(old);
        try
        {
            var commit = Task.Run(() => chainloader.Reload(HostileId));

            // Past the 5s drain budget the generation parks at RetirementBlocked (re-drive queued).
            Thread.Sleep(TimeSpan.FromSeconds(6));
            Assert.Equal(LifetimeState.RetirementBlocked, old.Lifetime);
            Assert.True(LiveGates.IsRetired(old));

            // The new generation already serves: publication never waits for the drain.
            var current = chainloader.Plugins.Single(p => p.Manifest.Id == HostileId);
            Assert.Equal(LifetimeState.Running, current.Lifetime);
            Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(old));

            inFlight.Dispose();
            await commit.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Empty((await commit).Failed);
        }
        finally
        {
            inFlight.Dispose();
        }

        // The re-drive observes the resolved drain and completes teardown (slow OnUnload included).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (old.Lifetime != LifetimeState.UnloadRequested && DateTime.UtcNow < deadline)
        {
            chainloader.UpdateAll();
            Thread.Sleep(25);
        }

        Assert.Equal(LifetimeState.UnloadRequested, old.Lifetime);
        Assert.Equal(1, (int)GetHostileStatic(old, "UnloadedCount")!);
        var fresh = chainloader.Plugins.Single(p => p.Manifest.Id == HostileId);
        Assert.Equal(LifetimeState.Running, fresh.Lifetime);
        chainloader.Shutdown();
    }

    [Fact]
    public void Hostile_OnValidateFalse_RejectsWithV1Intact()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "HostilePlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var before = chainloader.Plugins.Single(p => p.Manifest.Id == HostileId);

        // Env try/finally: the candidate reads NAMI_HOSTILE_ONVALIDATE inside its own ALC at
        // OnValidate time. Same-class placement keeps these env tests sequential (xunit runs
        // one class sequentially; no other class reads these variables).
        Environment.SetEnvironmentVariable("NAMI_HOSTILE_ONVALIDATE", "0");
        ReloadResult result;
        try
        {
            result = chainloader.Reload(HostileId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NAMI_HOSTILE_ONVALIDATE", null);
        }

        Assert.Contains(HostileId, result.Failed);
        Assert.Empty(result.Reloaded);

        // The failed candidate cannot destroy the last known-good generation.
        var current = chainloader.Plugins.Single(p => p.Manifest.Id == HostileId);
        Assert.Same(before, current);
        Assert.Equal(LifetimeState.Running, current.Lifetime);
        chainloader.Shutdown();
    }

    [Fact]
    public void Hostile_WatcherAddedThrowing_LeavesNoRecord()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var config = new NamiConfig { RootPath = fixture.Root };
        config.HotReload.DebounceMs = 150;
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        var chainloader = new Chainloader(fixture.Root, config, hub);
        chainloader.LoadAll();
        chainloader.StartHotReload();
        try
        {
            HotReloadEventArgs? rejected = null;
            chainloader.PluginReloaded += args =>
            {
                if (args.PluginId.Equals(HostileId, StringComparison.OrdinalIgnoreCase) && !args.Success)
                {
                    rejected = args;
                }
            };

            // The dropped DLL's OnLoad throws inside its own ALC (env-gated); the watcher path
            // must reject it with no generation record left behind.
            Environment.SetEnvironmentVariable("NAMI_HOSTILE_FAILONLOAD", "1");
            try
            {
                File.Copy(Path.Combine(AppContext.BaseDirectory, "HostilePlugin.dll"),
                    Path.Combine(fixture.ModsDir, "HostilePlugin.dll"));
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && rejected is null)
                {
                    chainloader.UpdateAll();
                    Thread.Sleep(50);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NAMI_HOSTILE_FAILONLOAD", null);
            }

            Assert.NotNull(rejected);
            Assert.DoesNotContain(chainloader.Plugins, p => p.Manifest.Id == HostileId);
            Assert.Equal(LifetimeState.Running, chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId).Lifetime);
        }
        finally
        {
            chainloader.StopHotReload();
        }

        chainloader.Shutdown();
    }
    // Hostile-toggle audit (F15): this is the only Storm/Resource test touching Hostile state,
    // and it already uses the in-ALC copy — generation.Instance.GetType() resolves the field on
    // the ALC-loaded type, so sets and reads hit the generation's own statics, never the
    // default-context copy a test-side type reference would reach. Behavior switches for new
    // tests go through NAMI_HOSTILE_* env vars (read inside the ALC); statics stay as
    // in-ALC observable counters only.
    private static void SetHostileStatic(ModGeneration generation, string name, object? value)
    {
        var field = generation.Instance.GetType().GetField(
            name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Hostile field '{name}' not found.");
        field.SetValue(null, value);
    }

    private static object? GetHostileStatic(ModGeneration generation, string name)
    {
        var field = generation.Instance.GetType().GetField(
            name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Hostile field '{name}' not found.");
        return field.GetValue(null);
    }
}
