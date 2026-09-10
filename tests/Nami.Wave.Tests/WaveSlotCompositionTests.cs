using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Nami.Wave.Tests;

/// <summary>
/// Slot-composition proofs at the Wave layer: two host slot owners sharing one
/// target compose (both serve), removing one leaves the other intact, and a
/// re-registered callback serves the new generation. The host registers one
/// stable trampoline per slot under these owner ids, so this is the native
/// composition the generation swap relies on. Targets are private to this
/// class so parallel siblings can never observe these hooks.
/// </summary>
public class WaveSlotCompositionTests : IDisposable
{
    public WaveSlotCompositionTests()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
        SlotTarget.Counter = 0;
    }

    public void Dispose()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
    }

    private static MethodInfo M(Expression<Action> e) =>
        (MethodInfo)((MethodCallExpression)e.Body).Method;

    private static class SlotTarget
    {
        public static int Counter;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Slot()
        {
            for (var i = 0; i < 4; i++)
            {
                Counter += i;
            }
        }
    }

    [Fact]
    public void TwoOwners_BothServe_UnhookOne_LeavesOther()
    {
        SlotTarget.Counter = 0;
        var first = 0;
        var second = 0;
        var m = M(() => SlotTarget.Slot());

        Wave.Hook(m, "nami:gen:mod-a:slot", observer: () => first++);
        Wave.Hook(m, "nami:gen:mod-b:slot", observer: () => second++);

        SlotTarget.Slot();
        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(6, SlotTarget.Counter);

        Wave.Unhook(m, "nami:gen:mod-a:slot");
        Assert.False(Wave.IsHooked(m, "nami:gen:mod-a:slot"));
        Assert.True(Wave.IsHooked(m, "nami:gen:mod-b:slot"));

        SlotTarget.Slot();
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(12, SlotTarget.Counter);
    }

    [Fact]
    public void GateSwap_ReregisteredCallback_ServesNewVote()
    {
        SlotTarget.Counter = 0;
        var m = M(() => SlotTarget.Slot());

        // v1 votes to skip: the original does not run.
        Wave.Hook(m, "nami:gen:mod:slot", gate: () => true);
        SlotTarget.Slot();
        Assert.Equal(0, SlotTarget.Counter);

        // Generation swap at the Wave layer: unhook v1, hook v2 voting to run.
        Wave.Unhook(m, "nami:gen:mod:slot");
        Wave.Hook(m, "nami:gen:mod:slot", gate: () => false);
        SlotTarget.Slot();
        Assert.Equal(6, SlotTarget.Counter);
    }

    [Fact]
    public void MixedHookAndPatchEntries_ComposeOnOneTarget()
    {
        SlotTarget.Counter = 0;
        var observed = 0;
        var m = M(() => SlotTarget.Slot());

        Wave.Hook(m, "nami:gen:mod:gate-slot", observer: () => observed++);
        Wave.Patch(m, "nami:gen:mod:prefix-slot", prefix: (Action)(() => observed += 10));

        SlotTarget.Slot();
        Assert.Equal(11, observed);
        Assert.Equal(6, SlotTarget.Counter);

        Wave.Unhook(m, "nami:gen:mod:gate-slot");
        SlotTarget.Slot();
        Assert.Equal(21, observed);
    }
}
