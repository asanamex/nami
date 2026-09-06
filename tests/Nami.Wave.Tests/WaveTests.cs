using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nami.Wave;

namespace Nami.Wave.Tests;

// These tests exercise REAL inline detours on JIT-compiled methods in this test process.
// Release builds give the realistic shape; behavior assertions hold in both.

public static class Target
{
    // Realistic bodies (well over the 14-byte detour jump). Each invocation adds a fixed
    // delta to the static so tests assert on accumulated deltas.
    public const int PingDelta = 6;
    public const int CountedDelta = 6;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ping()
    {
        for (int i = 0; i < 4; i++) Calls += i;
    }

    public static int Calls;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Counted()
    {
        for (int i = 0; i < 4; i++) Counter += i;
    }

    public static int Counter;

    // A method with a real stack frame (locals + calls) — exercises the skip trampoline.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Framed()
    {
        long local = 42;
        for (int i = 0; i < 3; i++) local = local * 3 + i;
        Counter += (int)(local % 1000);
    }

    public static int AddOne(int x) => x + 1;
}

public class WaveTests : IDisposable
{
    public WaveTests() => Wave.UnhookEverything();

    public void Dispose() => Wave.UnhookEverything();

    private static MethodInfo M(Expression<Action> e) =>
        (MethodInfo)((MethodCallExpression)e.Body).Method;

    [Fact]
    public void Observer_RunsAndOriginalStillExecutes()
    {
        Target.Calls = 0;
        var observed = 0;
        var m = M(() => Target.Ping());

        Wave.Hook(m, "test", observer: () => observed++);
        Target.Ping();
        Target.Ping();

        Assert.Equal(2 * Target.PingDelta, Target.Calls); // original ran
        Assert.Equal(2, observed);                         // observer ran each time
    }

    [Fact]
    public void Gate_TrueSkipsOriginal()
    {
        Target.Calls = 0;
        var gateRuns = 0;
        var m = M(() => Target.Ping());

        Wave.Hook(m, "test", gate: () => { gateRuns++; return true; });
        Target.Ping();

        Assert.Equal(0, Target.Calls); // original skipped
        Assert.Equal(1, gateRuns);
    }

    [Fact]
    public void Gate_FalseRunsOriginal()
    {
        Target.Calls = 0;
        var m = M(() => Target.Ping());

        Wave.Hook(m, "test", gate: () => false);
        Target.Ping();
        Target.Ping();

        Assert.Equal(2 * Target.PingDelta, Target.Calls);
    }

    [Fact]
    public void Unhook_RestoresOriginalBehavior()
    {
        Target.Calls = 0;
        var m = M(() => Target.Ping());

        Wave.Hook(m, "test", gate: () => true);
        Target.Ping();
        Assert.Equal(0, Target.Calls);

        Wave.Unhook(m, "test");
        Target.Ping();
        Assert.Equal(Target.PingDelta, Target.Calls);
        Assert.False(Wave.IsHooked(m, "test"));
    }

    [Fact]
    public void Chain_MultipleOwners_RunLifo()
    {
        Target.Calls = 0;
        var order = new List<string>();
        var m = M(() => Target.Ping());

        Wave.Hook(m, "owner-a", observer: () => order.Add("a"));
        Wave.Hook(m, "owner-b", observer: () => order.Add("b"));

        Target.Ping();

        Assert.Equal(new[] { "b", "a" }, order); // newest first
        Assert.Equal(Target.PingDelta, Target.Calls);
    }

    [Fact]
    public void Chain_AnyGateTrue_SkipsOriginalForAll()
    {
        Target.Calls = 0;
        var m = M(() => Target.Ping());

        Wave.Hook(m, "owner-a", gate: () => false);
        Wave.Hook(m, "owner-b", gate: () => true);

        Target.Ping();
        Assert.Equal(0, Target.Calls);
    }

    [Fact]
    public void UnhookOneOwner_KeepsOthers()
    {
        Target.Calls = 0;
        var a = 0;
        var b = 0;
        var m = M(() => Target.Ping());

        Wave.Hook(m, "owner-a", observer: () => a++);
        Wave.Hook(m, "owner-b", observer: () => b++);

        Wave.Unhook(m, "owner-a");
        Target.Ping();

        Assert.Equal(0, a);
        Assert.Equal(1, b);
        Assert.Equal(Target.PingDelta, Target.Calls);
    }

    [Fact]
    public void UnhookAll_ForOwner_RemovesEveryTarget()
    {
        Target.Calls = 0;
        Target.Counter = 0;
        var m1 = M(() => Target.Ping());
        var m2 = M(() => Target.Counted());

        Wave.Hook(m1, "mod1", gate: () => true);
        Wave.Hook(m2, "mod1", gate: () => true);
        Wave.UnhookAll("mod1");

        Target.Ping();
        Target.Counted();
        Assert.Equal(Target.PingDelta, Target.Calls);
        Assert.Equal(Target.CountedDelta, Target.Counter);
    }

    [Fact]
    public void DuplicateOwner_Throws()
    {
        var m = M(() => Target.Ping());
        Wave.Hook(m, "dup", gate: () => false);
        Assert.Throws<InvalidOperationException>(() => Wave.Hook(m, "dup", gate: () => false));
    }

    [Fact]
    public void FramedMethod_GateSkipWorks_ObserverWorks()
    {
        Target.Counter = 0;
        var m = M(() => Target.Framed());

#if DEBUG
        // Debug JIT emits a non-relocatable prologue shape for this method; Wave targets
        // the optimized (Release) code that games actually ship. Verify we refuse loudly
        // rather than corrupting the process.
        Assert.Throws<Wave.HookException>(() => Wave.Hook(m, "obs", observer: () => { }));
#else
        // Observer: original runs.
        var observed = 0;
        Wave.Hook(m, "obs", observer: () => observed++);
        Target.Framed();
        Assert.Equal(1, observed);
        Wave.Unhook(m, "obs");

        // Gate skip on a frame method: the skip trampoline unwinds the frame cleanly.
        Wave.Hook(m, "gate", gate: () => true);
        var before = Target.Counter;
        Target.Framed();
        Assert.Equal(before, Target.Counter); // original did NOT run
#endif
    }

    [Fact]
    public void Hook_RejectsValueReturningMethod()
    {
        var nonVoid = typeof(Target).GetMethod(nameof(Target.AddOne))!;
        Assert.Throws<Wave.HookException>(() => Wave.Hook(nonVoid, "t", observer: () => { }));
    }

    [Fact]
    public void Hook_NoCallback_Throws()
    {
        var m = M(() => Target.Ping());
        Assert.Throws<ArgumentException>(() => Wave.Hook(m, "t"));
    }

    // Dedicated target for the re-hook test: kept separate so no other test perturbs the
    // JIT state of this method (tiered JIT may back-patch a repeatedly hooked/unhooked
    // method to a fresh body, which would silently bypass a stale detour — a documented
    // Wave limit: hook methods that are already hot/stable).
    public static class RehookTarget
    {
        public const int Delta = 6;
        public static int Calls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Ping()
        {
            for (int i = 0; i < 4; i++) Calls += i;
        }
    }

    [Fact]
    public void Rehook_AfterFullUnhook_Works()
    {
        RehookTarget.Calls = 0;
        var m = M(() => RehookTarget.Ping());

        // Warm up past tier-0 FIRST so the JIT never creates a new code version while a
        // detour is installed (tiered JIT may back-patch a repeatedly hooked/unhooked
        // method to a fresh body, which would silently bypass a stale detour — a documented
        // Wave limit: hook methods that are already hot/stable).
        for (int i = 0; i < 50_000; i++) RehookTarget.Ping();
        RehookTarget.Calls = 0;

        Wave.Hook(m, "a", observer: () => { });
        RehookTarget.Ping();
        Assert.Equal(RehookTarget.Delta, RehookTarget.Calls);
        Wave.UnhookAll("a");
        RehookTarget.Calls = 0;

        // Re-hook the same method: site was torn down; a fresh detour must install cleanly.
        Wave.Hook(m, "b", gate: () => true);
        RehookTarget.Ping();
        Assert.Equal(0, RehookTarget.Calls);

        Wave.UnhookAll("b");
        RehookTarget.Ping();
        Assert.Equal(RehookTarget.Delta, RehookTarget.Calls);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(100000)]
    public void Hook_Overhead_IsSmall(int iterations)
    {
        var m = M(() => Target.Ping());

        // Warm up (also promotes tier-1) BEFORE installing the hook and resetting.
        for (int i = 0; i < 10_000; i++) Target.Ping();

        Wave.Hook(m, "bench", observer: () => { });
        Target.Calls = 0;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) Target.Ping();
        sw.Stop();

        double nsPerCall = sw.Elapsed.TotalNanoseconds / iterations;
        Assert.True(nsPerCall < 500, $"hooked call too slow: {nsPerCall:F1} ns/call");
        Assert.Equal(iterations * Target.PingDelta, Target.Calls);
    }
}
