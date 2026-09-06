using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Nami.Wave;
using Nami.Wave.Internal;

namespace Nami.Wave.Tests;

// ============================================================= copier fixtures

public static class CopyFixtures
{
    public static int Statics;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Fib(int n)
    {
        if (n < 2) return n;
        return Fib(n - 1) + Fib(n - 2);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int SumLoop(int n)
    {
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            total += i;
            if (total > 1000) break;
        }
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TryFinally(int n)
    {
        try
        {
            Statics += n;
        }
        finally
        {
            Statics += 1;
        }
    }

    public class Widget
    {
        public int Value;
        public int Scale(int x) => Value * x;
        public string Describe(string fmt) => string.Format(fmt, Value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Widget Make(int v) => new Widget { Value = v };

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long AddLongs(long a, long b) => a + b;
}

// ============================================================= copier tests

public class IlCopyTests
{
    private static MethodInfo Copy(MethodBase m) => IlRewriter.CopyBody(m, "test_copy");

    private static TDelegate Make<TDelegate>(MethodBase m) where TDelegate : Delegate
        => (TDelegate)Copy(m).CreateDelegate(typeof(TDelegate));

    [Fact]
    public void Copy_PureFunction_Matches()
    {
        var f = Make<Func<int, int>>(typeof(CopyFixtures).GetMethod(nameof(CopyFixtures.Fib))!);
        Assert.Equal(55, f(10)); // fib(10)
    }

    [Fact]
    public void Copy_LoopWithBranch_Matches()
    {
        var f = Make<Func<int, int>>(typeof(CopyFixtures).GetMethod(nameof(CopyFixtures.SumLoop))!);
        Assert.Equal(45, f(10));      // 0..9
        Assert.Equal(1035, f(100));   // hits break > 1000
    }

    [Fact]
    public void Copy_StaticFieldAccess_Matches()
    {
        CopyFixtures.Statics = 0;
        var f = Make<Action<int>>(typeof(CopyFixtures).GetMethod(nameof(CopyFixtures.TryFinally))!);
        f(5);
        Assert.Equal(6, CopyFixtures.Statics);
    }

    [Fact]
    public void Copy_ObjectConstructionAndCall_Matches()
    {
        var make = Make<Func<int, CopyFixtures.Widget>>(typeof(CopyFixtures).GetMethod(nameof(CopyFixtures.Make))!);
        var w = make(7);
        Assert.Equal(7, w.Value);

        // Instance target: the copy is a static-shaped DM (declaring type first).
        var scale = Make<Func<CopyFixtures.Widget, int, int>>(typeof(CopyFixtures.Widget).GetMethod(nameof(CopyFixtures.Widget.Scale))!);
        Assert.Equal(21, scale(w, 3));

        // String-returning instance with a call + ldarg chain.
        var describe = Make<Func<CopyFixtures.Widget, string, string>>(typeof(CopyFixtures.Widget).GetMethod(nameof(CopyFixtures.Widget.Describe))!);
        Assert.Equal("value=7", describe(w, "value={0}"));
    }

    [Fact]
    public void Copy_LongParams_Matches()
    {
        var f = Make<Func<long, long, long>>(typeof(CopyFixtures).GetMethod(nameof(CopyFixtures.AddLongs))!);
        Assert.Equal(4_000_000_000L, f(1_500_000_000L, 2_500_000_000L));
    }
}
