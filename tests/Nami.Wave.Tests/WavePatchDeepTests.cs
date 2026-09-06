using System.Reflection;
using System.Runtime.CompilerServices;
using Nami.Wave;

namespace Nami.Wave.Tests;

public static class M2DeepTargets
{
    public static int Calls;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Compute(int x, int y)
    {
        Calls++;
        if (x > 100)
        {
            return x * y; // early ret path
        }
        return x + y;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Wrap(string s)
    {
        try
        {
            return s.Trim();
        }
        catch
        {
            return "";
        }
    }

    public class Player
    {
        public int Health = 100;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Status(string name)
        {
            return $"{name}:{Health}";
        }
    }
}

#if DEBUG
// Debug JIT emits non-relocatable prologue shapes for the branched/framed targets in this
// class; Wave targets the optimized (Release) code that games actually ship. (See
// WaveTests.FramedMethod for the same policy.) Deep patch semantics are fully exercised in
// Release builds; the IL-copy machinery itself is configuration-independent.
public class WavePatchDeepTests
{
    [Fact]
    public void Patch_DeepSemantics_SkippedInDebug() { }
}
#else
public class WavePatchDeepTests : IDisposable
{
    public WavePatchDeepTests()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
        M2DeepTargets.Calls = 0;
    }

    public void Dispose()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
    }

    // ============================================= __state threading prefix -> postfix

    [Fact]
    public void Patch_State_ThreadsPrefixToPostfix()
    {
        object? seenState = "unset";
        var m = typeof(M2DeepTargets).GetMethod(nameof(M2DeepTargets.Compute))!;

        // Prefix writes __state; postfix reads it back (same call).
        Wave.Patch(m, "t",
            prefix: (int x, int y, out object __state) => { __state = $"x={x},y={y}"; },
            postfix: (ref object __state, ref int __result) =>
            {
                seenState = __state;
                __result += 1;
            });

        var result = M2DeepTargets.Compute(20, 22); // 42 + 1
        Assert.Equal(43, result);
        Assert.Equal("x=20,y=22", seenState);
    }

    // ============================================= __args full argument array

    [Fact]
    public void Patch_Args_ReceivesAllArguments()
    {
        object[]? seen = null;
        var m = typeof(M2DeepTargets).GetMethod(nameof(M2DeepTargets.Compute))!;

        Wave.Patch(m, "t", prefix: (object[] __args) => { seen = __args; });
        var result = M2DeepTargets.Compute(5, 6);

        Assert.Equal(11, result);
        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Length);
        Assert.Equal(5, seen[0]);
        Assert.Equal(6, seen[1]);
    }

    // ============================================= bool prefix returning true (run original)

    [Fact]
    public void Patch_PrefixTrue_RunsOriginal()
    {
        var ran = 0;
        var m = typeof(M2DeepTargets).GetMethod(nameof(M2DeepTargets.Compute))!;

        Wave.Patch(m, "t", prefix: (int x, int y) => { ran++; return true; });
        var result = M2DeepTargets.Compute(20, 22);

        Assert.Equal(42, result);
        Assert.Equal(1, ran);
        Assert.Equal(1, M2DeepTargets.Calls);
    }

    // ============================================= multiple rets (early + late)

    [Fact]
    public void Patch_MultipleRets_PostfixRunsOnEachPath()
    {
        var postfixRuns = 0;
        var m = typeof(M2DeepTargets).GetMethod(nameof(M2DeepTargets.Compute))!;

        Wave.Patch(m, "t", postfix: (ref int __result) => { postfixRuns++; __result += 1000; });

        var big = M2DeepTargets.Compute(200, 3);   // early ret: 600
        var small = M2DeepTargets.Compute(20, 22); // late ret: 42

        Assert.Equal(1600, big);
        Assert.Equal(1042, small);
        Assert.Equal(2, postfixRuns);
    }

    // ============================================= EH (try/catch) target

    [Fact]
    public void Patch_TryCatchTarget_Works()
    {
        var postfixRuns = 0;
        var m = typeof(M2DeepTargets).GetMethod(nameof(M2DeepTargets.Wrap))!;

        Wave.Patch(m, "t", postfix: (ref string __result) => { postfixRuns++; __result += "!"; });
        var result = M2DeepTargets.Wrap("  hi  ");

        Assert.Equal("hi!", result);
        Assert.Equal(1, postfixRuns);
    }

    // ============================================= instance method returning string

    [Fact]
    public void Patch_InstanceStringMethod()
    {
        M2DeepTargets.Player? seenInstance = null;
        string? seenResult = null;
        var m = typeof(M2DeepTargets.Player).GetMethod(nameof(M2DeepTargets.Player.Status))!;

        Wave.Patch(m, "t",
            prefix: (M2DeepTargets.Player __instance) => { seenInstance = __instance; },
            postfix: (ref string __result) => { seenResult = __result; });

        var player = new M2DeepTargets.Player { Health = 70 };
        var result = player.Status("nami");

        Assert.Equal("nami:70", result);
        Assert.Same(player, seenInstance);
        Assert.Equal("nami:70", seenResult);
    }
}
#endif
