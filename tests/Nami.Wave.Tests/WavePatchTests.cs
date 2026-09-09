using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nami.Wave;

namespace Nami.Wave.Tests;

// M2 patch targets - realistic shapes with real bodies.

public static class M2Targets
{
    public static int Calls;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Add(int a, int b)
    {
        Calls++;
        return a + b;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Log(string message)
    {
        Calls++;
        LastMessage = message;
    }

    public static string LastMessage = "";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ping()
    {
        for (int i = 0; i < 4; i++) Calls += i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long MulLong(long a, long b) => a * b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Concat(string a, string b) => a + b;

    public class Hero
    {
        public int Hp;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int TakeDamage(int amount)
        {
            Hp -= amount;
            return Hp;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Greet(string name) => $"hi {name} (hp={Hp})";
    }
}

public class WavePatchTests : IDisposable
{
    public WavePatchTests()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
        M2Targets.Calls = 0;
        M2Targets.LastMessage = "";
    }

    public void Dispose()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
    }

    private static MethodInfo M(Expression<Action> e) =>
        (MethodInfo)((MethodCallExpression)e.Body).Method;

    // ===================================================== value returns + args

    // Prefix sees the args; postfix sees result. Static target, int params.
    private static int s_seenA;
    private static int s_seenB;
    private static int s_seenResult;

    static void Prefix_Add(int a, int b)
    {
        s_seenA = a;
        s_seenB = b;
    }

    static void Postfix_Add(int a, int b, ref int __result)
    {
        s_seenA = a;
        s_seenB = b;
        s_seenResult = __result;
        __result += 1000; // rewrite
    }

    [Fact]
    public void Patch_ValueReturn_ArgsAndResultRewrite()
    {
        s_seenA = s_seenB = s_seenResult = 0;
        var m = typeof(M2Targets).GetMethod(nameof(M2Targets.Add))!;

        Wave.Patch(m, "t", prefix: Prefix_Add, postfix: Postfix_Add);
        var result = M2Targets.Add(20, 22);

        Assert.Equal(20, s_seenA);      // prefix saw arg a
        Assert.Equal(22, s_seenB);      // prefix saw arg b
        Assert.Equal(42, s_seenResult); // postfix saw original result
        Assert.Equal(1042, result);     // postfix rewrote the result
        Assert.Equal(1, M2Targets.Calls);
    }

    // ===================================================== instance + __instance

    static void Prefix_Instance(M2Targets.Hero __instance, int amount)
    {
        // Damage immunity: absorb the hit.
        s_seenA = amount;
        s_instHpBefore = __instance.Hp;
    }

    static void Postfix_Instance(M2Targets.Hero __instance, ref int __result)
    {
        s_instHpAfter = __result;
    }

    private static int s_instHpBefore;
    private static int s_instHpAfter;

    [Fact]
    public void Patch_InstanceMethod_SeesInstanceAndResult()
    {
        var hero = new M2Targets.Hero { Hp = 100 };
        var m = typeof(M2Targets.Hero).GetMethod(nameof(M2Targets.Hero.TakeDamage))!;

        Wave.Patch(m, "t", prefix: Prefix_Instance, postfix: Postfix_Instance);
        var hp = hero.TakeDamage(30);

        Assert.Equal(70, hp);            // original ran
        Assert.Equal(30, s_seenA);       // prefix saw amount
        Assert.Equal(100, s_instHpBefore); // prefix saw this.Hp BEFORE
        Assert.Equal(70, s_instHpAfter);   // postfix saw this.Hp AFTER
    }

    // ===================================================== skip (bool prefix)

    static bool Prefix_Skip() => false;

    [Fact]
    public void Patch_PrefixFalse_SkipsOriginal()
    {
        M2Targets.Calls = 0;
        var m = typeof(M2Targets).GetMethod(nameof(M2Targets.Log))!;

        Wave.Patch(m, "t", prefix: Prefix_Skip);
        M2Targets.Log("hello");

        Assert.Equal(0, M2Targets.Calls);   // original did NOT run
        Assert.Equal("", M2Targets.LastMessage);
    }

    static void Postfix_AfterSkip(ref string __result)
    {
        s_seenResultString = __result;
    }

    private static string? s_seenResultString;

    [Fact]
    public void Patch_PrefixFalse_SkipWithValueReturn_RunsPostfixOnDefault()
    {
        s_seenResultString = null;
        var m = typeof(M2Targets).GetMethod(nameof(M2Targets.Concat))!;

        Wave.Patch(m, "t", prefix: Prefix_Skip, postfix: Postfix_AfterSkip);
        var result = M2Targets.Concat("a", "b");

        Assert.Null(result);              // default(string) when skipped
        Assert.Null(s_seenResultString);  // postfix ran with default result
    }

    // ===================================================== void + observer only

    static void Postfix_Log(string message)
    {
        s_seenA = message.Length;
    }

    [Fact]
    public void Patch_VoidMethod_PostfixRuns()
    {
        s_seenA = 0;
        var m = typeof(M2Targets).GetMethod(nameof(M2Targets.Log))!;

        Wave.Patch(m, "t", postfix: Postfix_Log);
        M2Targets.Log("hello");

        Assert.Equal(5, s_seenA);
        Assert.Equal(1, M2Targets.Calls);
    }

    // ===================================================== long + string types

    static void Postfix_MulLong(ref long __result)
    {
        s_seenResultLong = __result;
        __result += 1;
    }

    private static long s_seenResultLong;

    [Fact]
    public void Patch_LongResult_RewriteWorks()
    {
        s_seenResultLong = 0;
        var m = typeof(M2Targets).GetMethod(nameof(M2Targets.MulLong))!;

        Wave.Patch(m, "t", postfix: Postfix_MulLong);
        var result = M2Targets.MulLong(3_000_000_000L, 2L);

        Assert.Equal(6_000_000_000L, s_seenResultLong);
        Assert.Equal(6_000_000_001L, result);
    }

    // ===================================================== chains + unhook

    [Fact]
    public void Patch_MultipleOwners_BothRun()
    {
        var order = new List<string>();
        var m = typeof(M2Targets).GetMethod(nameof(M2Targets.Log))!;

        
        void Post1(string message) => order.Add($"post1:{message}");
        void Post2(string message) => order.Add($"post2:{message}");

        Wave.Patch(m, "a", postfix: Post1);
        Wave.Patch(m, "b", postfix: Post2);
        M2Targets.Log("x");

        // Postfixes unwind newest-first.
        Assert.Equal(new[] { "post2:x", "post1:x" }, order);

        Wave.Unpatch(m, "a");
        order.Clear();
        M2Targets.Log("y");
        Assert.Equal(new[] { "post2:y" }, order);
        Assert.True(Wave.IsPatched(m, "b"));
        Assert.False(Wave.IsPatched(m, "a"));
    }

    [Fact]
    public void Patch_UnpatchAll_RestoresOriginal()
    {
        M2Targets.Calls = 0;
        var m = typeof(M2Targets).GetMethod(nameof(M2Targets.Add))!;
        Wave.Patch(m, "a", postfix: (ref int __result) => { __result += 1; });
        Wave.Patch(m, "b", postfix: (ref int __result) => { __result += 1; });

        Assert.Equal(44, M2Targets.Add(20, 22));
        Wave.UnpatchAll("a");
        Assert.Equal(43, M2Targets.Add(20, 22));
        Wave.UnpatchAll("b");
        Assert.Equal(42, M2Targets.Add(20, 22));
        Assert.False(Wave.IsPatched(m, "b"));
    }

    // ===================================================== Hook + Patch compose

    [Fact]
    public void HookAndPatch_OnSameMethod_Compose()
    {
        var m = typeof(M2Targets).GetMethod(nameof(M2Targets.Ping))!;
        var order = new List<string>();

        // Postfix forces the ILCopy strategy; the Hook observer joins the same chain
        // as a void prefix (Hook keeps its contract: runs pre-original, LIFO).
        Wave.Hook(m, "m1", observer: () => order.Add("hook"));
        Wave.Patch(m, "m2", postfix: () => order.Add("patch"));
        Assert.Equal(WavePatchEngine.ILCopy, Wave.GetPatchEngine(m));

        M2Targets.Ping();
        Assert.Equal(new[] { "hook", "patch" }, order);
        Assert.True(Wave.IsHooked(m, "m1"));
        Assert.True(Wave.IsPatched(m, "m2"));

        // Removing one owner rebuilds for the rest; removing both restores.
        Wave.Unpatch(m, "m2");
        Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(m));
        order.Clear();
        M2Targets.Ping();
        Assert.Equal(new[] { "hook" }, order);

        Wave.Unhook(m, "m1");
        Assert.Equal(WavePatchEngine.None, Wave.GetPatchEngine(m));
    }
}
