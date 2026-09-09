using System.Reflection;
using Nami.Wave;
using Nami.Wave.Internal;

namespace Nami.Wave.Tests;

public class GenMethodDirectTests
{
    [Fact]
    public void PatchedBody_DirectInvoke()
    {
        var m = typeof(CopyDetourTarget).GetMethod(nameof(CopyDetourTarget.Add))!;
        var body = IlRewriter.Analyze(m);
        var entries = new List<PatchEntry>
        {
            new() { Owner = "t", Prefix = () => { } },
        };
        var patched = PatchedBodyBuilder.Build(m, entries, body);

        // Invoke directly (no detour) - validates the generated IL.
        var result = (int)patched.Invoke(null, new object[] { 20, 22 })!;
        Assert.Equal(42, result);
    }

    [Fact]
    public void PatchedBody_WithPostfix_DirectInvoke()
    {
        var m = typeof(CopyDetourTarget).GetMethod(nameof(CopyDetourTarget.Add))!;
        var body = IlRewriter.Analyze(m);
        var entries = new List<PatchEntry>
        {
            new() { Owner = "t", Postfix = () => { } },
        };
        var patched = PatchedBodyBuilder.Build(m, entries, body);

        var result = (int)patched.Invoke(null, new object[] { 20, 22 })!;
        Assert.Equal(42, result);
    }

    [Fact]
    public void PatchedBody_WithPostfixRefResult_DirectInvoke()
    {
        var m = typeof(CopyDetourTarget).GetMethod(nameof(CopyDetourTarget.Add))!;
        var body = IlRewriter.Analyze(m);
        var entries = new List<PatchEntry>
        {
            new() { Owner = "t", Postfix = (ref int __result) => { __result += 5; } },
        };
        var patched = PatchedBodyBuilder.Build(m, entries, body);

        var result = (int)patched.Invoke(null, new object[] { 20, 22 })!;
        Assert.Equal(47, result);
    }
}
