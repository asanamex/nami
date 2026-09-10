using System.Text.Json;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Generations;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Tests;

/// <summary>
/// Lifecycle, lease-gate, and retirement proofs for the live mod runtime:
/// every illegal <see cref="LifetimeState"/> edge throws, a retired generation
/// admits no new execution, quarantine is per generation, teardown never races
/// ordinary execution, and a barrier-held v1 callback survives its commit.
/// </summary>
public class LiveRuntimeTests
{
    private const string AlphaId = "dev.nami.fixtures.alpha";
    private const string BadId = "dev.nami.fixtures.bad";

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

    private static Chainloader Create(GameDirFixture fixture, NamiConfig? config = null)
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        return new Chainloader(fixture.Root, config ?? NamiConfig.Load(fixture.Root), hub);
    }

    [Fact]
    public void Lifetime_EveryIllegalTransition_ThrowsNamingBothEnds()
    {
        var forward = new (LifetimeState From, LifetimeState To)[]
        {
            (LifetimeState.Discovered, LifetimeState.Preparing),
            (LifetimeState.Preparing, LifetimeState.Loaded),
            (LifetimeState.Loaded, LifetimeState.Starting),
            (LifetimeState.Starting, LifetimeState.Running),
            (LifetimeState.Running, LifetimeState.Retiring),
            (LifetimeState.Retiring, LifetimeState.Quiescing),
            (LifetimeState.Quiescing, LifetimeState.Reclaiming),
            (LifetimeState.Reclaiming, LifetimeState.UnloadRequested),
            (LifetimeState.UnloadRequested, LifetimeState.Collected),
        };
        var legal = new HashSet<(LifetimeState, LifetimeState)>(forward)
        {
            (LifetimeState.Running, LifetimeState.Quarantined),
            (LifetimeState.Quiescing, LifetimeState.RetirementBlocked),
            (LifetimeState.RetirementBlocked, LifetimeState.Quiescing),
        };
        foreach (var state in Enum.GetValues<LifetimeState>())
        {
            if (state is not (LifetimeState.Collected or LifetimeState.Failed or LifetimeState.Quarantined))
            {
                legal.Add((state, LifetimeState.Failed));
            }
        }

        // 9 forward + Running->Quarantined + the Blocked pair + 10 non-terminal->Failed.
        Assert.Equal(22, legal.Count);

        var states = Enum.GetValues<LifetimeState>();
        foreach (var from in states)
        {
            foreach (var to in states)
            {
                if (legal.Contains((from, to)))
                {
                    Assert.Equal(to, LiveGates.TransitionTo(from, to));
                }
                else
                {
                    var ex = Assert.Throws<InvalidOperationException>(() => LiveGates.TransitionTo(from, to));
                    Assert.Contains(from.ToString(), ex.Message);
                    Assert.Contains(to.ToString(), ex.Message);
                }
            }
        }
    }

    [Fact]
    public void Lease_RetiredGeneration_RejectsNewExecution()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        using (LiveGates.EnterExecution(alpha))
        {
            Assert.Equal(1, LiveGates.ActiveExecutions(alpha));
        }

        Assert.Equal(0, LiveGates.ActiveExecutions(alpha));

        using var inFlight = LiveGates.EnterExecution(alpha);
        var outstanding = LiveGates.RetireGate(alpha);
        Assert.Equal(1, outstanding);
        Assert.True(LiveGates.IsRetired(alpha));

        // The gate is linearizable: retirement linearized first, so entry now fails.
        var rejected = Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(alpha));
        Assert.Equal(AlphaId, rejected.ModId);
        Assert.Equal(alpha.Generation, rejected.GenerationId);

        // The drain observes the held lease exactly once.
        Assert.Equal(1, LiveGates.ActiveExecutions(alpha));
        inFlight.Dispose();
        Assert.Equal(0, LiveGates.ActiveExecutions(alpha));

        // Retirement stays rejected after the drain: never entry-after-retire.
        Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(alpha));
        chainloader.Shutdown();
    }

    [Fact]
    public void Quarantine_BadCandidate_SiblingGenerationUnaffected()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig(quarantineEnabled: true, threshold: 2);
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll", "BadPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();

        ModGeneration? quarantined = null;
        chainloader.PluginQuarantined += generation => quarantined = generation;

        var bad = chainloader.Plugins.Single(p => p.Manifest.Id == BadId);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (bad.Lifetime != LifetimeState.Quarantined && DateTime.UtcNow < deadline)
        {
            chainloader.UpdateAll();
        }

        Assert.Equal(LifetimeState.Quarantined, bad.Lifetime);
        Assert.NotNull(bad.QuarantinedAt);
        Assert.True(bad.ConsecutiveFailures >= 2);
        Assert.Same(bad, quarantined);

        // Quarantine is per (modId, generation): siblings keep serving.
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        Assert.Equal(LifetimeState.Running, alpha.Lifetime);
        using (LiveGates.EnterExecution(alpha))
        {
        }

        // The quarantined generation admits no new ordinary execution.
        Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(bad));

        // The game keeps running after the bad generation is disabled.
        chainloader.UpdateAll();
        Assert.Equal(LifetimeState.Running, chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId).Lifetime);
        chainloader.Shutdown();
    }

    [Fact]
    public void TeardownGate_RefusesEarlyEntry_AndLeaseHeldEntry()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        // Teardown is its own domain: Running is never a teardown state.
        var early = Assert.Throws<InvalidOperationException>(() => LiveGates.EnterTeardown(alpha));
        Assert.Contains("Quiescing", early.Message);

        // Even at quiescence, a held ordinary lease refuses teardown.
        using var lease = LiveGates.EnterExecution(alpha);
        LiveGates.SetLifetimeDirect(alpha, LifetimeState.Quiescing);
        try
        {
            var held = Assert.Throws<InvalidOperationException>(() => LiveGates.EnterTeardown(alpha));
            Assert.Contains("lease", held.Message);
        }
        finally
        {
            lease.Dispose();
        }

        // Drained and quiesced: teardown entry succeeds, then the record is restored.
        LiveGates.EnterTeardown(alpha);
        LiveGates.SetLifetimeDirect(alpha, LifetimeState.Running);
        Assert.Equal(LifetimeState.Running, alpha.Lifetime);
        chainloader.Shutdown();
    }

    [Fact]
    public void RetireVsEnter_RaceStress_NeverEntryAfterRetire_NeverCountLeak()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        const int threads = 8;
        const int iterations = 500;
        var acquired = 0;
        var released = 0;
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    try
                    {
                        using (LiveGates.EnterExecution(alpha))
                        {
                            Interlocked.Increment(ref acquired);
                        }

                        Interlocked.Increment(ref released);
                    }
                    catch (GenerationRetiredException)
                    {
                        return;
                    }
                }
            });
            workers[t].Start();
        }

        while (Volatile.Read(ref acquired) < 200)
        {
            Thread.Sleep(1);
        }

        LiveGates.RetireGate(alpha);
        foreach (var worker in workers)
        {
            worker.Join(TimeSpan.FromSeconds(20));
            Assert.False(worker.IsAlive);
        }

        // Every granted lease was released exactly once: the quiescence drain sees zero.
        Assert.Equal(acquired, released);
        Assert.Equal(0, LiveGates.ActiveExecutions(alpha));
        Assert.True(LiveGates.IsRetired(alpha));

        // After retirement linearized, entry is uniformly rejected.
        for (var i = 0; i < 100; i++)
        {
            Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(alpha));
        }

        chainloader.Shutdown();
    }

    [Fact]
    public void QuarantineTeardown_HeldLease_DefersUntilDrained()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig(quarantineEnabled: true, threshold: 1);
        CopyPlugins(fixture, "BadPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var bad = chainloader.Plugins.Single(p => p.Manifest.Id == BadId);

        // Hold ordinary execution open across the quarantine: the quarantine teardown gate
        // (explicit zero-lease assertion, not EnterTeardown — Quarantined is terminal) must
        // defer OnUnload until the drain, never run under the held lease.
        var held = LiveGates.EnterExecution(bad);
        try
        {
            chainloader.UpdateAll(); // Bad.OnUpdate throws once → quarantined (threshold 1).
            Assert.Equal(LifetimeState.Quarantined, bad.Lifetime);
            Assert.True(LiveGates.IsRetired(bad));

            chainloader.UpdateAll(); // Drain runs QuarantineTeardown; the 5s budget expires held.
            Assert.Equal(LifetimeState.Quarantined, bad.Lifetime);

            // Deferred, not torn down: still retiring with the held lease as the blocker.
            var blockers = StatusRetiringBlockers(fixture, BadId);
            Assert.Contains(blockers, b => b.Contains("active execution"));
        }
        finally
        {
            held.Dispose();
        }

        // Released: the re-drive completes teardown with the record staying Quarantined
        // (no new lifetime edge), reported under its actual lifetime.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            chainloader.UpdateAll();
            if (StatusRetiringBlockers(fixture, BadId).Count == 0)
            {
                break;
            }

            Thread.Sleep(100);
        }

        Assert.Equal(LifetimeState.Quarantined, bad.Lifetime);
        Assert.Empty(StatusRetiringBlockers(fixture, BadId));
        Assert.Equal("Quarantined", StatusRetiringLifetime(fixture, BadId));
        Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(bad));
        chainloader.Shutdown();
    }

    /// <summary>Blockers listed for a mod in status.json's retiring array (empty when absent).</summary>
    private static List<string> StatusRetiringBlockers(GameDirFixture fixture, string modId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "status.json")));
        foreach (var row in document.RootElement.GetProperty("retiring").EnumerateArray())
        {
            if (row.GetProperty("modId").GetString() == modId)
            {
                return row.GetProperty("blockers").EnumerateArray()
                    .Select(b => b.GetString() ?? string.Empty).ToList();
            }
        }

        return new List<string>();
    }

    /// <summary>Lifetime listed for a mod in status.json's retiring array (null when absent).</summary>
    private static string? StatusRetiringLifetime(GameDirFixture fixture, string modId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "status.json")));
        foreach (var row in document.RootElement.GetProperty("retiring").EnumerateArray())
        {
            if (row.GetProperty("modId").GetString() == modId)
            {
                return row.GetProperty("lifetime").GetString();
            }
        }

        return null;
    }

    [Fact]
    public void OnUnload_RunsExactlyOnce()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        var warnings = new List<string>();
        LiveGates.RunOnUnloadOnce(alpha, warnings.Add);
        Assert.Equal(1, LiveGates.OnUnloadRan(alpha));
        LiveGates.RunOnUnloadOnce(alpha, warnings.Add);
        Assert.Equal(1, LiveGates.OnUnloadRan(alpha));
        chainloader.Shutdown();
    }

    [Fact]
    public async Task InFlight_BarrierHeldV1Callback_FinishesAcrossCommit_ReclaimsAfter()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var old = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        var oldNumber = old.Generation;

        // One v1 call stays open across the commit: the generation cannot drain under it.
        var inFlight = LiveGates.EnterExecution(old);
        ReloadResult result;
        try
        {
            var commit = Task.Run(() => chainloader.Reload(AlphaId));

            // Wait until retirement has linearized (bounded: preparation is the only variable).
            var retiredDeadline = DateTime.UtcNow.AddSeconds(20);
            while (!LiveGates.IsRetired(old) && DateTime.UtcNow < retiredDeadline)
            {
                Thread.Sleep(25);
            }

            Assert.True(LiveGates.IsRetired(old));

            // The swap already published: the new generation serves while v1 still drains.
            var current = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
            Assert.True(current.Generation > oldNumber);
            Assert.Equal(LifetimeState.Running, current.Lifetime);

            // Zero new v1 calls: the retired generation rejects entry while held open.
            Assert.Equal(1, LiveGates.ActiveExecutions(old));
            Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(old));

            // v1 finishes only after the commit: release lets the drain observe zero.
            inFlight.Dispose();

            await commit.WaitAsync(TimeSpan.FromSeconds(30));
            result = await commit;
        }
        finally
        {
            inFlight.Dispose();
        }

        Assert.Equal(0, LiveGates.ActiveExecutions(old));
        Assert.Empty(result.Failed);
        Assert.Contains(AlphaId, result.Reloaded);

        // The commit drained without parking (released well inside the budget) and retired v1.
        var pumpDeadline = DateTime.UtcNow.AddSeconds(20);
        while (old.Lifetime is LifetimeState.Retiring or LifetimeState.Quiescing &&
               DateTime.UtcNow < pumpDeadline)
        {
            chainloader.UpdateAll();
            Thread.Sleep(25);
        }

        Assert.Equal(LifetimeState.UnloadRequested, old.Lifetime);
        var fresh = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        Assert.Equal(LifetimeState.Running, fresh.Lifetime);
        chainloader.Shutdown();
    }
}
