using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Nami.Wave;

namespace Nami.Wave.Tests;

/// <summary>
/// Wave-layer seam proofs: steady-state concurrent dispatch (stable hook/patch, no rebuild in
/// flight) is exact from every thread; sequential body swaps publish whole artifacts with the
/// documented original-only Unpatch window; teardown-at-retire leaves original-only behavior.
/// 'Torn' means precisely: the seam throws (MissingMethod/NullRef/InvalidCast from the
/// detour), the original body is skipped without a gate vote to skip, an observer/prefix fires
/// twice for one call, or one call observes a half-rebuilt chain (both the old and the new
/// callback fire for it).
/// Explicit non-goal, verified by experiment: hammering across a SAME-OWNER rebuild (which
/// passes through zero entries, tearing the detour down under in-flight calls) demonstrably
/// tears — NullReference inside the target under load, skipped/duplicated originals, and an
/// access violation at small body sizes. Rebuilds are therefore safe-point-only work after
/// quiescence (no in-flight ordinary execution), which the gate layer proves in Nami.Tests
/// (<c>LiveMidCallbackTests</c>); this class proves everything up to that boundary. Targets are
/// private to this class so no sibling can observe these hooks (the assembly additionally
/// disables parallelization).
/// </summary>
public class WaveRebuildSeamTests : IDisposable
{
    public WaveRebuildSeamTests()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
    }

    public void Dispose()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
    }

    // Detour-sized bodies: Wave rewrites the target prologue inline, so every target needs a
    // realistic body well over the detour jump (the loop pattern the existing suites use). A
    // single-statement body is too small to host the detour and access-violates.
    private static class ObserverTarget
    {
        public static int Counter;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Fire()
        {
            // Interlocked: four hammer threads share this body, so plain += would lose updates
            // and fake a product skip. The loop keeps the detour-sized realistic shape.
            for (var i = 0; i < 4; i++) Interlocked.Add(ref Counter, i);
        }
    }

    private static class PrefixTarget
    {
        public static int Counter;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Fire()
        {
            for (var i = 0; i < 4; i++) Interlocked.Add(ref Counter, i);
        }
    }

    private static class ArithTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Add(int a, int b) => a + b;
    }

    private static class DirectTarget
    {
        public static int Counter;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Fire()
        {
            for (var i = 0; i < 4; i++) Counter += i;
        }
    }

    private static MethodInfo M(Expression<Action> e) =>
        (MethodInfo)((MethodCallExpression)e.Body).Method;

    private static MethodInfo MInt(Expression<Func<int>> e) =>
        (MethodInfo)((MethodCallExpression)e.Body).Method;

    /// <summary>
    /// Steady-state concurrent dispatch (stable hook, no rebuild in flight): every call runs the
    /// original exactly once and fires the observer exactly once, from any thread.
    /// Kill-case: torn concurrent dispatch would throw, skip Fire, or misdeliver the observer.
    /// </summary>
    [Fact]
    public void HookObserver_SteadyStateConcurrentDispatch_NeverTorn()
    {
        var m = M(() => ObserverTarget.Fire());
        ObserverTarget.Counter = 0;
        var seen = 0;
        const string owner = "seam-observer";

        Wave.Hook(m, owner, observer: () => { Interlocked.Increment(ref seen); });
        ObserverTarget.Fire();
        Assert.Equal(6, ObserverTarget.Counter);
        Assert.Equal(1, Volatile.Read(ref seen));
        ObserverTarget.Counter = 0;
        Volatile.Write(ref seen, 0);

        const int threads = 4;
        const int iters = 1000;
        var total = threads * iters;
        var errors = new ConcurrentQueue<Exception>();
        var workers = new Thread[threads];
        for (var t = 0; t < workers.Length; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (var i = 0; i < iters; i++)
                {
                    try
                    {
                        ObserverTarget.Fire();
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                        return;
                    }
                }
            });
            workers[t].Start();
        }

        foreach (var worker in workers)
        {
            worker.Join(TimeSpan.FromSeconds(30));
            Assert.False(worker.IsAlive);
        }

        Assert.Empty(errors);

        // Stable hook, exact accounting: the original ran once per call and the observer fired
        // once per call — concurrent dispatch itself never tears.
        Assert.Equal(6 * total, ObserverTarget.Counter);
        Assert.Equal(total, Volatile.Read(ref seen));
    }

    /// <summary>
    /// Steady-state concurrent dispatch on the Patch entry point (the RequiresPatchRebuild path's
    /// stable state): exact original and prefix accounting from every thread, no rebuild in flight.
    /// Kill-case: torn concurrent dispatch would throw, skip Fire, or misdeliver the prefix.
    /// </summary>
    [Fact]
    public void PatchPrefix_SteadyStateConcurrentDispatch_NeverTorn()
    {
        var m = M(() => PrefixTarget.Fire());
        PrefixTarget.Counter = 0;
        var seen = 0;
        const string owner = "seam-prefix";

        Wave.Patch(m, owner, prefix: (Action)(() => { Interlocked.Increment(ref seen); }));
        PrefixTarget.Fire();
        Assert.Equal(6, PrefixTarget.Counter);
        Assert.Equal(1, Volatile.Read(ref seen));
        Assert.True(Wave.IsPatched(m, owner));
        PrefixTarget.Counter = 0;
        Volatile.Write(ref seen, 0);

        const int threads = 4;
        const int iters = 1000;
        var total = threads * iters;
        var errors = new ConcurrentQueue<Exception>();
        var workers = new Thread[threads];
        for (var t = 0; t < workers.Length; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (var i = 0; i < iters; i++)
                {
                    try
                    {
                        PrefixTarget.Fire();
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                        return;
                    }
                }
            });
            workers[t].Start();
        }

        foreach (var worker in workers)
        {
            worker.Join(TimeSpan.FromSeconds(30));
            Assert.False(worker.IsAlive);
        }

        Assert.Empty(errors);
        Assert.True(Wave.IsPatched(m, owner));

        // Same exact accounting through the Patch entry point.
        Assert.Equal(6 * total, PrefixTarget.Counter);
        Assert.Equal(total, Volatile.Read(ref seen));
    }

    /// <summary>
    /// Transpiler body swap, sequential: each rebuild publishes one whole body, and the Unpatch
    /// window serves the original — every observed value is a whole artifact, never a torn mix.
    /// Kill-case: a misrouted rebuild would return a stale body after its swap or a non-option value.
    /// </summary>
    [Fact]
    public void Transpiler_BodySwap_Sequential_WholeValues()
    {
        var m = MInt(() => ArithTarget.Add(0, 0));
        const string owner = "seam-transpiler";

        Wave.Transpile(m, owner, il =>
        {
            Assert.True(il.Goto(OpCodes.Add));
            il.Replace(OpCodes.Sub);
        });
        Assert.True(Wave.IsPatched(m, owner));
        Assert.Equal(7, ArithTarget.Add(10, 3));

        // The documented unpatched window serves the original body only.
        Wave.Unpatch(m, owner);
        Assert.False(Wave.IsPatched(m, owner));
        Assert.Equal(13, ArithTarget.Add(10, 3));

        Wave.Transpile(m, owner, il =>
        {
            Assert.True(il.Goto(OpCodes.Add));
            il.Replace(OpCodes.Mul);
        });
        Assert.True(Wave.IsPatched(m, owner));
        Assert.Equal(30, ArithTarget.Add(10, 3));

        Wave.Unpatch(m, owner);
        Assert.False(Wave.IsPatched(m, owner));
        Assert.Equal(13, ArithTarget.Add(10, 3));
    }
    /// <summary>
    /// Direct (non-slot) registration carve-out at the Wave layer: teardown-at-retire removes the
    /// entry, and post-retire calls run the original only — promptly, never hanging.
    /// Kill-case: if teardown missed the direct entry, its stale observer would keep firing.
    /// </summary>
    [Fact]
    public void DirectReg_TeardownAtRetire_RunsOriginalOnlyWithoutHang()
    {
        var m = M(() => DirectTarget.Fire());
        DirectTarget.Counter = 0;
        var seen = 0;
        const string owner = "direct-carve";

        Wave.Hook(m, owner, observer: () => { Interlocked.Increment(ref seen); });
        DirectTarget.Fire();
        Assert.Equal(1, Volatile.Read(ref seen));
        Assert.True(Wave.IsHooked(m, owner));

        Wave.UnhookAll(owner);

        Assert.False(Wave.IsHooked(m, owner));
        const int calls = 200;
        for (var i = 0; i < calls; i++)
        {
            DirectTarget.Fire();
        }

        Assert.Equal(6 * (calls + 1), DirectTarget.Counter);
        Assert.Equal(1, Volatile.Read(ref seen));
    }
}
