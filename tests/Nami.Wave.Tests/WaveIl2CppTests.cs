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
}