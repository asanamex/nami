using Nami;
using Nami.Wave;

namespace Nami.Wave.Tests;

// WaveIl2Cpp hooks IL2CPP game methods through the injected nami_loader. These tests run
// WITHOUT a game/loader process, so they pin the graceful-failure contract: availability
// is false, hooks refuse loudly with a precise error, and argument validation happens
// before any native call. The native machinery itself is covered by native/smoke
// (dispatch stub + trampoline + restore) and in-game verification (documented in
// docs/tide.md §9).
public unsafe class WaveIl2CppTests
{
    [Fact]
    public void Il2Cpp_WithoutLoader_IsNotAvailable()
    {
        Assert.False(WaveIl2Cpp.IsAvailable);
    }

    [Fact]
    public void Il2Cpp_WithoutLoader_HookThrowsClearError()
    {
        var ex = Assert.Throws<WaveIl2Cpp.Il2CppHookException>(() =>
            WaveIl2Cpp.Hook("GameAssembly", "MyGame", "Player", "TakeDamage", 1,
                (instance, args, count) => false, "test.mod"));
        Assert.Contains("requires the Nami loader", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void Il2Cpp_ArgCountOutOfRange_Throws(int argCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WaveIl2Cpp.Hook("GameAssembly", "MyGame", "Player", "TakeDamage", argCount,
                (instance, args, count) => false, "test.mod"));
    }

    [Fact]
    public void Il2Cpp_WithoutLoader_HookFullThrowsClearError()
    {
        var ex = Assert.Throws<WaveIl2Cpp.Il2CppHookException>(() =>
            WaveIl2Cpp.HookFull("GameAssembly", "MyGame", "Player", "GetHealth", 0,
                WaveIl2Cpp.Il2CppReturnKind.I32,
                (instance, args, count, result, kind) => false, null, "test.mod"));
        Assert.Contains("requires the Nami loader", ex.Message);
    }

    [Fact]
    public void Il2Cpp_HookFull_RequiresPrefixOrPostfix()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WaveIl2Cpp.HookFull("GameAssembly", "MyGame", "Player", "GetHealth", 0,
                WaveIl2Cpp.Il2CppReturnKind.I32, null, null, "test.mod"));
        Assert.Contains("prefix/postfix", ex.Message);
    }

    [Fact]
    public void Il2Cpp_HookFull_PostfixOnly_IsValidSignature()
    {
        // Only postfix (no prefix) must pass validation and reach the no-loader gate,
        // not throw the "at least one" argument error.
        var ex = Assert.Throws<WaveIl2Cpp.Il2CppHookException>(() =>
            WaveIl2Cpp.HookFull("GameAssembly", "MyGame", "Player", "GetHealth", 0,
                WaveIl2Cpp.Il2CppReturnKind.F32, null,
                (instance, args, count, result, kind) => { }, "test.mod"));
        Assert.Contains("requires the Nami loader", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(13)]
    public void Il2Cpp_HookFull_ArgCountOutOfRange_Throws(int argCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WaveIl2Cpp.HookFull("GameAssembly", "MyGame", "Player", "Compute", argCount,
                WaveIl2Cpp.Il2CppReturnKind.I64,
                (instance, args, count, result, kind) => false,
                (instance, args, count, result, kind) => { }, "test.mod"));
    }

    [Fact]
    public void Il2Cpp_HookFull_ReturnKindOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WaveIl2Cpp.HookFull("GameAssembly", "MyGame", "Player", "Compute", 1,
                (WaveIl2Cpp.Il2CppReturnKind)99,
                (instance, args, count, result, kind) => false,
                (instance, args, count, result, kind) => { }, "test.mod"));
    }

    [Fact]
    public void Il2Cpp_HookFull_AcceptsStackArgCounts()
    {
        // Up to 12 args is legal (register + stack); 12 must pass validation and reach
        // the no-loader gate rather than the argument-range check.
        var ex = Assert.Throws<WaveIl2Cpp.Il2CppHookException>(() =>
            WaveIl2Cpp.HookFull("GameAssembly", "MyGame", "Player", "Wide", 12,
                WaveIl2Cpp.Il2CppReturnKind.Void,
                (instance, args, count, result, kind) => false,
                (instance, args, count, result, kind) => { }, "test.mod"));
        Assert.Contains("requires the Nami loader", ex.Message);
    }

    [Fact]
    public void Il2Cpp_WithoutLoader_HookTypedThrowsClearError()
    {
        var ex = Assert.Throws<WaveIl2Cpp.Il2CppHookException>(() =>
            WaveIl2Cpp.HookTyped("GameAssembly", "MyGame", "Player", "TakeDamage",
                new[] { Nami.TideType.I32 }, Nami.TideType.Void,
                context => false, null, "test.mod"));
        Assert.Contains("requires the Nami loader", ex.Message);
    }

    [Fact]
    public void Il2Cpp_HookTyped_RequiresPrefixOrPostfix()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WaveIl2Cpp.HookTyped("GameAssembly", "MyGame", "Player", "TakeDamage",
                new[] { Nami.TideType.I32 }, Nami.TideType.Void,
                null, null, "test.mod"));
        Assert.Contains("prefix/postfix", ex.Message);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(20)]
    public void Il2Cpp_HookTyped_TooManyArgs_Throws(int count)
    {
        var types = new Nami.TideType[count];
        Array.Fill(types, Nami.TideType.I32);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WaveIl2Cpp.HookTyped("GameAssembly", "MyGame", "Player", "Wide",
                types, Nami.TideType.Void,
                context => false, null, "test.mod"));
    }
}
