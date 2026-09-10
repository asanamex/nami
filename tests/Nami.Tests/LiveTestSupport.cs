using System.Reflection;
using System.Runtime.CompilerServices;
using Nami.Core;
using Nami.Core.Generations;

namespace Nami.Tests;

/// <summary>
/// White-box access to the live runtime's internal gates for the xunit proofs.
/// Nami.Core exposes no InternalsVisibleTo, so access is via reflection; every
/// invocation unwraps <see cref="TargetInvocationException"/> so tests observe the
/// product exception (<see cref="GenerationRetiredException"/>,
/// <see cref="InvalidOperationException"/>), never binder noise.
/// </summary>
internal static class LiveGates
{
    private static readonly Type GenerationType = typeof(ModGeneration);
    private static readonly Type LoaderType = typeof(Chainloader);
    private static readonly Type DispatcherType = typeof(GenerationDispatcher);
    private static readonly Type SnapshotType =
        typeof(ModGeneration).Assembly.GetType("Nami.Core.Generations.RuntimeSnapshot", throwOnError: true)!;
    private static readonly Type TransitionsType =
        typeof(ModGeneration).Assembly.GetType("Nami.Core.Generations.LifetimeTransitions", throwOnError: true)!;
    private static readonly Type RetirementRecordType =
        typeof(ModGeneration).Assembly.GetType("Nami.Core.Generations.RetirementRecord", throwOnError: true)!;

    private static object Invoke(MethodBase method, object? target, object?[]? args)
    {
        try
        {
            return method.Invoke(target, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static MethodInfo GenerationMethod(string name)
        => GenerationType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"ModGeneration.{name} not found.");

    /// <summary>Enters ordinary execution (product throws GenerationRetiredException when retired).</summary>
    public static IDisposable EnterExecution(ModGeneration generation)
        => (IDisposable)Invoke(GenerationMethod("EnterExecution"), generation, null);

    /// <summary>Claims retirement; returns the outstanding lease count observed at the claim.</summary>
    public static int RetireGate(ModGeneration generation)
        => (int)Invoke(GenerationMethod("RetireGate"), generation, null);

    /// <summary>Enters teardown (product enforces quiescence plus zero held leases).</summary>
    public static void EnterTeardown(ModGeneration generation)
        => Invoke(GenerationMethod("EnterTeardown"), generation, null);

    public static int ActiveExecutions(ModGeneration generation)
        => (int)GenerationType.GetProperty(
            "ActiveExecutions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(generation)!;

    public static bool IsRetired(ModGeneration generation)
        => (bool)GenerationType.GetProperty(
            "IsRetired", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(generation)!;

    /// <summary>Direct lifetime staging for tests (bypasses edge validation by design).</summary>
    public static void SetLifetimeDirect(ModGeneration generation, LifetimeState state)
    {
        var set = GenerationType.GetProperty("Lifetime")!.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("ModGeneration.Lifetime setter not found.");
        Invoke(set, generation, new object[] { state });
    }

    /// <summary>Runs one LifetimeTransitions edge (product throws InvalidOperationException on illegal edges).</summary>
    public static LifetimeState TransitionTo(LifetimeState from, LifetimeState to)
    {
        var method = TransitionsType.GetMethod(
            "TransitionTo", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LifetimeTransitions.TransitionTo not found.");
        return (LifetimeState)Invoke(method, null, new object[] { from, to });
    }

    /// <summary>Runs one generation's OnUnload-once gate (product runs OnUnload at most once, swallowing errors).</summary>
    public static void RunOnUnloadOnce(ModGeneration generation, Action<string> logWarn)
    {
        var method = GenerationMethod("RunOnUnloadOnce");
        Invoke(method, generation, new object[] { logWarn });
    }

    public static int OnUnloadRan(ModGeneration generation)
        => (int)GenerationType.GetField(
            "_onUnloadRan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(generation)!;

    public static object CurrentSnapshot(Chainloader loader)
    {
        var field = LoaderType.GetField("_currentSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Chainloader._currentSnapshot not found.");
        return field.GetValue(loader)!;
    }

    public static long SnapshotVersion(object snapshot)
        => (long)SnapshotType.GetProperty("Version")!.GetValue(snapshot)!;

    public static IReadOnlyDictionary<string, ModGeneration> SnapshotGenerations(object snapshot)
        => (IReadOnlyDictionary<string, ModGeneration>)SnapshotType.GetProperty("Generations")!.GetValue(snapshot)!;

    public static IReadOnlyDictionary<string, DispatchSlot> SnapshotSlots(object snapshot)
        => (IReadOnlyDictionary<string, DispatchSlot>)SnapshotType.GetProperty("DispatchSlots")!.GetValue(snapshot)!;

    public static object BuildSnapshot(
        long version,
        IDictionary<string, ModGeneration> generations,
        IDictionary<string, DispatchSlot> slots)
    {
        var ctor = SnapshotType.GetConstructors().Single();
        return ctor.Invoke(new object[]
        {
            version,
            generations,
            slots,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        });
    }

    /// <summary>Pure re-resolution of a slot against a snapshot (product throws GenerationRetiredException when gone).</summary>
    public static (Delegate Callback, int Generation) ResolveCurrent(DispatchSlot slot, object snapshot)
    {
        var method = DispatcherType.GetMethod(
            "ResolveCurrent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GenerationDispatcher.ResolveCurrent not found.");
        return ((Delegate, int))Invoke(method, null, new object?[] { slot, snapshot });
    }

    /// <summary>Fused acquire-or-reject for dispatch (product path: one snapshot read plus the retire gate).</summary>
    public static (IDisposable Lease, Delegate Callback) AcquireCurrentGeneration(
        Chainloader loader, string modId, string slotKey)
    {
        var method = LoaderType.GetMethod(
            "AcquireCurrentGeneration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Chainloader.AcquireCurrentGeneration not found.");
        var result = Invoke(method, loader, new object[] { modId, slotKey });
        var type = result.GetType();
        var lease = (IDisposable)type.GetField("Item1")!.GetValue(result)!;
        var callback = (Delegate)type.GetField("Item2")!.GetValue(result)!;
        return (lease, callback);
    }

    /// <summary>Host-owned retirement log in teardown order (mod id plus generation number).</summary>
    public static IReadOnlyList<(string ModId, int Generation)> RetirementLog(Chainloader loader)
    {
        var prop = LoaderType.GetProperty(
            "RetirementRecords", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Chainloader.RetirementRecords not found.");
        var records = (System.Collections.IEnumerable)prop.GetValue(loader)!;
        var modId = RetirementRecordType.GetProperty("ModId")!;
        var genId = RetirementRecordType.GetProperty("GenerationId")!;
        var log = new List<(string, int)>();
        foreach (var record in records)
        {
            log.Add(((string)modId.GetValue(record)!, (int)genId.GetValue(record)!));
        }

        return log;
    }

    /// <summary>Captures only a weak handle to the generation's ALC (never the generation itself).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference AlcWeakOf(ModGeneration generation)
        => new WeakReference(generation.LoadContext, trackResurrection: false);

    /// <summary>Bounded observe-only collection loop (tests and diagnostics only, never runtime paths).</summary>
    public static void CollectUntilGone(IReadOnlyList<WeakReference> trackers, int rounds = 10)
    {
        for (var i = 0; i < rounds; i++)
        {
            if (AliveCount(trackers) == 0)
            {
                return;
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
    }

    public static int AliveCount(IReadOnlyList<WeakReference> trackers)
    {
        var alive = 0;
        foreach (var tracker in trackers)
        {
            if (tracker.IsAlive)
            {
                alive++;
            }
        }

        return alive;
    }
}
