using Nami.Sdk;

namespace Nami.Samples.TideProbe;

/// <summary>
/// Verifies Tide's typed game access end-to-end in a real game.
/// </summary>
[NamiPlugin]
[PluginInfo("dev.nami.samples.tideprobe", "Tide Probe", "0.1.0", Description = "Verifies typed game access through Tide.")]
public sealed class TideProbePlugin : NamiPlugin
{
    public override void OnLoad()
    {
        var log = Context.Log;

        if (!Tide.IsAvailable)
        {
            log.Error("Tide unavailable — is nami_loader loaded and enableMonoBridge on?");
            return;
        }

        log.Info("Tide available; typed calls...");

        // 1. Typed static method call with a STRING arg: Debug.Log(object).
        try
        {
            var debug = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Debug");
            debug.CallStatic("Log", TideValue.FromString("TideProbe: typed Debug.Log(string) OK"));
            log.Info("typed Debug.Log(string) call OK");
        }
        catch (Exception ex)
        {
            log.Error($"Debug.Log(string) failed: {ex.Message}");
        }

        // 2. Typed static method call with an INT arg (pick a harmless int-taking static).
        //    Debug.Log(object) boxes any value; pass an int to prove primitive marshaling.
        try
        {
            var debug = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Debug");
            debug.CallStatic("Log", TideValue.FromInt(12345));
            log.Info("typed Debug.Log(int) call OK (primitive arg marshaled)");
        }
        catch (Exception ex)
        {
            log.Error($"Debug.Log(int) failed: {ex.Message}");
        }

        // 3. Instance access: Application has no parameterless ctor, so create a
        //    UnityEngine.GameObject and call an instance method on it — the real prize.
        try
        {
            var goClass = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "GameObject");
            var go = goClass.NewObject();  // new GameObject() — parameterless ctor
            log.Info($"created GameObject instance (handle={go.HandleValue})");

            var id = go.CallIntMethod("GetInstanceID");
            log.Info($"GameObject.GetInstanceID() = {id}");
            go.Dispose();
        }
        catch (Exception ex)
        {
            log.Error($"instance access failed: {ex.Message}");
        }

        // 4. Unity internal-call property access (the historical crash case): read AND write
        //    Time.timeScale, whose getter/setter are Unity internal calls that used to crash
        //    when invoked from the nested drain.
        try
        {
            var timeClass = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Time");
            var original = timeClass.GetStaticFloat("timeScale");
            log.Info($"Time.timeScale read = {original}");
            timeClass.SetStaticFloat("timeScale", original);
            log.Info("Time.timeScale write OK (internal-call property path stable)");
        }
        catch (Exception ex)
        {
            log.Error($"Time.timeScale property access failed: {ex.Message}");
        }

        // 5. Scene-object discovery via a known safe static: Camera.main is a plain managed
        //    static property (no internal-call scene iteration). Full FindObjectOfType needs
        //    a non-nested main-thread hook (see docs) and is not exposed yet.
        try
        {
            var cameraClass = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Camera");
            using var camera = cameraClass.GetStaticObject("main");
            log.Info(camera is not null
                ? $"Camera.main found live instance (handle={camera.HandleValue})"
                : "Camera.main -> none (no active Camera in the scene)");
        }
        catch (Exception ex)
        {
            log.Error($"Camera.main access failed: {ex.Message}");
        }

        log.Info("TideProbe verification complete");
    }
}
