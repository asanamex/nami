using System.Reflection;

namespace Nami.Tests;

/// <summary>
/// Seam proofs for the Core↔Wave reflection bridge (F1): <c>Wave.Patch</c> exposes two
/// 5-parameter overloads (the <c>MethodBase</c>-target overload and the generic-definition
/// overload), so the old arity-4 lookup bound nothing and every prefix/postfix slot
/// registration threw "Wave API drift". The bridge now resolves by leading parameter types.
/// These tests exercise the product selectors (reached by assembly-qualified name, the same
/// way <see cref="LiveGates"/> reaches internals — Nami.Core exposes no InternalsVisibleTo)
/// against a Wave-double mirroring the real overload pair, so they fail on the pre-fix
/// arity lookup and pass on the type lookup.
/// (Nami.Tests stays Wave-free: the double stands in for the optional Wave assembly.)
/// </summary>
public class WaveBridgeTests
{
    private static readonly Type BridgeType =
        typeof(Nami.Core.Generations.ModGeneration).Assembly
            .GetType("Nami.Core.Generations.WaveBridge", throwOnError: true)!;

    /// <summary>
    /// Wave-double mirroring the real <c>Nami.Wave.Wave</c> overload pair:
    /// two 5-parameter <c>Patch</c> overloads distinguished only by leading types.
    /// </summary>
    private static class FakeWave
    {
        public static (MethodBase? Target, string? Owner, Delegate? Prefix, Delegate? Postfix, object? Transpiler)? LastPatch;

        public static void Patch(MethodBase target, string owner, Delegate? prefix, Delegate? postfix, object? transpiler)
        {
            LastPatch = (target, owner, prefix, postfix, transpiler);
        }

        public static MethodInfo Patch(MethodInfo genericDefinition, Type[] typeArguments, string owner, Delegate? prefix, Delegate? postfix)
        {
            return genericDefinition;
        }

        public static void Hook(MethodBase target, string owner, Func<bool>? gate, Action? observer)
        {
        }
    }

    /// <summary>IL2CPP-double mirroring the <c>HookFull</c>/<c>HookTyped</c> 9-parameter pair.</summary>
    private static class FakeIl2Cpp
    {
        public static void HookFull(string assembly, string ns, string klass, string method,
            int argCount, DayOfWeek returnKind, Delegate? prefix, Delegate? postfix, string owner)
        {
        }

        public static void HookTyped(string assembly, string ns, string klass, string method,
            IReadOnlyList<string> parameterTypes, DayOfWeek? returnType,
            Delegate? prefix, Delegate? postfix, string owner)
        {
        }
    }

    private static object? InvokeSelector(string name, MethodInfo[] candidates)
    {
        var selector = BridgeType.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"WaveBridge.{name} not found.");
        try
        {
            return selector.Invoke(null, new object?[] { candidates });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    [Fact]
    public void PatchSelector_PicksMethodBaseOverload_NotGenericDefinition()
    {
        var candidates = typeof(FakeWave).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Patch").ToArray();
        Assert.Equal(2, candidates.Length); // the ambiguous pair the old arity lookup choked on
        Assert.All(candidates, m => Assert.Equal(5, m.GetParameters().Length));

        // The pre-fix lookup (name + 4 params) binds nothing: both overloads take 5.
        Assert.DoesNotContain(candidates, m => m.GetParameters().Length == 4);

        var selected = (MethodInfo?)InvokeSelector("SelectPatchMethod", typeof(FakeWave).GetMethods(BindingFlags.Public | BindingFlags.Static));
        Assert.NotNull(selected);
        var parameters = selected.GetParameters();
        Assert.Equal(typeof(MethodBase), parameters[0].ParameterType);
        Assert.Equal(typeof(string), parameters[1].ParameterType);
    }

    [Fact]
    public void PatchSelector_InvokesFiveParams_PrefixAndPostfixSlots()
    {
        var selected = (MethodInfo?)InvokeSelector("SelectPatchMethod", typeof(FakeWave).GetMethods(BindingFlags.Public | BindingFlags.Static));
        Assert.NotNull(selected);

        // Exact invoke shape the fixed PatchPrefix/PatchPostfix bridge calls use.
        var target = typeof(FakeWave).GetMethod(nameof(FakeWave.Hook))!;
        Action prefix = () => { };
        FakeWave.LastPatch = null;
        selected.Invoke(null, new object?[] { target, "owner", prefix, null, null });
        Assert.NotNull(FakeWave.LastPatch);
        Assert.Same(prefix, FakeWave.LastPatch.Value.Prefix);
        Assert.Null(FakeWave.LastPatch.Value.Postfix);

        Action postfix = () => { };
        selected.Invoke(null, new object?[] { target, "owner", null, postfix, null });
        Assert.Same(postfix, FakeWave.LastPatch.Value.Postfix);
        Assert.Null(FakeWave.LastPatch.Value.Prefix);
    }

    [Fact]
    public void HookFullTypedSelectors_DisambiguateNineParamPair()
    {
        var candidates = typeof(FakeIl2Cpp).GetMethods(BindingFlags.Public | BindingFlags.Static);
        var full = (MethodInfo?)InvokeSelector("SelectHookFullMethod", candidates);
        var typed = (MethodInfo?)InvokeSelector("SelectHookTypedMethod", candidates);

        Assert.NotNull(full);
        Assert.Equal("HookFull", full.Name);
        Assert.NotNull(typed);
        Assert.Equal("HookTyped", typed.Name);
        Assert.NotSame(full, typed);
    }
}
