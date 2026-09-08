using Nami.Sdk;

namespace Nami.Samples.TideProbe;

/// <summary>
/// Verifies Tide's typed game access end-to-end in a real game.
/// </summary>
[NamiPlugin]
[PluginInfo("dev.nami.samples.tideprobe", "Tide Probe", "0.1.0", Description = "Verifies typed game access through Tide.")]
public sealed class TideProbePlugin : NamiPlugin
{
    private int _ticks;
    private bool _sceneProbeDone;

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

        // 2. Typed static method call with an INT arg — proves primitive marshaling AND the
        //    signature-aware boxing path (int is boxed for Debug.Log(object)).
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

        // 3. Instance access on a freshly created GameObject.
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

        // 4. Generic typed API: bool + enum reads, no hand-picked TideType.
        try
        {
            var app = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Application");
            bool wasBg = app.Get<bool>("runInBackground");
            log.Info($"Application.runInBackground (typed Get<bool>) = {wasBg}");
            app.Set("runInBackground", true);
            log.Info("Application.runInBackground set to true via typed Set<bool> OK");

            var quality = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "QualitySettings");
            int shadow = quality.Get<int>("shadowResolution");  // ShadowResolution enum as int
            log.Info($"QualitySettings.shadowResolution (enum via Get<int>) = {shadow}");
        }
        catch (Exception ex)
        {
            log.Error($"typed generic API failed: {ex.Message}");
        }

        // 5. Array access: read a static string[] — System.Environment.GetCommandLineArgs().
        try
        {
            var env = GameClass.Resolve("mscorlib", "System", "Environment");
            var v = env.CallStaticValue("GetCommandLineArgs", Array.Empty<TideValue>(), TideType.Object);
            using var args = GameObject.FromHandle(v.Handle);
            if (args is null)
            {
                log.Error("GetCommandLineArgs returned a null handle");
            }
            else
            {
                int n = TideArrays.GetLength(args);
                log.Info($"Environment.GetCommandLineArgs() length = {n}");
                if (n > 0)
                {
                    var first = TideArrays.GetString(args, 0);
                    log.Info($"args[0] = '{first}'");
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"array access failed: {ex.Message}");
        }

        log.Info("TideProbe boot checks complete; scene probe fires after the scene loads");
    }

    public override void OnUpdate()
    {
        // Scene-object discovery needs a loaded scene; fire once ~10s after boot (600 ticks).
        if (_sceneProbeDone)
        {
            return;
        }

        if (++_ticks < 600)
        {
            return;
        }

        _sceneProbeDone = true;
        var log = Context.Log;

        // 6. Scene-object discovery via the SAFE static-accessor route (baseline for
        //    step 7 below).
        try
        {
            var cameraClass = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Camera");
            using var camera = cameraClass.GetStaticObject("main");
            log.Info(camera is not null
                ? $"Camera.main found live instance (handle={camera.HandleValue})"
                : "Camera.main -> none (no active Camera in the scene)");
            if (camera is not null)
            {
                var name = camera.GetString("name");
                log.Info($"live Camera.name = '{name}'");
            }
        }
        catch (Exception ex)
        {
            log.Error($"Camera.main access failed: {ex.Message}");
        }

        // 7. Scene-object discovery via Object.FindObjectOfType (post-invoke export).
        //    Same target as step 6 — the two routes must agree.
        // 7a. Name-based search (GameObject.Find needs no Type arg — kept as the
        //     tripwire: if the Type-based step below ever regresses, this tells us
        //     whether scene iteration itself or only the Type path broke).
        try
        {
            var goClass = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "GameObject");
            var v = goClass.CallStaticValue("Find", new[] { TideValue.FromString("Main Camera") }, TideType.Object);
            using var byName = GameObject.FromHandle(v.Handle);
            log.Info(byName is not null
                ? $"GameObject.Find hit (handle={byName.HandleValue})"
                : "GameObject.Find -> none");
        }
        catch (Exception ex)
        {
            log.Error($"GameObject.Find failed: {ex.Message}");
        }

        try
        {
            var cameraClass = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Camera");
            using var found = cameraClass.FindObject();
            log.Info(found is not null
                ? $"FindObject(Camera) hit (handle={found.HandleValue})"
                : "FindObject(Camera) -> none");
            if (found is not null)
            {
                var name = found.GetString("name");
                log.Info($"found Camera.name = '{name}'");
            }
        }
        catch (Exception ex)
        {
            log.Error($"FindObject(Camera) failed: {ex.Message}");
        }

        log.Info("TideProbe verification complete");
    }
}
