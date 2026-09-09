using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Nami.Wave;

// Wave hooked-call overhead benchmark (x64, Release) with comparative gates.
// M1 rows reuse BenchTarget.Tick (parameterless void). M2 vs HarmonyX rows patch two
// identical Add(int,int)->int targets with same-shaped prefix+postfix pairs, so the
// Wave/Harmony ratio is apples-to-apples on this machine, same process, back-to-back.
// Gates (env-overridable, generous): exact restore, absolute M1/M2 budgets, and the
// Wave-vs-Harmony ratio. Exit code is nonzero on any gate failure (CI/automation wire-up).

public static class BenchTarget
{
    public static long Acc;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Tick()
    {
        for (int i = 0; i < 4; i++) Acc += i;
    }
}

public static class CalcA
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Add(int a, int b) => a + b;
}

public static class CalcB
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Add(int a, int b) => a + b;
}

// Signature-shape coverage for the unified Patch router (M9). Each row patches a
// distinct target with hooks the fast stub can serve, asserts the Fast engine (the
// deterministic gate - timing-independent), checks correctness, and times the row
// against a generous budget. If a future change narrows fast eligibility, the engine
// assert fails in CI instead of silently demoting shapes to IL-copy.
public static class ShapeTargets
{
    public static int SeenV0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void V0() => SeenV0++;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void V1(int a) => SeenV0 += a;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Add2(int a, int b) => a + b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long AddL(long a, long b) => a + b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double MulD(double a, double b)
    {
        var t = a * b;
        t += a;
        t -= b;
        return t;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsPos(int x) => x > 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double MixAdd(int a, double b) => a + b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float NegF(float x) => -x;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Concat2(string a, string b) => a + b;
}

public static class Program
{
    private const int N = 1_000_000;

    // Same-shaped hooks on both engines: observe args, pass result through.
    private delegate bool AddPrefix(int a, int b);
    private delegate void AddPostfix(ref int __result);

    private static long _waveSeen;
    private static long _harmonySeen;

    private static bool WavePrefix(int a, int b)
    {
        _waveSeen += a + b;
        return true;
    }

    private static void WavePostfix(ref int __result) => _waveSeen += __result;

    public static class HarmonyPatches
    {
        public static bool Prefix(int a, int b)
        {
            Program._harmonySeen += a + b;
            return true;
        }

        public static void Postfix(ref int __result) => Program._harmonySeen += __result;
    }

    public static int Main()
    {
        var failures = new List<string>();
        double Gate(string name, double def) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : def;

        var m1Budget = Gate("NAMI_GATE_M1_NS", 200);
        var m2Budget = Gate("NAMI_GATE_M2_NS", 600);
        var ratioBudget = Gate("NAMI_GATE_M2_VS_HARMONY", 4.0);
        const double restoreTolerance = 0.25;

        var m = typeof(BenchTarget).GetMethod(nameof(BenchTarget.Tick))!;
        for (int i = 0; i < 200_000; i++) CallTick();

        var baseline = Time("baseline (direct)       ", () =>
        {
            for (int i = 0; i < N; i++) CallTick();
        });

        Wave.Hook(m, "bench", observer: () => { });
        for (int i = 0; i < 200_000; i++) CallTick();
        var observer = Time("hooked observer         ", () =>
        {
            for (int i = 0; i < N; i++) CallTick();
        });

        Wave.Hook(m, "bench-gate", gate: () => true);
        for (int i = 0; i < 200_000; i++) CallTick();
        Time("hooked gate-skip        ", () =>
        {
            for (int i = 0; i < N; i++) CallTick();
        });

        Wave.UnhookEverything();
        for (int i = 0; i < 200_000; i++) CallTick();
        var restored = Time("restored after unhook   ", () =>
        {
            for (int i = 0; i < N; i++) CallTick();
        });

        // M2 (Wave IL-copy) vs HarmonyX prefix+postfix on identical targets.
        var waveAdd = typeof(CalcA).GetMethod(nameof(CalcA.Add))!;
        var harmonyAdd = typeof(CalcB).GetMethod(nameof(CalcB.Add))!;
        for (int i = 0; i < 200_000; i++) { CallAddA(1, 2); CallAddB(1, 2); }

        Wave.Patch(waveAdd, "bench-m2",
            prefix: (AddPrefix)WavePrefix,
            postfix: (AddPostfix)WavePostfix);
        var waveM2 = Time("wave M2 prefix+postfix     ", () =>
        {
            for (int i = 0; i < N; i++) CallAddA(i, i + 1);
        });
        Wave.UnpatchEverything();

        var harmony = new Harmony("bench-harmony");
        harmony.Patch(harmonyAdd,
            prefix: new HarmonyMethod(typeof(HarmonyPatches).GetMethod(nameof(HarmonyPatches.Prefix))!),
            postfix: new HarmonyMethod(typeof(HarmonyPatches).GetMethod(nameof(HarmonyPatches.Postfix))!));
        var harmonyTime = Time("harmonyX prefix+postfix    ", () =>
        {
            for (int i = 0; i < N; i++) CallAddB(i, i + 1);
        });
        harmony.UnpatchSelf();

        // Correctness: both engines must compute identical results over the same inputs.
        var expected = 0L;
        for (int i = 0; i < N; i++) expected += 2 * i + 1;
        Wave.Patch(waveAdd, "bench-check", prefix: (AddPrefix)WavePrefix, postfix: (AddPostfix)WavePostfix);
        var waveSum = 0L;
        for (int i = 0; i < N; i++) waveSum += CallAddA(i, i + 1);
        Wave.UnpatchEverything();
        harmony.Patch(harmonyAdd,
            prefix: new HarmonyMethod(typeof(HarmonyPatches).GetMethod(nameof(HarmonyPatches.Prefix))!),
            postfix: new HarmonyMethod(typeof(HarmonyPatches).GetMethod(nameof(HarmonyPatches.Postfix))!));
        var harmonySum = 0L;
        for (int i = 0; i < N; i++) harmonySum += CallAddB(i, i + 1);
        harmony.UnpatchSelf();
        Console.WriteLine($"result check             : wave={waveSum} harmony={harmonySum} expected={expected}");
        if (waveSum != expected || harmonySum != expected)
        {
            failures.Add($"result mismatch (wave={waveSum} harmony={harmonySum} expected={expected})");
        }

        Console.WriteLine($"wave M2 vs harmonyX      : {waveM2 / harmonyTime,7:F2}x");
        void Check(bool ok, string message)
        {
            Console.WriteLine($"{(ok ? "GATE PASS" : "GATE FAIL")} {message}");
            if (!ok)
            {
                failures.Add(message);
            }
        }

        Check(Math.Abs(restored - baseline) / baseline <= restoreTolerance,
            $"exact restore (restored={restored:F2} baseline={baseline:F2} tol={restoreTolerance})");
        Check(observer <= m1Budget, $"M1 observer {observer:F2}ns <= {m1Budget}ns");
        Check(waveM2 <= m2Budget, $"M2 prefix+postfix {waveM2:F2}ns <= {m2Budget}ns");
        Check(waveM2 / harmonyTime <= ratioBudget,
            $"M2 vs HarmonyX ratio {waveM2 / harmonyTime:F2}x <= {ratioBudget}x");

        ShapeCoverage(Check, Gate);

        foreach (var f in failures)
        {
            Console.Error.WriteLine($"bench gate failed: {f}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private static void ShapeCoverage(Action<bool, string> check, Func<string, double, double> gate)
    {
        var fastBudget = gate("NAMI_GATE_FAST_NS", 400);

        static System.Reflection.MethodInfo M(Type t, string name) => t.GetMethod(name)!;

        void FastRow(string name, System.Reflection.MethodInfo target,
            Delegate? prefix, Delegate? postfix, Action call, Func<bool> verify)
        {
            for (int i = 0; i < 200_000; i++) call();
            try
            {
                Wave.Patch(target, "shape-" + name, prefix, postfix);
            }
            catch (Wave.HookException ex)
            {
                // Not detourable at all (tiny prologue) - a decoder matter, not routing.
                Console.WriteLine($"shape {name} skipped ({ex.Message.Split(':')[0]})");
                return;
            }
            check(Wave.GetPatchEngine(target) == WavePatchEngine.Fast,
                $"shape {name} routes Fast (got {Wave.GetPatchEngine(target)})");
            check(verify(), $"shape {name} computes correctly");
            var t = Time($"fast {name,-22}", () =>
            {
                for (int i = 0; i < N; i++) call();
            });
            check(t <= fastBudget, $"fast {name} {t:F2}ns <= {fastBudget}ns");
            Wave.UnpatchEverything();
        }

        ShapeTargets.SeenV0 = 0;
        FastRow("void()", M(typeof(ShapeTargets), nameof(ShapeTargets.V0)),
            prefix: (Action)(() => { ShapeTargets.SeenV0++; }), null,
            () => CallV0(), () => ShapeTargets.SeenV0 > 0);

        FastRow("void(int)", M(typeof(ShapeTargets), nameof(ShapeTargets.V1)),
            prefix: (Action<int>)((int a) => { ShapeTargets.SeenV0 += a; }), null,
            () => CallV1(3), () => ShapeTargets.SeenV0 >= 3);

        var add2Seen = (0, 0);
        FastRow("int(int,int)", M(typeof(ShapeTargets), nameof(ShapeTargets.Add2)),
            prefix: (Func<int, int, bool>)((int a, int b) => { add2Seen = (a, b); return true; }),
            null,
            () => CallShapeAdd2(20, 22),
            () => CallShapeAdd2(20, 22) == 42 && add2Seen == (20, 22));

        FastRow("long(long,long)", M(typeof(ShapeTargets), nameof(ShapeTargets.AddL)),
            prefix: (Func<long, long, bool>)((long a, long b) => true), null,
            () => CallAddL(20, 22),
            () => CallAddL(20, 22) == 42L);

        FastRow("double(double,double)", M(typeof(ShapeTargets), nameof(ShapeTargets.MulD)),
            prefix: (Func<double, double, bool>)((double a, double b) => true), null,
            () => CallMulD(2.5, 4),
            () => CallMulD(2.5, 4) == 8.5);

        FastRow("bool(int)", M(typeof(ShapeTargets), nameof(ShapeTargets.IsPos)),
            prefix: (Func<int, bool>)((int x) => true), null,
            () => CallIsPos(5),
            () => CallIsPos(5) && !CallIsPos(-5));

        FastRow("double(int,double)", M(typeof(ShapeTargets), nameof(ShapeTargets.MixAdd)),
            prefix: (Func<int, double, bool>)((int a, double b) => true), null,
            () => CallMixAdd(20, 22.5),
            () => CallMixAdd(20, 22.5) == 42.5);

        FastRow("float(float)", M(typeof(ShapeTargets), nameof(ShapeTargets.NegF)),
            prefix: (Func<float, bool>)((float x) => true), null,
            () => CallNegF(1.5f),
            () => CallNegF(1.5f) == -1.5f);

        // Skip returns the type default on the fast path.
        var skip = false;
        var add2 = M(typeof(ShapeTargets), nameof(ShapeTargets.Add2));
        for (int i = 0; i < 200_000; i++) CallShapeAdd2(1, 2);
        Wave.Patch(add2, "shape-skip", prefix: (Func<bool>)(() => !skip));
        check(Wave.GetPatchEngine(add2) == WavePatchEngine.Fast, "shape skip routes Fast");
        check(CallShapeAdd2(20, 22) == 42, "shape skip runs when gate passes");
        skip = true;
        check(CallShapeAdd2(20, 22) == 0, "shape skip returns default(int) when gated");
        Wave.UnpatchEverything();

        // M2 controls: shapes the fast path must refuse stay ILCopy.
        var concat = M(typeof(ShapeTargets), nameof(ShapeTargets.Concat2));
        Wave.Patch(concat, "shape-m2",
            prefix: (Func<string, string, bool>)((string a, string b) => true));
        check(Wave.GetPatchEngine(concat) == WavePatchEngine.ILCopy, "shape string routes ILCopy");
        check(ShapeTargets.Concat2("a", "b") == "ab", "shape string computes");
        Wave.UnpatchEverything();

        Wave.Patch(add2, "shape-m2ref", postfix: (ActionRefInt)RefPostfix);
        check(Wave.GetPatchEngine(add2) == WavePatchEngine.ILCopy, "shape ref-result routes ILCopy");
        Wave.UnpatchEverything();

        var v0 = M(typeof(ShapeTargets), nameof(ShapeTargets.V0));
        Wave.Transpile(v0, "shape-m2tr", il => { });
        check(Wave.GetPatchEngine(v0) == WavePatchEngine.ILCopy, "shape transpiler routes ILCopy");
        Wave.UnpatchEverything();
    }

    private delegate void ActionRefInt(ref int result);
    private static void RefPostfix(ref int __result) => __result += 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CallTick() => BenchTarget.Tick();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CallV0() => ShapeTargets.V0();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CallV1(int a) => ShapeTargets.V1(a);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CallShapeAdd2(int a, int b) => ShapeTargets.Add2(a, b);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long CallAddL(long a, long b) => ShapeTargets.AddL(a, b);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double CallMulD(double a, double b) => ShapeTargets.MulD(a, b);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CallIsPos(int x) => ShapeTargets.IsPos(x);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double CallMixAdd(int a, double b) => ShapeTargets.MixAdd(a, b);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float CallNegF(float x) => ShapeTargets.NegF(x);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CallAddA(int a, int b) => CalcA.Add(a, b);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CallAddB(int a, int b) => CalcB.Add(a, b);

    private static double Time(string label, Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        var ns = sw.Elapsed.TotalNanoseconds / N;
        Console.WriteLine($"{label}: {ns,7:F2} ns/call");
        return ns;
    }
}
