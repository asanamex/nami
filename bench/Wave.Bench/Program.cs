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

        foreach (var f in failures)
        {
            Console.Error.WriteLine($"bench gate failed: {f}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CallTick() => BenchTarget.Tick();

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
