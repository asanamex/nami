namespace Nami.Tests;

public sealed class GameObjectTests
{
    [Fact]
    public void FromHandle_Zero_ReturnsNull()
    {
        Assert.Null(GameObject.FromHandle(0));
    }

    [Fact]
    public void FromHandle_NonZero_Wraps()
    {
        var go = GameObject.FromHandle(12345);
        Assert.NotNull(go);
        Assert.Equal(12345L, go!.HandleValue);
        Assert.False(go.IsDisposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // Dispose sends FreeHandle to native, which is unavailable in a unit test — the
        // managed handle is zeroed FIRST, so the second dispose is a pure no-op regardless
        // of whether the first reached native.
        var go = GameObject.FromHandle(1);
        go.Dispose();  // zeroes the handle before calling native
        Assert.True(go.IsDisposed);
        go.Dispose();  // must not throw
    }

    [Fact]
    public void UseAfterDispose_ThrowsObjectDisposed()
    {
        var go = GameObject.FromHandle(1);
        go.Dispose();
        Assert.True(go.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => go.CallIntMethod("GetInstanceID"));
        Assert.Throws<ObjectDisposedException>(() => go.GetInt("field"));
        Assert.Throws<ObjectDisposedException>(() => go.SetInt("field", 1));
        Assert.Throws<ObjectDisposedException>(() => go.CallVoid("M", Array.Empty<TideValue>()));
    }
}

public sealed class TideAvailabilityTests
{
    [Fact]
    public void IsAvailable_IsFalse_OutsideGame()
    {
        // nami_loader.dll is not loaded in a plain unit test process.
        Assert.False(Tide.IsAvailable);
    }

    [Fact]
    public void UnityLog_ReturnsFalse_WhenUnavailable()
    {
        Assert.False(Tide.UnityLog("hello"));
    }

    [Fact]
    public void InvokeStatic_ReturnsFalse_WhenUnavailable()
    {
        Assert.False(Tide.InvokeStatic("A", "B", "C", "M"));
    }
}
