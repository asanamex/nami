using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nami.Wave;

// Wave hooked-call overhead benchmark (x64, Release).
// The loop calls through a [NoInlining] barrier so the JIT cannot OSR-recompile the call
// site mid-run. Iteration count stays in the verified-stable envelope (see docs/wave.md:
// the native-stub dispatch is GC/EH-safe through ~1M calls per hook; a documented edge
// exists in multi-10M tight loops where unwinding crosses the stub's unmanaged frame).

public static class BenchTarget
{
    public static long Acc;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Tick()
    {
        for (int i = 0; i < 4; i++) Acc += i;
    }
}

public static class Program
{
    private const int N = 1_000_000;

    public static void Main()
    {
        var m = typeof(BenchTarget).GetMethod(nameof(BenchTarget.Tick))!;
        for (int i = 0; i < 200_000; i++) CallTick();

        Time("baseline (direct)       ", () =>
        {
            for (int i = 0; i < N; i++) CallTick();
        });

        Wave.Hook(m, "bench", observer: () => { });
        for (int i = 0; i < 200_000; i++) CallTick();
        Time("hooked observer         ", () =>
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
        Time("restored after unhook   ", () =>
        {
            for (int i = 0; i < N; i++) CallTick();
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CallTick() => BenchTarget.Tick();

    private static void Time(string label, Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        Console.WriteLine($"{label}: {sw.Elapsed.TotalNanoseconds / N,7:F2} ns/call");
    }
}
