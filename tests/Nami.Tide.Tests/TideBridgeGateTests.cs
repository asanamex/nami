namespace Nami.Tests;

/// <summary>
/// Honest <c>enableMonoBridge</c> gate: when disabled, every mod-facing Tide entry throws
/// <see cref="Tide.TideException"/> with <c>Code=-3</c> instead of touching native
/// (no AV possible by construction); when enabled but loader-absent, the historical
/// no-throw behavior holds. Boot.Run propagates the flag via Tide.SetBridgeEnabled.
/// </summary>
public sealed class TideBridgeGateTests
{
    [Fact]
    public void Disabled_Bridge_Throws_BeforeNativeContact()
    {
        Tide.SetBridgeEnabled(false);
        try
        {
            Assert.False(Tide.BridgeEnabled);
            Assert.False(Tide.IsAvailable);
            Assert.False(Tide.IsReady);

            var logEx = Assert.Throws<Tide.TideException>(() => Tide.UnityLog("hello"));
            Assert.Equal(-3, logEx.Code);

            var invokeEx = Assert.Throws<Tide.TideException>(() => Tide.InvokeStatic("A", "B", "C", "M"));
            Assert.Equal(-3, invokeEx.Code);

            var cls = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Time");
            var callEx = Assert.Throws<Tide.TideException>(() => cls.GetStaticFloat("timeScale"));
            Assert.Equal(-3, callEx.Code);

            using var batch = new TideBatch();
            batch.EnqueueGetStatic(cls, "timeScale", TideType.R4);
            var batchEx = Assert.Throws<Tide.TideException>(() => batch.Flush());
            Assert.Equal(-3, batchEx.Code);

            // Teardown stays no-throw when disabled (early return, no native contact).
            GameObject.FromHandle(999).Dispose();
        }
        finally
        {
            Tide.SetBridgeEnabled(true);
        }
    }

    [Fact]
    public void Enabled_WithoutLoader_KeepsHistoricalNoThrowBehavior()
    {
        Tide.SetBridgeEnabled(true);
        try
        {
            Assert.True(Tide.BridgeEnabled);
            // nami_loader.dll is not loaded in a plain unit test process.
            Assert.False(Tide.IsAvailable);
            Assert.False(Tide.UnityLog("hello"));
            Assert.False(Tide.InvokeStatic("A", "B", "C", "M"));
        }
        finally
        {
            Tide.SetBridgeEnabled(true);
        }
    }
}
