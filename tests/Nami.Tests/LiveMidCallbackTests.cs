using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Generations;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Tests;

/// <summary>
/// Mid-callback reload proofs: concurrent dispatch fire across real commits never enters a
/// retired generation, transpiler old bodies stay valid at the safe point while the new
/// snapshot routes to the new body, and direct (non-slot) registrations keep their
/// documented teardown-at-retire carve-out.
/// Nami.Tests stays Wave-free: slots here are synthetic host-owned <see cref="DispatchSlot"/>
/// values resolved with the product's <see cref="GenerationDispatcher.ResolveCurrent"/> plus
/// the product's lease gate — the exact halves of the fused <c>AcquireCurrentGeneration</c>
/// path (one snapshot read, then a lease through the same atomic gate retirement uses) —
/// while every commit is a real <see cref="Chainloader"/> reload. Real Wave dispatch under
/// fire is proven in Nami.Wave.Tests (<c>WaveRebuildSeamTests</c>). Callbacks here are
/// host-side <see cref="Action"/>s so the gate proof is isolated from ALC rooting
/// (reclamation itself is proven by the existing leak-chain test); every granted lease still
/// orders against the real retire gate on real generations.
/// </summary>
public class LiveMidCallbackTests
{
    private const string AlphaId = "dev.nami.fixtures.alpha";

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

    /// <summary>Single-pointer publication box: volatile ref swap, mirroring the snapshot root.</summary>
    private sealed class SnapBox
    {
        public volatile object? Current;
    }

    /// <summary>
    /// Cross-thread dispatch storm across real commits with a barrier-held v1 callback, so the
    /// first retire must wait for the drain while dispatchers keep firing.
    /// Kill-case: if dispatch could route to retired code, a dispatcher would invoke a callback
    /// whose generation lost the retire race (the lease would have thrown instead) or the final
    /// lease balance would leak.
    /// </summary>
    [Fact]
    public async Task DispatchStorm_AcrossRealCommits_NeverEntersRetiredGeneration()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var boot = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        var bootNumber = boot.Generation;
        var bootVersion = LiveGates.SnapshotVersion(LiveGates.CurrentSnapshot(chainloader));

        const string slotKey = "storm";
        var owner = GenerationDispatcher.SlotOwnerFor(AlphaId, slotKey);
        var probe = new DispatchSlot(AlphaId, slotKey, PatchKind.CallbackObserver, new Action(() => { }), bootNumber);

        var bodies = new ConcurrentDictionary<int, Delegate>();
        var liveGens = new ConcurrentDictionary<int, byte>();
        var invokedGens = new ConcurrentDictionary<int, byte>();
        var fatalErrors = new ConcurrentQueue<Exception>();
        var box = new SnapBox();
        long snapVersion = 0;
        long acquired = 0, released = 0, invocations = 0, rejections = 0, mismatches = 0;
        var stop = false;

        void Publish(ModGeneration generation)
        {
            // Registered before the snapshot publish, so any resolvable generation has its body.
            Action body = () => { Interlocked.Increment(ref invocations); };
            bodies[generation.Generation] = body;
            liveGens[generation.Generation] = 0;
            snapVersion++;
            box.Current = LiveGates.BuildSnapshot(
                snapVersion,
                new Dictionary<string, ModGeneration>(StringComparer.OrdinalIgnoreCase) { [AlphaId] = generation },
                new Dictionary<string, DispatchSlot>(StringComparer.Ordinal)
                {
                    [owner] = new DispatchSlot(AlphaId, slotKey, PatchKind.CallbackObserver, body, generation.Generation),
                });
        }

        void Step()
        {
            // One snapshot read per dispatch: resolve, then lease the same generation through the
            // retire gate. A retire racing this step either lets the lease through (the drain will
            // observe it) or rejects it before entry — never reclaims under it.
            var snap = box.Current!;
            Delegate callback;
            int genNumber;
            try
            {
                (callback, genNumber) = LiveGates.ResolveCurrent(probe, snap);
            }
            catch (GenerationRetiredException)
            {
                Interlocked.Increment(ref rejections);
                return;
            }

            var gens = LiveGates.SnapshotGenerations(snap);
            if (!gens.TryGetValue(AlphaId, out var generation) || generation.Generation != genNumber)
            {
                Interlocked.Increment(ref mismatches);
                return;
            }

            if (!bodies.TryGetValue(genNumber, out var registered) || !ReferenceEquals(callback, registered))
            {
                Interlocked.Increment(ref mismatches);
                return;
            }

            try
            {
                using (LiveGates.EnterExecution(generation))
                {
                    Interlocked.Increment(ref acquired);
                    invokedGens.TryAdd(genNumber, 0);
                    ((Action)callback)();
                }

                Interlocked.Increment(ref released);
            }
            catch (GenerationRetiredException)
            {
                Interlocked.Increment(ref rejections);
            }
        }

        void Worker()
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    Step();
                }
                catch (Exception ex)
                {
                    fatalErrors.Enqueue(ex);
                    return;
                }
            }
        }

        Publish(boot);

        const int dispatchers = 4;
        var workers = new Thread[dispatchers];
        for (var t = 0; t < workers.Length; t++)
        {
            workers[t] = new Thread(Worker);
            workers[t].Start();
        }

        const int extraCommits = 3;
        var totalCommits = 1 + extraCommits;
        var allGens = new List<ModGeneration> { boot };
        try
        {
            // The storm is live before the first commit starts hammering the retire gate.
            var stormDeadline = DateTime.UtcNow.AddSeconds(10);
            while (Interlocked.Read(ref acquired) < 100 && DateTime.UtcNow < stormDeadline)
            {
                Thread.Sleep(5);
            }

            Assert.True(Interlocked.Read(ref acquired) >= 100);

            // Barrier-held v1 callback across the first commit: retirement must wait for the drain
            // while dispatchers keep firing, and the swap still publishes underneath.
            var barrierHold = LiveGates.EnterExecution(boot);
            var commit = Task.Run(() => chainloader.Reload(AlphaId));
            try
            {
                var retiredDeadline = DateTime.UtcNow.AddSeconds(20);
                while (!LiveGates.IsRetired(boot) && DateTime.UtcNow < retiredDeadline)
                {
                    Thread.Sleep(25);
                }

                Assert.True(LiveGates.IsRetired(boot));

                var first = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
                Assert.True(first.Generation > bootNumber);
                Assert.Equal(LifetimeState.Running, first.Lifetime);
                Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(boot));

                Publish(first);
                allGens.Add(first);
            }
            finally
            {
                barrierHold.Dispose();
            }

            var firstResult = await commit.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Empty(firstResult.Failed);
            Assert.Contains(AlphaId, firstResult.Reloaded);

            for (var i = 0; i < extraCommits; i++)
            {
                var result = chainloader.Reload(AlphaId);
                Assert.Empty(result.Failed);
                var current = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
                Publish(current);
                allGens.Add(current);
            }

            // Every published generation must serve at least once before the storm stops.
            var finalGen = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId).Generation;
            var servedDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!invokedGens.ContainsKey(finalGen) && DateTime.UtcNow < servedDeadline)
            {
                Thread.Sleep(10);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            foreach (var worker in workers)
            {
                worker.Join(TimeSpan.FromSeconds(20));
                Assert.False(worker.IsAlive);
            }
        }

        // No worker ever saw anything unexpected: no torn resolution, no foreign callback.
        Assert.Empty(fatalErrors);
        Assert.Equal(0, Interlocked.Read(ref mismatches));

        // Exactly-once lease balance: every granted lease was released exactly once, and every
        // granted lease ran its callback exactly once (invoke-under-lease pairing, no skips).
        Assert.Equal(Interlocked.Read(ref acquired), Interlocked.Read(ref released));
        Assert.Equal(Interlocked.Read(ref acquired), Interlocked.Read(ref invocations));

        // Every invoked generation was live at invoke time (registered before its publish), and
        // every live generation served at least once — never a retired-and-reclaimed outsider.
        foreach (var gen in liveGens.Keys)
        {
            Assert.True(invokedGens.ContainsKey(gen), $"generation {gen} was published but never served");
        }

        Assert.Empty(invokedGens.Keys.Except(liveGens.Keys));

        // Each real commit publishes exactly one snapshot and mints exactly one generation.
        var endVersion = LiveGates.SnapshotVersion(LiveGates.CurrentSnapshot(chainloader));
        Assert.Equal(bootVersion + totalCommits, endVersion);
        var end = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        Assert.Equal(bootNumber + totalCommits, end.Generation);

        // After retirement linearized, entry is uniformly rejected and nothing is held.
        foreach (var gen in allGens.Where(g => g.Generation != end.Generation))
        {
            Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(gen));
            Assert.Equal(0, LiveGates.ActiveExecutions(gen));
        }

        Assert.Equal(0, LiveGates.ActiveExecutions(end));

        // Settle teardowns, then every superseded generation must have drained and unloaded.
        var pumpDeadline = DateTime.UtcNow.AddSeconds(20);
        while (allGens.Any(g => g.Lifetime is LifetimeState.Retiring or LifetimeState.Quiescing or LifetimeState.RetirementBlocked) &&
               DateTime.UtcNow < pumpDeadline)
        {
            chainloader.UpdateAll();
            Thread.Sleep(25);
        }

        foreach (var gen in allGens.Where(g => g.Generation != end.Generation))
        {
            Assert.Equal(LifetimeState.UnloadRequested, gen.Lifetime);
        }

        Assert.Equal(LifetimeState.Running, end.Lifetime);
        chainloader.Shutdown();
    }

    /// <summary>
    /// Transpiler old-body validity at the data layer: the old generated body stays resolvable
    /// and executable from the held pre-commit snapshot while the new snapshot routes to the new
    /// body, with the commit swapping routing exactly once (transpilers rebuild, never swap live).
    /// Kill-case: if a commit live-swapped transpiler callbacks, the held old snapshot would
    /// resolve the new body (or nothing) instead of the still-valid old artifact.
    /// </summary>
    [Fact]
    public void TranspilerOldBody_StaysValidAcrossCommit_NewSnapshotRoutesToNewBody()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var old = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        // Transpiler-bearing slots never participate in callback swaps, however they are wired.
        Assert.Equal(
            ReloadCapability.RequiresPatchRebuild,
            GenerationPatchClassifier.Classify(PatchKind.Transpiler, isIl2CppBackend: false, BindingMechanism.TrampolineDispatch));
        Assert.Equal(
            ReloadCapability.RequiresPatchRebuild,
            GenerationPatchClassifier.Classify(PatchKind.Transpiler, isIl2CppBackend: false, BindingMechanism.BakedIntoGeneratedBody));

        const string slotKey = "il-body";
        var owner = GenerationDispatcher.SlotOwnerFor(AlphaId, slotKey);
        var oldRuns = 0;
        Action oldBody = () => { Interlocked.Increment(ref oldRuns); };
        var oldSlot = new DispatchSlot(AlphaId, slotKey, PatchKind.Transpiler, oldBody, old.Generation);
        var oldSnap = LiveGates.BuildSnapshot(
            1,
            new Dictionary<string, ModGeneration>(StringComparer.OrdinalIgnoreCase) { [AlphaId] = old },
            new Dictionary<string, DispatchSlot>(StringComparer.Ordinal) { [owner] = oldSlot });

        var resolvedOld = LiveGates.ResolveCurrent(oldSlot, oldSnap);
        Assert.Same(oldBody, resolvedOld.Callback);

        var versionBefore = LiveGates.SnapshotVersion(LiveGates.CurrentSnapshot(chainloader));
        var result = chainloader.Reload(AlphaId);
        Assert.Empty(result.Failed);
        var versionAfter = LiveGates.SnapshotVersion(LiveGates.CurrentSnapshot(chainloader));
        Assert.Equal(versionBefore + 1, versionAfter);

        var current = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        var newRuns = 0;
        Action newBody = () => { Interlocked.Increment(ref newRuns); };
        var newSnap = LiveGates.BuildSnapshot(
            2,
            new Dictionary<string, ModGeneration>(StringComparer.OrdinalIgnoreCase) { [AlphaId] = current },
            new Dictionary<string, DispatchSlot>(StringComparer.Ordinal)
            {
                [owner] = new DispatchSlot(AlphaId, slotKey, PatchKind.Transpiler, newBody, current.Generation),
            });

        // The held pre-commit snapshot is frozen: the old body stays valid for in-flight
        // execution draining at the safe point.
        var stillOld = LiveGates.ResolveCurrent(oldSlot, oldSnap);
        Assert.Same(oldBody, stillOld.Callback);
        Assert.Equal(old.Generation, stillOld.Generation);

        // The new snapshot routes to the new body, exactly once (one version step per layer).
        var routed = LiveGates.ResolveCurrent(oldSlot, newSnap);
        Assert.Same(newBody, routed.Callback);
        Assert.Equal(current.Generation, routed.Generation);

        // Both artifacts still execute: the commit never tore the old body.
        ((Action)stillOld.Callback)();
        ((Action)routed.Callback)();
        Assert.Equal(1, Volatile.Read(ref oldRuns));
        Assert.Equal(1, Volatile.Read(ref newRuns));

        // The retired generation admits no new execution even while its body reference stays valid.
        Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(old));

        chainloader.Shutdown();
    }

    /// <summary>
    /// Direct (non-slot) registration carve-out: such callbacks live outside every snapshot, are
    /// never lease-tracked, keep teardown-at-retire removal (which never fails the retire), and a
    /// post-retire direct invoke is undefined by contract but must complete, never hang.
    /// Kill-case: if direct references gated teardown, EnterTeardown would refuse while the direct
    /// delegate is still alive.
    /// </summary>
    [Fact]
    public async Task DirectReg_MidRetire_KeepsDocumentedTeardownAtRetireSemantics()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        // A direct callback held outside every snapshot is not lease-tracked.
        var directRuns = 0;
        Action direct = () => { Interlocked.Increment(ref directRuns); };
        Assert.Equal(0, LiveGates.ActiveExecutions(alpha));

        // Teardown-at-retire removal never fails the retire: with Wave absent (this suite stays
        // Wave-free) the bridge records the CoreCLR-only gap instead of throwing.
        var logs = new List<string>();
        TeardownOwner(alpha.Manifest.Id, logs.Add);
        Assert.Single(logs);
        Assert.Contains(alpha.Manifest.Id, logs[0]);
        Assert.Contains("CoreCLR-only", logs[0]);

        // Quiescence is lease-count, not ref-count: the live direct reference blocks nothing.
        LiveGates.RetireGate(alpha);
        LiveGates.SetLifetimeDirect(alpha, LifetimeState.Quiescing);
        LiveGates.EnterTeardown(alpha);
        Assert.Throws<GenerationRetiredException>(() => LiveGates.EnterExecution(alpha));

        // Post-retire direct invoke is undefined but must never hang: it completes promptly.
        await Task.Run(direct).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref directRuns));

        chainloader.Shutdown();
    }

    private static void TeardownOwner(string owner, Action<string> log)
    {
        var bridge = typeof(ModGeneration).Assembly.GetType(
            "Nami.Core.Generations.WaveBridge", throwOnError: true)!;
        var method = bridge.GetMethod(
            "TeardownOwner", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("WaveBridge.TeardownOwner not found.");
        try
        {
            method.Invoke(null, new object?[] { owner, log });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }
}
