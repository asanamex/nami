using System.Reflection;
using System.Text.Json;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Generations;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Tests;

/// <summary>
/// Dispatch-slot and publication proofs: patch classification never over-claims,
/// slot resolution swaps with the snapshot (never a mixed graph), a held
/// pre-commit reference still resolves the old generation, and every multi-mod
/// commit publishes through exactly one atomic root replacement.
/// </summary>
public class LiveDispatchTests
{
    private const string AlphaId = "dev.nami.fixtures.alpha";
    private const string BetaId = "dev.nami.fixtures.beta";
    private const string GammaId = "dev.nami.fixtures.gamma";

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
    public void Classify_CallbackViaTrampoline_IsFullyLiveReloadable()
    {
        foreach (var kind in new[]
                 {
                     PatchKind.CallbackGate, PatchKind.CallbackObserver,
                     PatchKind.CallbackPrefix, PatchKind.CallbackPostfix,
                 })
        {
            Assert.Equal(
                ReloadCapability.FullyLiveReloadable,
                GenerationPatchClassifier.Classify(kind, isIl2CppBackend: false, BindingMechanism.TrampolineDispatch));
        }
    }

    [Fact]
    public void Classify_TranspilerOrBakedBody_RequiresPatchRebuild()
    {
        Assert.Equal(
            ReloadCapability.RequiresPatchRebuild,
            GenerationPatchClassifier.Classify(
                PatchKind.Transpiler, isIl2CppBackend: false, BindingMechanism.TrampolineDispatch));
        Assert.Equal(
            ReloadCapability.RequiresPatchRebuild,
            GenerationPatchClassifier.Classify(
                PatchKind.CallbackObserver, isIl2CppBackend: false, BindingMechanism.BakedIntoGeneratedBody));
        Assert.Equal(
            ReloadCapability.RequiresPatchRebuild,
            GenerationPatchClassifier.Classify(
                PatchKind.CallbackPrefix, isIl2CppBackend: true, BindingMechanism.BakedIntoGeneratedBody));
    }

    [Fact]
    public void Classify_Il2CppOrNativeEntryOrBackend_AtLeastSafePointRequired()
    {
        // IL2CPP separability is mechanically present but operationally unproven: the
        // classifier reports SafePointRequired here and the host escalates to
        // RestartRequired until a proven-separable entry exists (reported, never forced).
        foreach (var kind in new[] { PatchKind.Il2CppHook, PatchKind.Il2CppFull, PatchKind.Il2CppTyped })
        {
            var capability = GenerationPatchClassifier.Classify(
                kind, isIl2CppBackend: false, BindingMechanism.NativeEntry);
            Assert.True(
                capability is ReloadCapability.SafePointRequired or ReloadCapability.RestartRequired,
                $"{kind} via NativeEntry classified {capability}");
        }

        Assert.Equal(
            ReloadCapability.SafePointRequired,
            GenerationPatchClassifier.Classify(
                PatchKind.CallbackGate, isIl2CppBackend: false, BindingMechanism.NativeEntry));
        Assert.Equal(
            ReloadCapability.SafePointRequired,
            GenerationPatchClassifier.Classify(
                PatchKind.CallbackObserver, isIl2CppBackend: true, BindingMechanism.TrampolineDispatch));
    }

    [Fact]
    public void Classify_UnknownMechanism_IsRestartRequired_NeverUnsupported()
    {
        foreach (var kind in Enum.GetValues<PatchKind>())
        {
            Assert.Equal(
                ReloadCapability.RestartRequired,
                GenerationPatchClassifier.Classify(kind, isIl2CppBackend: false, mechanism: null));
        }

        foreach (var kind in Enum.GetValues<PatchKind>())
        {
            foreach (var mechanism in Enum.GetValues<BindingMechanism>())
            {
                foreach (var backend in new[] { false, true })
                {
                    Assert.NotEqual(
                        ReloadCapability.Unsupported,
                        GenerationPatchClassifier.Classify(kind, backend, mechanism));
                }
            }
        }
    }

    [Fact]
    public void SlotOwner_UsesStableHostNaming()
    {
        Assert.Equal("nami:gen:alpha:tick", GenerationDispatcher.SlotOwnerFor("alpha", "tick"));
        Assert.Throws<ArgumentException>(() => GenerationDispatcher.SlotOwnerFor("", "tick"));
        Assert.Throws<ArgumentException>(() => GenerationDispatcher.SlotOwnerFor("alpha", "  "));
    }

    [Fact]
    public void ResolveCurrent_SwapsWithSnapshot_MissingSlotRetires()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        var v1Calls = 0;
        var v2Calls = 0;
        Action v1 = () => v1Calls++;
        Action v2 = () => v2Calls++;

        var slotKey = "swap";
        var gens = new Dictionary<string, ModGeneration>(StringComparer.OrdinalIgnoreCase)
        {
            [AlphaId] = alpha,
        };
        var snap1 = LiveGates.BuildSnapshot(1, gens, new Dictionary<string, DispatchSlot>(StringComparer.Ordinal)
        {
            [GenerationDispatcher.SlotOwnerFor(AlphaId, slotKey)] =
                new DispatchSlot(AlphaId, slotKey, PatchKind.CallbackObserver, v1, alpha.Generation),
        });

        var probe = new DispatchSlot(AlphaId, slotKey, PatchKind.CallbackObserver, v1, alpha.Generation);
        var resolved1 = LiveGates.ResolveCurrent(probe, snap1);
        Assert.Same(v1, resolved1.Callback);
        Assert.Equal(alpha.Generation, resolved1.Generation);

        // Publish v2: the new snapshot resolves the new callback while the old one is untouched.
        var snap2 = LiveGates.BuildSnapshot(2, gens, new Dictionary<string, DispatchSlot>(StringComparer.Ordinal)
        {
            [GenerationDispatcher.SlotOwnerFor(AlphaId, slotKey)] =
                new DispatchSlot(AlphaId, slotKey, PatchKind.CallbackObserver, v2, alpha.Generation),
        });
        var resolved2 = LiveGates.ResolveCurrent(probe, snap2);
        Assert.Same(v2, resolved2.Callback);

        var resolvedOld = LiveGates.ResolveCurrent(probe, snap1);
        Assert.Same(v1, resolvedOld.Callback);

        resolved1.Callback.DynamicInvoke();
        resolved2.Callback.DynamicInvoke();
        Assert.Equal(1, v1Calls);
        Assert.Equal(1, v2Calls);

        // A removed slot means its generation retired: no new execution, fail fast.
        var empty = LiveGates.BuildSnapshot(3, gens, new Dictionary<string, DispatchSlot>(StringComparer.Ordinal));
        var retired = Assert.Throws<GenerationRetiredException>(() => LiveGates.ResolveCurrent(probe, empty));
        Assert.Equal(AlphaId, retired.ModId);

        chainloader.Shutdown();
    }

    [Fact]
    public void AcquireCurrentGeneration_MissingSlot_ThrowsWithoutLeakingLease()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        var ex = Assert.Throws<GenerationRetiredException>(
            () => LiveGates.AcquireCurrentGeneration(chainloader, AlphaId, "no-such-slot"));
        Assert.Equal(AlphaId, ex.ModId);
        Assert.Equal(0, LiveGates.ActiveExecutions(alpha));
        chainloader.Shutdown();
    }

    [Fact]
    public void Snapshot_HeldPreCommitReference_StillResolvesOldGenerationAfterPublish()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();

        var before = LiveGates.CurrentSnapshot(chainloader);
        var beforeVersion = LiveGates.SnapshotVersion(before);
        var beforeAlpha = LiveGates.SnapshotGenerations(before)[AlphaId].Generation;
        var beforeSlots = LiveGates.SnapshotSlots(before).Count;

        var result = chainloader.Reload(AlphaId);
        Assert.Empty(result.Failed);

        var after = LiveGates.CurrentSnapshot(chainloader);
        Assert.NotSame(before, after);
        Assert.Equal(beforeVersion + 1, LiveGates.SnapshotVersion(after));

        // Snapshot immutability, not locking: the held reference is frozen at publish time.
        Assert.Equal(beforeVersion, LiveGates.SnapshotVersion(before));
        Assert.Equal(beforeAlpha, LiveGates.SnapshotGenerations(before)[AlphaId].Generation);
        Assert.Equal(beforeSlots, LiveGates.SnapshotSlots(before).Count);
        Assert.True(LiveGates.SnapshotGenerations(after)[AlphaId].Generation > beforeAlpha);
        chainloader.Shutdown();
    }

    [Fact]
    public void Publish_MultiModCommit_UsesExactlyOneAtomicExchange()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();

        var statusPath = Path.Combine(fixture.Root, "status.json");
        var before = JsonDocument.Parse(File.ReadAllText(statusPath)).RootElement.GetProperty("version").GetInt64();

        var result = chainloader.Reload(AlphaId);
        Assert.Equal(new[] { AlphaId, BetaId, GammaId }, result.Reloaded);

        // Three generations replaced, one root replacement: readers never see a mixed graph.
        var after = JsonDocument.Parse(File.ReadAllText(statusPath)).RootElement.GetProperty("version").GetInt64();
        Assert.Equal(before + 1, after);
        chainloader.Shutdown();
    }

    [Fact]
    public void Publish_ConcurrentDispatchers_NeverObserveMixedGraph()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var boot = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId).Generation;
        // Generation numbers come from one global sequence (boot deals alpha=n,
        // beta=n+1, gamma=n+2; every alpha-rooted reload prepares all three in
        // order), so the atomicity invariant is constant offsets — never a
        // mixed-version graph. Lifetime and ReloadCount are assigned pre-publication
        // (CommitGraph finalizes candidates before the exchange), so the published
        // objects are fully formed; only the dispatch-relevant state (generation
        // numbers, slots) is what this test guards.
        const int commits = 6;
        var observations = new System.Collections.Concurrent.ConcurrentBag<(int A, int B, int C)>();
        var stop = false;
        var readers = new Thread[4];
        for (var t = 0; t < readers.Length; t++)
        {
            readers[t] = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    var plugins = chainloader.Plugins;
                    var a = plugins.Single(p => p.Manifest.Id == AlphaId);
                    var b = plugins.Single(p => p.Manifest.Id == BetaId);
                    var c = plugins.Single(p => p.Manifest.Id == GammaId);
                    observations.Add((a.Generation, b.Generation, c.Generation));
                }
            });
            readers[t].Start();
        }

        try
        {
            for (var i = 0; i < commits; i++)
            {
                var result = chainloader.Reload(AlphaId);
                Assert.Empty(result.Failed);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            foreach (var reader in readers)
            {
                reader.Join(TimeSpan.FromSeconds(20));
            }
        }

        Assert.NotEmpty(observations);
        foreach (var (a, b, c) in observations)
        {
            // One snapshot per observation: constant generation offsets, never
            // a half-published mix (an old beta beside a new alpha breaks this).
            Assert.True(b - a == 1 && c - b == 1, $"mixed graph observed: alpha={a} beta={b} gamma={c}");
            Assert.InRange(a, boot, boot + (3 * commits));
        }

        // Eventual consistency once the commit's post-publication bookkeeping lands.
        var end = chainloader.Plugins;
        Assert.Equal(boot + (3 * commits), end.Single(p => p.Manifest.Id == AlphaId).Generation);
        Assert.All(end, p => Assert.Equal(commits, p.ReloadCount));
        chainloader.Shutdown();
    }

    [Fact]
    public void OwnerSuperseded_AbsentOwner_KeepsTeardown()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        // Removed mods leave stale Wave entries with no snapshot row: teardown proceeds.
        var snapshot = LiveGates.CurrentSnapshot(chainloader);
        Assert.False(IsOwnerSuperseded("nami:gen:gone:slot", alpha.Generation, snapshot));
        chainloader.Shutdown();
    }

    [Fact]
    public void OwnerSuperseded_NewerGeneration_SkipsTeardown_OwnGeneration_KeepsIt()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        const string slotKey = "shared";
        var owner = GenerationDispatcher.SlotOwnerFor(AlphaId, slotKey);
        Action callback = () => { };
        var gens = new Dictionary<string, ModGeneration>(StringComparer.OrdinalIgnoreCase)
        {
            [AlphaId] = alpha,
        };
        var replacement = LiveGates.BuildSnapshot(2, gens, new Dictionary<string, DispatchSlot>(StringComparer.Ordinal)
        {
            [owner] = new DispatchSlot(AlphaId, slotKey, PatchKind.CallbackObserver, callback, alpha.Generation + 1),
        });
        var own = LiveGates.BuildSnapshot(2, gens, new Dictionary<string, DispatchSlot>(StringComparer.Ordinal)
        {
            [owner] = new DispatchSlot(AlphaId, slotKey, PatchKind.CallbackObserver, callback, alpha.Generation),
        });

        // A reload committed after the retire gate: the owner serves the replacement.
        Assert.True(IsOwnerSuperseded(owner, alpha.Generation, replacement));
        Assert.False(IsOwnerSuperseded(owner, alpha.Generation + 1, replacement));
        Assert.False(IsOwnerSuperseded(owner, alpha.Generation, own));
        chainloader.Shutdown();
    }

    [Fact]
    public void NeedsSlotReinstall_StableSlot_TypeChange_ForcesRebuild()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);

        const string slotKey = "typed";
        var owner = GenerationDispatcher.SlotOwnerFor(AlphaId, slotKey);
        Action v1 = () => { };
        var preSlots = new Dictionary<string, DispatchSlot>(StringComparer.Ordinal)
        {
            [owner] = new DispatchSlot(AlphaId, slotKey, PatchKind.CallbackObserver, v1, alpha.Generation),
        };

        // Genuinely new slot: pre-publication registration already ran, no rebuild here.
        Assert.False(NeedsSlotReinstall(
            NewSlotStage(AlphaId, "fresh", PatchKind.CallbackObserver, v1, trampoline: v1, stable: true, alpha.Generation + 1),
            preSlots));

        // Stable trampoline, same delegate type: snapshot swap alone, zero Wave rebuild.
        Action v2SameType = () => { };
        Assert.False(NeedsSlotReinstall(
            NewSlotStage(AlphaId, slotKey, PatchKind.CallbackObserver, v2SameType, trampoline: v2SameType, stable: true, alpha.Generation + 1),
            preSlots));

        // Stable trampoline but the delegate TYPE changed: the registered trampoline baked
        // castclass <v1-type>, so dispatch would throw InvalidCastException — rebuild instead.
        Func<bool> v2OtherType = () => true;
        Assert.True(NeedsSlotReinstall(
            NewSlotStage(AlphaId, slotKey, PatchKind.CallbackObserver, v2OtherType, trampoline: v2OtherType, stable: true, alpha.Generation + 1),
            preSlots));

        // Per-generation trampoline or direct registration: always reinstalls.
        Assert.True(NeedsSlotReinstall(
            NewSlotStage(AlphaId, slotKey, PatchKind.CallbackObserver, v2SameType, trampoline: v2SameType, stable: false, alpha.Generation + 1),
            preSlots));
        Assert.True(NeedsSlotReinstall(
            NewSlotStage(AlphaId, slotKey, PatchKind.Transpiler, v2SameType, trampoline: null, stable: false, alpha.Generation + 1),
            preSlots));
        chainloader.Shutdown();
    }

    private static bool IsOwnerSuperseded(string owner, int retiringGeneration, object snapshot)
    {
        var method = typeof(Chainloader).GetMethod(
            "IsOwnerSuperseded", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Chainloader.IsOwnerSuperseded not found.");
        try
        {
            return (bool)method.Invoke(null, new object?[] { owner, retiringGeneration, snapshot })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static object NewSlotStage(
        string modId, string slotKey, PatchKind kind, Delegate callback,
        Delegate? trampoline, bool stable, int generation)
    {
        // SlotStage is internal (no InternalsVisibleTo); build it the same reflective way
        // LiveGates reaches every other internal seam.
        var stageType = typeof(ModGeneration).Assembly.GetType("Nami.Core.Generations.SlotStage", throwOnError: true)!;
        var stage = Activator.CreateInstance(stageType)
            ?? throw new InvalidOperationException("SlotStage has no parameterless constructor.");
        void Set(string name, object? value) =>
            stageType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(stage, value);
        Set("ModId", modId);
        Set("SlotKey", slotKey);
        Set("Kind", kind);
        Set("Slot", new DispatchSlot(modId, slotKey, kind, callback, generation));
        Set("Trampoline", trampoline);
        Set("Stable", stable);
        return stage;
    }

    private static bool NeedsSlotReinstall(object stage, IReadOnlyDictionary<string, DispatchSlot> preSlots)
    {
        var method = typeof(Chainloader).GetMethod(
            "NeedsSlotReinstall", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Chainloader.NeedsSlotReinstall not found.");
        try
        {
            return (bool)method.Invoke(null, new object?[] { stage, preSlots })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
