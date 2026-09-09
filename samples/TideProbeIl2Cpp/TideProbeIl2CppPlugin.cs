using Nami.Sdk;

namespace Nami.Samples.TideProbeIl2Cpp;

/// <summary>
/// Verifies Tide's IL2CPP backend end-to-end in a real IL2CPP Unity game.
/// Exercises type resolution, static field/property access, method calls with typed
/// returns, strings, enums, arrays and exceptions - all through the typed GameClass API
/// routed to the IL2CPP main-thread executor (window-proc drain).
/// </summary>
[NamiPlugin]
[PluginInfo("dev.nami.samples.tideprobe-il2cpp", "Tide Probe (IL2CPP)", "0.1.0",
    Description = "Verifies typed game access through Tide's IL2CPP backend.")]
public sealed class TideProbeIl2CppPlugin : NamiPlugin
{
    private int _ticks;
    private bool _probeDone;

    public override void OnLoad()
    {
        var log = Context.Log;

        if (!Tide.IsAvailable)
        {
            log.Error("Tide unavailable — is nami_loader loaded?");
            return;
        }

        log.Info($"Tide available; backend={Tide.ActiveBackend}");

        if (Tide.ActiveBackend != Tide.Backend.Il2Cpp)
        {
            log.Info("Not an IL2CPP title — this probe only runs on IL2CPP games.");
            return;
        }

        // The IL2CPP executor needs the game's main window, which may not exist yet at mod
        // load time; OnUpdate retries EnsureReady until it is.
        log.Info($"IL2CPP executor ready={Tide.EnsureReady()}");
    }

    public override void OnUpdate()
    {
        if (_probeDone)
        {
            return;
        }

        // Wait until the executor is confirmed ready (window present), then run once.
        if (!Tide.EnsureReady())
        {
            return;
        }
        if (++_ticks < 10)
        {
            return;
        }

        _probeDone = true;
        var log = Context.Log;

        // 1. Typed static method call with a STRING arg: Debug.Log(object) on the main thread.
        try
        {
            var debug = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Debug");
            debug.CallStatic("Log", TideValue.FromString("TideProbe-IL2CPP: typed Debug.Log(string) OK"));
            log.Info("typed Debug.Log(string) call OK");
        }
        catch (Exception ex)
        {
            log.Error($"Debug.Log(string) failed: {ex.Message}");
        }

        // 1b. The UnityLog convenience wrapper (same Debug.Log call, first-class API).
        try
        {
            bool ok = Tide.UnityLog("TideProbe-IL2CPP: Tide.UnityLog OK");
            log.Info($"Tide.UnityLog returned {ok}");
        }
        catch (Exception ex)
        {
            log.Error($"Tide.UnityLog failed: {ex.Message}");
        }

        // 2. Static property read: Application.runInBackground (typed Get<bool>).
        try
        {
            var app = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Application");
            bool bg = app.Get<bool>("runInBackground");
            log.Info($"Application.runInBackground (typed Get<bool>) = {bg}");
        }
        catch (Exception ex)
        {
            log.Error($"Application.runInBackground failed: {ex.Message}");
        }

        // 3. Enum property read as int: Screen.orientation (ScreenOrientation enum).
        try
        {
            var screen = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Screen");
            int orientation = screen.Get<int>("orientation");
            log.Info($"Screen.orientation (enum via Get<int>) = {orientation}");
        }
        catch (Exception ex)
        {
            log.Error($"enum read failed: {ex.Message}");
        }

        // 4. Static string[] array read: System.Environment.GetCommandLineArgs().
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

        // 5. Fail-loud exception surfacing: call a method that throws.
        try
        {
            var c = GameClass.Resolve("mscorlib", "System", "Convert");
            c.CallStatic("ToInt32", TideValue.FromString("not-a-number"));
            log.Info("Convert.ToInt32(bad) unexpectedly succeeded");
        }
        catch (Tide.TideException ex)
        {
            log.Info($"exception surfaced OK: code={ex.Code} mono={ex.IsMonoException}");
            log.Info($"  {ex.Message.Split('\n')[0]}");
        }
        catch (Exception ex)
        {
            log.Error($"unexpected exception type: {ex.GetType().Name}: {ex.Message}");
        }

        log.Info("TideProbe-IL2CPP verification complete");
    }
}
