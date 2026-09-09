using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Nami.Wave;

namespace Nami.Wave.Tests;

// Cursor-transpiler targets — realistic bodies with branches, switches and EH regions.

public static class TranspileTargets
{
    public static readonly List<string> Log = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Add(int a, int b) => a + b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Mul(int a, int b) => a * b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Sub(int a, int b) => a - b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int SumTo(int n)
    {
        var s = 0;
        for (var i = 1; i <= n; i++)
        {
            s += i;
        }
        return s;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Describe(int x)
    {
        switch (x)
        {
            case 0: return "zero";
            case 1: return "one";
            case 2: return "two";
            default: return "many";
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Guarded(int x)
    {
        try
        {
            return 100 / x;
        }
        catch (DivideByZeroException)
        {
            return -1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int WithFinally(int x)
    {
        var r = 0;
        try
        {
            r = 10 / x;
        }
        finally
        {
            Log.Add("finally");
        }
        return r;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Noisy(int v)
    {
        Log.Add("noisy");
        return v * 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Gate(int v)
    {
        Log.Add("reached-end");
        return v;
    }

    public static void Record(string s) => Log.Add(s);

    public class Hero
    {
        public int Hp;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int TakeDamage(int amount)
        {
            Hp -= amount;
            return Hp;
        }
    }
}

public class WaveTranspilerTests : IDisposable
{
    private readonly List<WaveTranspilerConflict> _conflictEvents = new();

    public WaveTranspilerTests()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
        TranspileTargets.Log.Clear();
        Wave.TranspilerConflict += OnConflict;
    }

    public void Dispose()
    {
        Wave.TranspilerConflict -= OnConflict;
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
    }

    private void OnConflict(WaveTranspilerConflict c) => _conflictEvents.Add(c);

    private static MethodInfo M(Expression<Func<int>> e) => (MethodInfo)((MethodCallExpression)e.Body).Method;
    private static MethodInfo M(Expression<Action> e) => (MethodInfo)((MethodCallExpression)e.Body).Method;

    // ============================================= replace: opcode-level rewrite

    [Fact]
    public void ReplaceOpcode_ChangesBehavior()
    {
        var add = M(() => TranspileTargets.Add(0, 0));
        Wave.Transpile(add, "test", il =>
        {
            Assert.True(il.Goto(OpCodes.Add));
            il.Replace(OpCodes.Sub);
        });

        Assert.Equal(7, TranspileTargets.Add(10, 3)); // was 13
    }

    [Fact]
    public void CursorExposesProvenance()
    {
        var add = M(() => TranspileTargets.Add(0, 0));
        WaveIlInstruction? seen = null;
        Wave.Transpile(add, "test", il =>
        {
            Assert.True(il.Goto(OpCodes.Add));
            seen = il.Current;
        });

        Assert.NotNull(seen);
        Assert.True(seen!.IsOriginal);
        Assert.Equal(2, seen.OriginalOffset); // ldarg.0(0) ldarg.1(1) add(2) ret(3)
        Assert.Null(seen.CreatedBy);
    }

    // ============================================= insert: branches must survive edits

    [Fact]
    public void InsertInsideLoop_BranchesSurvive()
    {
        var sumTo = M(() => TranspileTargets.SumTo(0));
        Assert.Equal(55, TranspileTargets.SumTo(10));

        // Insert a stack-neutral nop before the loop's add — inside the backward-branch
        // region. A list-index transpiler risks moving the loop's back-edge; labels cannot.
        Wave.Transpile(sumTo, "test", il =>
        {
            Assert.True(il.Goto(OpCodes.Add));
            il.Emit(OpCodes.Nop);
        });

        Assert.Equal(55, TranspileTargets.SumTo(10));
        Assert.Equal(0, TranspileTargets.SumTo(0));
        Assert.Equal(5050, TranspileTargets.SumTo(100));
    }

    [Fact]
    public void InjectEarlyExit_SkipsRestOfBody()
    {
        var gate = M(() => TranspileTargets.Gate(0));

        // At body start: leave the return value on the stack and branch over the
        // "reached-end" record straight to the ret (both paths arrive with [value]).
        Wave.Transpile(gate, "test", il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            var end = il.DefineLabel();
            il.Emit(OpCodes.Br, end);
            Assert.True(il.Goto(OpCodes.Ret));
            il.MarkLabel(end);
        });

        Assert.Equal(3, TranspileTargets.Gate(3));
        Assert.DoesNotContain("reached-end", TranspileTargets.Log);
    }

    [Fact]
    public void InjectedRets_StillRunPostfixes()
    {
        var add = M(() => TranspileTargets.Add(0, 0));
        var seen = int.MinValue;

        Wave.Patch(add, "test", postfix: (Action<int>)((int __result) => seen = __result),
            transpiler: il =>
            {
                // Early return of 42 — an INJECTED ret must still run the postfix chain.
                il.Emit(OpCodes.Ldc_I4, 42);
                il.Emit(OpCodes.Ret);
            });

        Assert.Equal(42, TranspileTargets.Add(10, 3));
        Assert.Equal(42, seen);
    }

    // ============================================= remove: stack-neutral edits

    [Fact]
    public void RemoveCall_DropsSideEffect()
    {
        var noisy = M(() => TranspileTargets.Noisy(0));

        Wave.Transpile(noisy, "test", il =>
        {
            // Log.Add is an instance callvirt: drop the whole sequence (ldsfld, ldstr,
            // callvirt) — removing only the call would leave two pushes behind.
            Assert.True(il.Goto(OpCodes.Callvirt));
            il.Prev();
            il.Prev();
            il.Remove();
            il.Remove();
            il.Remove();
        });

        Assert.Equal(10, TranspileTargets.Noisy(5));
        Assert.DoesNotContain("noisy", TranspileTargets.Log);
    }

    // ============================================= switch + string + method operands

    [Fact]
    public void InsertStringAndCall_SwitchStillWorks()
    {
        var describe = M(() => TranspileTargets.Describe(0));
        var record = M(() => TranspileTargets.Record(""));

        Wave.Transpile(describe, "test", il =>
        {
            il.Emit(OpCodes.Ldstr, "describe-entered");
            il.Emit(OpCodes.Call, record);
        });

        Assert.Equal("zero", TranspileTargets.Describe(0));
        Assert.Contains("describe-entered", TranspileTargets.Log);
        Assert.Equal("one", TranspileTargets.Describe(1));
        Assert.Equal("two", TranspileTargets.Describe(2));
        Assert.Equal("many", TranspileTargets.Describe(9));
    }

    // ============================================= exception blocks survive edits

    [Fact]
    public void InsertInsideTry_CatchStillBinds()
    {
        var guarded = M(() => TranspileTargets.Guarded(0));

        Wave.Transpile(guarded, "test", il =>
        {
            Assert.True(il.Goto(OpCodes.Div));
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Pop);
        });

        Assert.Equal(25, TranspileTargets.Guarded(4));
        Assert.Equal(-1, TranspileTargets.Guarded(0)); // catch still routes here
    }

    [Fact]
    public void InsertInsideTry_FinallyStillRuns()
    {
        var withFinally = M(() => TranspileTargets.WithFinally(0));

        Wave.Transpile(withFinally, "test", il =>
        {
            Assert.True(il.Goto(OpCodes.Div));
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Pop);
        });

        TranspileTargets.Log.Clear();
        Assert.Equal(2, TranspileTargets.WithFinally(5));
        Assert.Contains("finally", TranspileTargets.Log);
    }

    // ============================================= instance targets

    [Fact]
    public void InstanceTarget_Transpiled()
    {
        var takeDamage = typeof(TranspileTargets.Hero).GetMethod(nameof(TranspileTargets.Hero.TakeDamage))!;
        var hero = new TranspileTargets.Hero { Hp = 100 };

        Wave.Transpile(takeDamage, "test", il =>
        {
            Assert.True(il.Goto(OpCodes.Sub));
            il.Replace(OpCodes.Add); // damage heals, proving the IL edit landed
        });

        Assert.Equal(105, hero.TakeDamage(5));
    }

    // ============================================= provenance: conflicts between owners

    [Fact]
    public void OverlappingEdits_RaiseConflict_LaterOwnerWins()
    {
        var add = M(() => TranspileTargets.Add(0, 0));

        Wave.Transpile(add, "modA", il =>
        {
            Assert.True(il.Goto(OpCodes.Add));
            il.Replace(OpCodes.Sub); // original add node edited by modA
        });
        Wave.Transpile(add, "modB", il =>
        {
            Assert.True(il.Goto(OpCodes.Sub)); // finds the node modA rewrote
            il.Replace(OpCodes.Add);           // mutates modA's edit
        });

        // modB's edit of modA-rewritten IL is flagged, never silent.
        var conflict = Assert.Single(_conflictEvents);
        Assert.Equal("modB", conflict.Modifier);
        Assert.Equal("modA", conflict.PriorOwner);
        Assert.Equal("replace", conflict.Edit);
        Assert.Equal(2, conflict.OriginalOffset);
        Assert.Contains(Wave.RecentTranspilerConflicts, c =>
            c.Modifier == "modB" && c.PriorOwner == "modA" && c.OriginalOffset == 2);

        // Deterministic outcome: the later transpiler's rewrite is live (add won back).
        Assert.Equal(13, TranspileTargets.Add(10, 3));
    }

    [Fact]
    public void EditingInjectedIl_RaisesConflict()
    {
        var mul = M(() => TranspileTargets.Mul(0, 0));

        Wave.Transpile(mul, "modA", il =>
        {
            il.Emit(OpCodes.Nop); // modA's injected instruction
        });
        Wave.Transpile(mul, "modB", il =>
        {
            Assert.True(il.Goto(OpCodes.Nop)); // modA's injected IL
            il.Remove();                       // removing another owner's injection
        });

        var conflict = Assert.Single(_conflictEvents);
        Assert.Equal("modB", conflict.Modifier);
        Assert.Equal("modA", conflict.PriorOwner);
        Assert.Null(conflict.OriginalOffset); // injected IL has no original offset
        Assert.Equal(6, TranspileTargets.Mul(2, 3));
    }

    [Fact]
    public void DisjointEdits_NoConflict()
    {
        var sub = M(() => TranspileTargets.Sub(0, 0));
        var before = Wave.RecentTranspilerConflicts.Count;

        // Different owners touching DIFFERENT instructions compose without conflict.
        Wave.Transpile(sub, "modA", il =>
        {
            Assert.True(il.Goto(OpCodes.Sub));
            il.Replace(OpCodes.Add);
        });
        Wave.Transpile(sub, "modB", il =>
        {
            il.Emit(OpCodes.Nop); // disjoint insertion at the body start
        });

        Assert.Equal(before, Wave.RecentTranspilerConflicts.Count);
        Assert.Empty(_conflictEvents);
        Assert.Equal(13, TranspileTargets.Sub(10, 3));
    }

    // ============================================= lifecycle

    [Fact]
    public void Unpatch_TranspilerOnly_RestoresOriginal()
    {
        var add = M(() => TranspileTargets.Add(0, 0));
        Wave.Transpile(add, "test", il =>
        {
            Assert.True(il.Goto(OpCodes.Add));
            il.Replace(OpCodes.Sub);
        });
        Assert.Equal(7, TranspileTargets.Add(10, 3));
        Assert.True(Wave.IsPatched(add, "test"));

        Wave.Unpatch(add, "test");
        Assert.Equal(13, TranspileTargets.Add(10, 3));
        Assert.False(Wave.IsPatched(add, "test"));
    }

    [Fact]
    public void Patch_CombinesPrefixPostfixTranspiler()
    {
        var add = M(() => TranspileTargets.Add(0, 0));
        var postResult = int.MinValue;
        var skip = false;

        Wave.Patch(add, "test",
            prefix: (Func<bool>)(() => !skip),
            postfix: (Action<int>)((int __result) => postResult = __result),
            transpiler: il =>
            {
                Assert.True(il.Goto(OpCodes.Add));
                il.Replace(OpCodes.Mul); // transpiled body: a * b
            });

        // Prefix lets the (transpiled) original run: postfix sees the MULTIPLIED result.
        Assert.Equal(30, TranspileTargets.Add(10, 3));
        Assert.Equal(30, postResult);

        // Prefix skips: the transpiled body never runs; postfix sees the default.
        skip = true;
        Assert.Equal(0, TranspileTargets.Add(10, 3));
        Assert.Equal(0, postResult);
    }

    [Fact]
    public void ThrowingTranspiler_FailsLoudly_LeavesMethodWorking()
    {
        var add = M(() => TranspileTargets.Add(0, 0));

        var ex = Assert.Throws<Wave.HookException>(() =>
            Wave.Transpile(add, "bad", il => throw new InvalidOperationException("boom")));
        Assert.Contains("bad", ex.Message);
        Assert.Contains(add.Name, ex.Message);

        // Rollback: the site is gone and the method behaves as before.
        Assert.False(Wave.IsPatched(add, "bad"));
        Assert.Equal(13, TranspileTargets.Add(10, 3));
    }

    [Fact]
    public void BranchToUnanchoredLabel_FailsLoudly()
    {
        var add = M(() => TranspileTargets.Add(0, 0));

        var ex = Assert.Throws<Wave.HookException>(() =>
            Wave.Transpile(add, "bad", il => il.Emit(OpCodes.Br, il.DefineLabel())));
        Assert.Contains("unanchored", ex.Message);
    }

    [Fact]
    public void BranchOperandMustBeLabel()
    {
        var add = M(() => TranspileTargets.Add(0, 0));

        // Build wraps transpiler failures with owner context; the inner cause is the
        // operand-type error.
        var ex = Assert.Throws<Wave.HookException>(() =>
            Wave.Transpile(add, "bad", il => il.Emit(OpCodes.Br, 5)));
        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains("WaveIlLabel", ex.InnerException.Message);
    }

    [Fact]
    public void Goto_MissDoesNotMoveCursor()
    {
        var add = M(() => TranspileTargets.Add(0, 0));
        Wave.Transpile(add, "test", il =>
        {
            Assert.False(il.Goto(OpCodes.Xor)); // no such opcode in the body
            Assert.True(il.Goto(OpCodes.Add));  // cursor still finds from the start
        });
        Assert.Equal(13, TranspileTargets.Add(10, 3)); // no edits — body copied unchanged
    }
}
