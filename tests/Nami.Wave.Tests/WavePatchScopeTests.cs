using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nami.Wave;
using Nami.Wave.Internal;

namespace Nami.Wave.Tests;

// Scope extensions: closed generics, struct instance receivers, filter EH clauses,
// tiny-method near detours, managed + unmanaged calli. Open generic definitions stay
// refused (no machine code exists to detour).
public class WavePatchScopeTests : IDisposable
{
    public WavePatchScopeTests()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
        s_seen = 0;
        s_result = 0;
    }

    public void Dispose()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
    }

    private static int s_seen;
    private static int s_result;

    private sealed class Box<T>
    {
        public T Value = default!;
        public int Adds;
        public void Add(T x)
        {
            Value = x;
            Adds++;
        }
    }

    static void Prefix_AddObj(int x) => s_seen += x;

    [Fact]
    public void Patch_ClosedGenericClassMethod()
    {
        var m = typeof(Box<int>).GetMethod(nameof(Box<int>.Add))!;
        var box = new Box<int>();

        Wave.Patch(m, "t", prefix: (Action<int>)Prefix_AddObj);
        box.Add(41);

        Assert.Equal(41, s_seen);
        Assert.Equal(41, box.Value);
        Assert.Equal(1, box.Adds);
        Assert.True(Wave.IsPatched(m, "t"));
        Wave.Unpatch(m, "t");
        Assert.False(Wave.IsPatched(m, "t"));
        box.Add(7);
        Assert.Equal(7, box.Value);
    }

    private static T Echo<T>(T x) => x;

    [Fact]
    public void Patch_ClosedGenericMethod()
    {
        var m = typeof(WavePatchScopeTests).GetMethod(nameof(Echo),
            BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(int));

        Wave.Patch(m, "t", postfix: (ActionRef<int>)Postfix_Echo);
        Assert.Equal(43, CallEcho(42));
        Wave.Unpatch(m, "t");
        Assert.Equal(42, CallEcho(42));
    }

    private delegate void ActionRef<T>(ref T result);

    static void Postfix_Echo(ref int __result) => __result += 1;

    private static int CallEcho(int x) =>
        (int)typeof(WavePatchScopeTests).GetMethod(nameof(Echo),
            BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int))
            .Invoke(null, new object[] { x })!;

    [Fact]
    public void Patch_OpenGenericDefinitionRefuses()
    {
        var open = typeof(WavePatchScopeTests).GetMethod(nameof(Echo),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True(open.IsGenericMethodDefinition);
        var ex = Assert.Throws<Wave.HookException>(() =>
            Wave.Patch(open, "t", prefix: (Action<int>)Prefix_AddObj));
        Assert.Contains("MakeGenericMethod", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Patch_OpenGenericTypeRefuses()
    {
        var open = typeof(Box<>).GetMethod(nameof(Box<int>.Add))!;
        var ex = Assert.Throws<Wave.HookException>(() =>
            Wave.Patch(open, "t", prefix: (Action<int>)Prefix_AddObj));
        Assert.Contains("MakeGenericType", ex.Message, StringComparison.Ordinal);
    }

    static void Postfix_EchoStr(ref string __result) => __result += "!";

    private static string CallEchoStr(string x) =>
        (string)typeof(WavePatchScopeTests).GetMethod(nameof(Echo),
            BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(string))
            .Invoke(null, new object[] { x })!;

    [Fact]
    public void Patch_GenericDefinitionHelper()
    {
        var open = typeof(WavePatchScopeTests).GetMethod(nameof(Echo),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var closed = Wave.Patch(open, new[] { typeof(string) }, "t",
            postfix: (ActionRef<string>)Postfix_EchoStr);
        Assert.Equal("42!", CallEchoStr("42"));
        Wave.Unpatch(closed, "t");
        Assert.Equal("42", CallEchoStr("42"));
    }

    [Fact]
    public void Patch_GenericDefinitionHelperRefusesNonGeneric()
    {
        var plain = typeof(WavePatchScopeTests).GetMethod(nameof(Tiny),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Throws<ArgumentException>(() =>
            Wave.Patch(plain, new[] { typeof(int) }, "t", postfix: (FilterPostfix)Postfix_Tiny));
    }

    private struct Counter
    {
        public int N;
        public void Inc(int d) => N += d;
    }

    static int s_hpBefore;
    static void Prefix_Counter(Counter __instance, int d) => s_hpBefore = __instance.N;

    [Fact]
    public void Patch_StructInstanceMethod()
    {
        var m = typeof(Counter).GetMethod(nameof(Counter.Inc))!;
        var c = new Counter { N = 10 };

        Wave.Patch(m, "t", prefix: (StructPrefix)Prefix_Counter);
        c.Inc(5);

        Assert.Equal(10, s_hpBefore); // prefix observed the receiver value
        Assert.Equal(15, c.N);        // original semantics preserved (byref this)
        Wave.Unpatch(m, "t");
        c.Inc(1);
        Assert.Equal(16, c.N);
    }

    private delegate void StructPrefix(Counter __instance, int d);

    private static int MightThrow(int x) => x == 0 ? throw new InvalidOperationException("boom") : x * 2;

    private static int FilterTarget(int x)
    {
        try
        {
            return MightThrow(x);
        }
        catch (Exception) when (x > 0)
        {
            return -1;
        }
    }

    private static int CallFilter(int x) =>
        (int)typeof(WavePatchScopeTests).GetMethod(nameof(FilterTarget),
            BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { x })!;

    private delegate void FilterPostfix(ref int __result);

    static void Postfix_Filter(ref int __result) => s_result = __result;

    [Fact]
    public void Patch_FilterClauseSemantics()
    {
        var m = typeof(WavePatchScopeTests).GetMethod(nameof(FilterTarget),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Wave.Patch(m, "t", postfix: (FilterPostfix)Postfix_Filter);

        Assert.Equal(84, CallFilter(42));   // no throw: normal path + postfix ran
        Assert.Equal(84, s_result);
        Assert.Throws<TargetInvocationException>(() => CallFilter(0)); // filter false → propagates
        Wave.Unpatch(m, "t");
        Assert.Equal(84, CallFilter(42));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Tiny(int x) => x + 1;

    [Fact]
    public void Patch_TinyMethod()
    {
        var m = typeof(WavePatchScopeTests).GetMethod(nameof(Tiny),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Wave.Patch(m, "t", postfix: (FilterPostfix)Postfix_Tiny);
        Assert.Equal(43, Tiny(42));
        Assert.Equal(43, s_result);
        Wave.Unpatch(m, "t");
        Assert.Equal(43, Tiny(42));
    }

    static void Postfix_Tiny(ref int __result) => s_result = __result;

    private static int ManagedAddOne(int x) => x + 1;

    private static unsafe int CallManaged(int x)
    {
        delegate* managed<int, int> p = &ManagedAddOne;
        return p(x);
    }

    [UnmanagedCallersOnly]
    private static int UnmanagedAddOne(int x) => x + 1;

    private static unsafe int CallUnmanaged(int x)
    {
        delegate* unmanaged<int, int> p = &UnmanagedAddOne;
        return p(x);
    }

    [Fact]
    public void Patch_ManagedCalli()
    {
        var m = typeof(WavePatchScopeTests).GetMethod(nameof(CallManaged),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Wave.Patch(m, "t", postfix: (FilterPostfix)Postfix_Tiny);
        Assert.Equal(43, CallManaged(42));
        Assert.Equal(43, s_result);
        Wave.Unpatch(m, "t");
        Assert.Equal(43, CallManaged(42));
    }

    [Fact]
    public void Patch_UnmanagedCalli()
    {
        var m = typeof(WavePatchScopeTests).GetMethod(nameof(CallUnmanaged),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Wave.Patch(m, "t", postfix: (FilterPostfix)Postfix_Tiny);
        Assert.Equal(43, CallUnmanaged(42));
        Assert.Equal(43, s_result);
        Wave.Unpatch(m, "t");
        Assert.Equal(43, CallUnmanaged(42));
    }

    [Fact]
    public void CalliSignature_ManagedShape()
    {
        var sig = CalliSignatureParser.ParseBlob(typeof(WavePatchScopeTests).Module,
            new byte[] { 0x00, 0x01, 0x08, 0x08 }, Type.EmptyTypes, Type.EmptyTypes);
        Assert.False(sig.Unmanaged);
        Assert.Equal(typeof(int), sig.ReturnType);
        Assert.Equal(new[] { typeof(int) }, sig.ParameterTypes);
    }

    [Fact]
    public void CalliSignature_UnmanagedDefaultShape()
    {
        // 0x09: what Roslyn emits for a bare `unmanaged` fnptr.
        var sig = CalliSignatureParser.ParseBlob(typeof(WavePatchScopeTests).Module,
            new byte[] { 0x09, 0x01, 0x08, 0x08 }, Type.EmptyTypes, Type.EmptyTypes);
        Assert.True(sig.Unmanaged);
        Assert.Equal(typeof(int), sig.ReturnType);
        Assert.Equal(new[] { typeof(int) }, sig.ParameterTypes);
    }

    [Fact]
    public void CalliSignature_RefusesVararg()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CalliSignatureParser.ParseBlob(typeof(WavePatchScopeTests).Module,
                new byte[] { 0x05, 0x02, 0x08, 0x08, 0x41 }, Type.EmptyTypes, Type.EmptyTypes));
        Assert.Contains("SENTINEL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CalliSignature_RefusesNestedFnptr()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CalliSignatureParser.ParseBlob(typeof(WavePatchScopeTests).Module,
                new byte[] { 0x00, 0x00, 0x1A }, Type.EmptyTypes, Type.EmptyTypes));
        Assert.Contains("function-pointer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CalliSignature_RefusesUnknownCallConv()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CalliSignatureParser.ParseBlob(typeof(WavePatchScopeTests).Module,
                new byte[] { 0x07, 0x00, 0x01 }, Type.EmptyTypes, Type.EmptyTypes));
        Assert.Contains("0x07", ex.Message, StringComparison.Ordinal);
    }

    private delegate int IntFunc(int x);

    [Fact]
    public unsafe void RawMemory_NearAllocIsWithin2GB()
    {
        var target = (void*)Marshal.GetFunctionPointerForDelegate((IntFunc)(x => x)).ToPointer();
        var p = RawMemory.TryAllocExecutableNear(target, 64);
        Assert.NotEqual(IntPtr.Zero, (IntPtr)p);
        try
        {
            var dist = (long)p - (long)target;
            Assert.True(dist > -0x7F000000L && dist < 0x7F000000L);
        }
        finally
        {
            RawMemory.FreeExecutable(p, 0);
        }
    }

    [Fact]
    public unsafe void Install_KeepsTargetPageExecutable()
    {
        // ponytail: VirtualProtect(RW) on the patch site DEP-faults when the target shares
        // its page with live JIT code (e.g. Install's own Emit frame); must stay RWX.
        var m = typeof(WavePatchScopeTests).GetMethod(nameof(Tiny),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        RuntimeHelpers.PrepareMethod(m.MethodHandle);
        var addr = NativeInterop.GetCodeAddress(m);
        Assert.NotEqual(IntPtr.Zero, addr);
        var detour = Detour.TryCreate(addr);
        Assert.NotNull(detour);
        detour.Retarget(addr); // never called - Install only writes the jump
        detour.Install();
        try
        {
            Assert.True(RawMemory.IsExecutableCode((void*)addr));
        }
        finally
        {
            detour.Dispose();
        }
    }
}
