using System.Reflection;
using Nami.Wave;

namespace Nami.Wave.Tests;

/// <summary>
/// Wave-side shape proof for the Core↔Wave bridge seam (F1): the <c>MethodBase</c>-target
/// <c>Patch</c> overload takes 5 parameters, and prefix-only / postfix-only registrations
/// succeed through the exact 5-argument reflection shape the fixed bridge invokes with
/// <c>(target, owner, prefix, null, null)</c> / <c>(target, owner, null, postfix, null)</c>.
/// The pre-fix arity-4 lookup bound no overload at all (both <c>Patch</c> overloads take 5),
/// so this test fails against the old assumption and passes on the type-resolved shape.
/// </summary>
public class WavePatchSeamTests : IDisposable
{
    public WavePatchSeamTests()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
        M2Targets.Calls = 0;
    }

    public void Dispose()
    {
        Wave.UnpatchEverything();
        Wave.UnhookEverything();
    }

    private static MethodInfo ResolveMethodBasePatch()
    {
        var matches = typeof(Wave).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Patch"
                && m.GetParameters() is { Length: 5 } parameters
                && parameters[0].ParameterType == typeof(MethodBase)
                && parameters[1].ParameterType == typeof(string))
            .ToArray();
        Assert.Single(matches);
        return matches[0];
    }

    [Fact]
    public void Patch_HasNoFourParamOverload_MethodBaseOverloadTakesFive()
    {
        var patches = typeof(Wave).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Patch").ToArray();
        Assert.NotEmpty(patches);
        Assert.DoesNotContain(patches, m => m.GetParameters().Length == 4);
        Assert.Equal(2, patches.Count(m => m.GetParameters().Length == 5));

        var methodBase = ResolveMethodBasePatch();
        Assert.Equal(typeof(MethodBase), methodBase.GetParameters()[0].ParameterType);
    }

    private static int s_prefixSeen;
    private static int s_postfixSeen;

    private static void SeamPrefix(int a, int b) => s_prefixSeen = a + b;
    private static void SeamPostfix(int a, int b, ref int __result) { s_postfixSeen = __result; __result += 1000; }

    private delegate void SeamPostfixDelegate(int a, int b, ref int __result);

    [Fact]
    public void Patch_PrefixOnly_ViaFiveArgReflectionShape_RunsPrefix()
    {
        var patch = ResolveMethodBasePatch();
        var target = typeof(M2Targets).GetMethod(nameof(M2Targets.Add))!;

        s_prefixSeen = 0;
        patch.Invoke(null, new object?[] { target, "seam-prefix", (Delegate)new Action<int, int>(SeamPrefix), null, null });

        Assert.Equal(42, M2Targets.Add(20, 22));
        Assert.Equal(42, s_prefixSeen);
    }

    [Fact]
    public void Patch_PostfixOnly_ViaFiveArgReflectionShape_RunsPostfix()
    {
        var patch = ResolveMethodBasePatch();
        var target = typeof(M2Targets).GetMethod(nameof(M2Targets.Add))!;

        s_postfixSeen = 0;
        patch.Invoke(null, new object?[] { target, "seam-postfix", null, (Delegate)new SeamPostfixDelegate(SeamPostfix), null });

        Assert.Equal(1042, M2Targets.Add(20, 22));
        Assert.Equal(42, s_postfixSeen);
    }
}
