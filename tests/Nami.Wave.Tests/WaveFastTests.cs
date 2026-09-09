using System.Reflection;
using System.Runtime.InteropServices;
using Nami.Wave;

namespace Nami.Wave.Tests;

// Missing-rung tests for fast-path argument delivery (HEAD a15dae7 + fast-path work):
// prove that spilled register values reach observing hooks EXACTLY - with value
// assertions, not just "it ran". Tier 1 is pure managed (layout <-> reads agreement);
// tier 2 fills the block from real GP registers in native code, then reads it back
// through the real invoker. Neither installs any detour or stub.

public class WaveFastTests
{
    private static MethodInfo M(string name, Type t) => t.GetMethod(name)!;

    // ------------------------------------------------ tier 1: layout <-> reads

    [Fact]
    public void BlockDelivery_Arity1_ExactValue()
    {
        int seen = int.MinValue;
        var target = M(nameof(ArityTargets.One), typeof(ArityTargets));
        Func<int, bool> hook = a => { seen = a; return true; };
        var shape = WaveFast.AnalyzeShape(target)!;
        var binding = WaveFast.BindHook(hook, target.GetParameters(), new[] { typeof(int) }, isPrefix: true)!;
        var inv = WaveFast.MakeInvoker(hook, shape, binding);
        unsafe
        {
            ulong* raw = stackalloc ulong[4] { 11, 0, 0, 0 };
            int vote = inv(hook, (IntPtr)raw);
            Assert.Equal(0, vote);
            Assert.Equal(11, seen);
        }
    }

    [Fact]
    public void BlockDelivery_Arity2_ExactValues()
    {
        int seenA = int.MinValue, seenB = int.MinValue;
        var target = M(nameof(ArityTargets.Two), typeof(ArityTargets));
        Func<int, int, bool> hook = (a, b) => { seenA = a; seenB = b; return true; };
        var shape = WaveFast.AnalyzeShape(target)!;
        var binding = WaveFast.BindHook(hook, target.GetParameters(), new[] { typeof(int), typeof(int) }, isPrefix: true)!;
        var inv = WaveFast.MakeInvoker(hook, shape, binding);
        unsafe
        {
            ulong* raw = stackalloc ulong[4] { 20, 22, 0, 0 };
            int vote = inv(hook, (IntPtr)raw);
            Assert.Equal(0, vote);
            Assert.Equal(20, seenA);
            Assert.Equal(22, seenB);
        }
    }

    [Fact]
    public void BlockDelivery_Arity3_ExactValues()
    {
        long seenA = 0; int seenB = 0; int seenC = 0;
        var target = M(nameof(ArityTargets.Three), typeof(ArityTargets));
        Func<long, int, int, bool> hook = (a, b, c) => { seenA = a; seenB = b; seenC = c; return false; };
        var shape = WaveFast.AnalyzeShape(target)!;
        var binding = WaveFast.BindHook(hook, target.GetParameters(),
            new[] { typeof(long), typeof(int), typeof(int) }, isPrefix: true)!;
        var inv = WaveFast.MakeInvoker(hook, shape, binding);
        unsafe
        {
            ulong* raw = stackalloc ulong[4] { 0x123456789ABCDEF0UL, 7, 9, 0 };
            int vote = inv(hook, (IntPtr)raw);
            Assert.Equal(1, vote); // false = skip
            Assert.Equal(0x123456789ABCDEF0L, seenA); // full 64-bit slot, not truncated
            Assert.Equal(7, seenB);
            Assert.Equal(9, seenC);
        }
    }

    [Fact]
    public void BlockDelivery_Arity4_ExactValues()
    {
        int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        var target = M(nameof(ArityTargets.Four), typeof(ArityTargets));
        Func<int, int, int, int, bool> hook = (a, b, c, d) => { s0 = a; s1 = b; s2 = c; s3 = d; return true; };
        var shape = WaveFast.AnalyzeShape(target)!;
        var binding = WaveFast.BindHook(hook, target.GetParameters(),
            new[] { typeof(int), typeof(int), typeof(int), typeof(int) }, isPrefix: true)!;
        var inv = WaveFast.MakeInvoker(hook, shape, binding);
        unsafe
        {
            ulong* raw = stackalloc ulong[4] { 1, 2, 3, 4 };
            int vote = inv(hook, (IntPtr)raw);
            Assert.Equal(0, vote);
            Assert.Equal(new[] { 1, 2, 3, 4 }, new[] { s0, s1, s2, s3 });
        }
    }

    // ------------------------------------------------ tier 2: native spill -> managed read

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FillFn(IntPtr block, int a, int b, int c, int d);

    // this[i] = i-th GP register value, written from real registers in native code:
    // block=rcx, a=edx, b=r8, c=r9, d=[rsp+40] (first stack slot at callee entry).
    private static readonly byte[] FillCode = new byte[]
    {
        0x48, 0x89, 0x11,       // mov [rcx+0],rdx
        0x4C, 0x89, 0x41, 0x08, // mov [rcx+8],r8
        0x4C, 0x89, 0x49, 0x10, // mov [rcx+16],r9
        0x48, 0x8B, 0x44, 0x24, 0x28, // mov rax,[rsp+40]
        0x48, 0x89, 0x41, 0x18, // mov [rcx+24],rax
        0xC3,                   // ret
    };

    [Fact]
    public void NativeSpill_ManagedRead_ExactValues()
    {
        int s0 = -1, s1 = -1, s2 = -1, s3 = -1;
        var target = M(nameof(ArityTargets.Four), typeof(ArityTargets));
        Func<int, int, int, int, bool> hook = (a, b, c, d) => { s0 = a; s1 = b; s2 = c; s3 = d; return true; };
        var shape = WaveFast.AnalyzeShape(target)!;
        var binding = WaveFast.BindHook(hook, target.GetParameters(),
            new[] { typeof(int), typeof(int), typeof(int), typeof(int) }, isPrefix: true)!;
        var inv = WaveFast.MakeInvoker(hook, shape, binding);

        unsafe
        {
            var exec = (byte*)Nami.Wave.Internal.RawMemory.AllocExecutable((nuint)FillCode.Length);
            for (int i = 0; i < FillCode.Length; i++)
            {
                exec[i] = FillCode[i];
            }
            Nami.Wave.Internal.RawMemory.FlushCode(exec, (nuint)FillCode.Length);
            Nami.Wave.Internal.RawMemory.MakeExecutable(exec, (nuint)FillCode.Length);
            try
            {
                var fill = (FillFn)Marshal.GetDelegateForFunctionPointer((IntPtr)exec, typeof(FillFn));
                var block = new ulong[4];
                fixed (ulong* pBlock = block)
                {
                    // Fill from REAL registers (10->edx, 20->r8, 30->r9, 40->stack)...
                    fill((IntPtr)pBlock, 10, 20, 30, 40);
                    // ...read back through the REAL invoker, like FastPre does.
                    int vote = inv(hook, (IntPtr)pBlock);
                    Assert.Equal(0, vote);
                    Assert.Equal(new[] { 10, 20, 30, 40 }, new[] { s0, s1, s2, s3 });
                }
            }
            finally
            {
                Nami.Wave.Internal.RawMemory.FreeExecutable(exec, (nuint)FillCode.Length);
            }
        }
    }

    // Targets exist only to give the binder real MethodInfos (never called/patched).
    private static class ArityTargets
    {
        public static bool One(int a) => a != 0;
        public static bool Two(int a, int b) => a != b;
        public static bool Three(long a, int b, int c) => a != b + c;
        public static bool Four(int a, int b, int c, int d) => a != b + c + d;
    }

    // ------------------------------------------------ live fast-path behavior

    public static class FastLiveTargets
    {
        public static int CallsA;
        public static int CallsB;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static int AddA(int a, int b)
        {
            CallsA++;
            return a + b;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static int AddB(int a, int b)
        {
            CallsB++;
            return a + b;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static int PureAdd(int a, int b) => a + b;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining |
            System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
        public static int PureAddNoOpt(int a, int b) => a + b;
    }

    public class WaveFastLiveTests : IDisposable
    {
        public WaveFastLiveTests()
        {
            Wave.UnpatchEverything();
            Wave.UnhookEverything();
            FastLiveTargets.CallsA = 0;
            FastLiveTargets.CallsB = 0;
        }

        public void Dispose()
        {
            Wave.UnpatchEverything();
            Wave.UnhookEverything();
            FastLiveTargets.CallsA = 0;
            FastLiveTargets.CallsB = 0;
        }

        private static MethodInfo M(System.Linq.Expressions.Expression<System.Func<int>> e) =>
            (MethodInfo)((System.Linq.Expressions.MethodCallExpression)e.Body).Method;

        [Fact]
        public void LiveObservingPrefix_SeesExactArgs_RunsOriginal()
        {
            var add = M(() => FastLiveTargets.AddA(0, 0));
            int seenA = -1, seenB = -1;
            Wave.Patch(add, "t", prefix: (int a, int b) => { seenA = a; seenB = b; return true; });

            Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(add));
            Assert.Equal(42, FastLiveTargets.AddA(20, 22));
            Assert.Equal(20, seenA);
            Assert.Equal(22, seenB);
            Assert.Equal(1, FastLiveTargets.CallsA);
        }

        [Fact]
        public void LiveFalseVote_SkipsWithDefault()
        {
            var add = M(() => FastLiveTargets.AddB(0, 0));
            Wave.Patch(add, "t", prefix: (Func<bool>)(() => false));

            Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(add));
            Assert.Equal(0, FastLiveTargets.AddB(20, 22));
            Assert.Equal(0, FastLiveTargets.CallsB); // body (incl. prologue) never ran
        }

        [Fact]
        public void LiveBlindPrefix_RunsOriginal()
        {
            var add = M(() => FastLiveTargets.AddA(0, 0));
            int ran = 0;
            Wave.Patch(add, "t", prefix: () => { ran++; });

            Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(add));
            Assert.Equal(42, FastLiveTargets.AddA(20, 22));
            Assert.Equal(1, ran);
            Assert.Equal(1, FastLiveTargets.CallsA);
        }

        [Fact]
        public void LiveBlindPrefix_AddB_FromHere()
        {
            var m = M(() => FastLiveTargets.AddB(0, 0));
            int ran = 0;
            try
            {
                Wave.Patch(m, "t", prefix: () => { ran++; });
                Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(m));
                Assert.Equal(42, FastLiveTargets.AddB(20, 22));
                Assert.Equal(1, ran);
                Assert.Equal(1, FastLiveTargets.CallsB);
            }
            finally
            {
                Wave.UnpatchEverything();
                FastLiveTargets.CallsB = 0;
            }
        }

        [Fact]
        public void LiveM2_AddB()
        {
            var m = M(() => FastLiveTargets.AddB(0, 0));
            int ran = 0;
            int post = 0;
            try
            {
                Wave.Patch(m, "t", prefix: () => { ran++; }, postfix: () => { post++; });
                Assert.Equal(WavePatchEngine.ILCopy, Wave.GetPatchEngine(m));
                Assert.Equal(42, FastLiveTargets.AddB(20, 22));
                Assert.Equal(1, ran);
                Assert.Equal(1, post);
                Assert.Equal(1, FastLiveTargets.CallsB);
            }
            finally
            {
                Wave.UnpatchEverything();
                FastLiveTargets.CallsB = 0;
            }
        }

        [Fact]
        public void LiveObservingPrefix_NoStaticsTarget()
        {
            var add = M(() => FastLiveTargets.PureAdd(0, 0));
            int seenA = -1, seenB = -1;
            Wave.Patch(add, "t", prefix: (int a, int b) => { seenA = a; seenB = b; return true; });

            Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(add));
            Assert.Equal(42, FastLiveTargets.PureAdd(20, 22));
            Assert.Equal(20, seenA);
            Assert.Equal(22, seenB);
        }

        [Fact]
        public void LiveObservingPrefix_NoOptTarget()
        {
            var add = M(() => FastLiveTargets.PureAddNoOpt(0, 0));
            int seenA = -1, seenB = -1;
            Wave.Patch(add, "t", prefix: (int a, int b) => { seenA = a; seenB = b; return true; });

            Assert.Equal(42, FastLiveTargets.PureAddNoOpt(20, 22));
            Assert.Equal(20, seenA);
            Assert.Equal(22, seenB);
        }

        [Fact]
        public void LiveStaticObservingPrefix()
        {
            var add = M(() => FastLiveTargets.PureAdd(0, 0));
            Wave.Patch(add, "t2", prefix: (int a, int b) => a + b == 42);

            Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(add));
            Assert.Equal(42, FastLiveTargets.PureAdd(20, 22));
        }

        [Fact]
        public void LiveObservingPrefix_SkipVote()
        {
            var add = M(() => FastLiveTargets.PureAdd(0, 0));
            Wave.Patch(add, "t4", prefix: (int a, int b) => false);

            Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(add));
            Assert.Equal(0, FastLiveTargets.PureAdd(20, 22));
        }

        [Fact]
        public void LiveBlindPrefix_PureAdd()
        {
            var add = M(() => FastLiveTargets.PureAdd(0, 0));
            int ran = 0;
            Wave.Patch(add, "t5", prefix: () => { ran++; });

            Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(add));
            Assert.Equal(42, FastLiveTargets.PureAdd(20, 22));
            Assert.Equal(1, ran);
        }

        [Fact]
        public void LiveObservingPrefix_Arity1()
        {
            var add = M(() => FastLiveTargets.PureAdd(0, 0));
            int seen = -1;
            Wave.Patch(add, "t3", prefix: (int a) => { seen = a; return true; });

            Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(add));
            Assert.Equal(42, FastLiveTargets.PureAdd(20, 22));
            Assert.Equal(20, seen);
        }

        [Fact]
        public void LiveHotLoop_StaysStable()
        {
            int n = 1000;
            var env = System.Environment.GetEnvironmentVariable("WAVE_LOOP_N");
            if (int.TryParse(env, out int parsed) && parsed > 0)
            {
                n = parsed;
            }
            var add = M(() => FastLiveTargets.AddB(0, 0));
            int ran = 0;
            Wave.Patch(add, "t", prefix: () => { ran++; });

            Assert.Equal(WavePatchEngine.Fast, Wave.GetPatchEngine(add));
            long sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += FastLiveTargets.AddB(i, 1);
            }
            Assert.Equal(n, ran);
            Assert.Equal(n, FastLiveTargets.CallsB);
            long expected = 0;
            for (int i = 0; i < n; i++)
            {
                expected += i + 1;
            }
            Assert.Equal(expected, sum);
        }
    }
}
