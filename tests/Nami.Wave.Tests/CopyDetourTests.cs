using System.Reflection;
using System.Runtime.CompilerServices;
using Nami.Wave;

namespace Nami.Wave.Tests;

public static class CopyDetourTarget
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Add(int a, int b)
    {
        return a + b;
    }
}

// Detour a target straight to a plain DM copy of itself (no injection). Is the
// detour -> DynamicMethod jump itself sound?
public class CopyDetourTests : IDisposable
{
    public CopyDetourTests() => Wave.UnpatchEverything();
    public void Dispose() => Wave.UnpatchEverything();

    [Fact]
    public void DetourToCleanCopy_Works()
    {
        var m = typeof(CopyDetourTarget).GetMethod(nameof(CopyDetourTarget.Add))!;
        // Patch with a no-op prefix/postfix (still exercises the full detour+DM path).
        Wave.Patch(m, "t", prefix: () => { });
        var result = CopyDetourTarget.Add(20, 22);
        Assert.Equal(42, result);
    }
}
