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

            // Call instance method: set name via property "name" (instance property set).
            // GameObject.name is a property with a setter that goes through Unity internal
            // calls; instead call a pure managed instance method: AddComponent needs a type
            // arg (unsupported), so call getter-free instance method: Transform? Keep simple:
            // read instance property via the property path is the risky one; instead call
            // GetInstanceID() (0-arg, returns int, managed wrapper around native).
            var id = go.CallIntMethod("GetInstanceID");
            log.Info($"GameObject.GetInstanceID() = {id}");
            go.Dispose();
        }
        catch (Exception ex)
        {
            log.Error($"instance access failed: {ex.Message}");
        }

        log.Info("TideProbe verification complete");
    }
}
