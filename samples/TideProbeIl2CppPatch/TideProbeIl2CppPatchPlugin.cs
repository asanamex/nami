using Nami.Sdk;
using Nami.Wave;

namespace Nami.Samples.TideProbeIl2CppPatch;

/// <summary>
/// Verifies Wave's IL2CPP method patching end-to-end in a real IL2CPP Unity game:
/// install + pass-through (trampoline preserves the original result), skip semantics,
/// exact restore on unhook, and an organic per-frame hook the game itself drives.
///
/// All assertions are driven by the probe itself via Tide — deterministic, no game
/// behavior is assumed:
///   A. baseline: System.Environment.get_TickCount returns real values (no hook)
///   B. hook + pass-through: 5 calls return real values AND fire the callback
///   C. skip: the hooked call returns 0
///   D. unhook: exact restore, real values again
///   E. organic: UnityEngine.Time.get_deltaTime hooked pass-through; count fires the
///      game's own per-frame reads over ~3 seconds (honest report if the game reads 0)
/// </summary>
[NamiPlugin]
[PluginInfo("dev.nami.samples.tideprobe-il2cpp-patch", "Tide Probe (IL2CPP Patch)", "0.1.0",
    Description = "Verifies Wave IL2CPP method patching in a real IL2CPP game.")]
public sealed unsafe class TideProbeIl2CppPatchPlugin : NamiPlugin
{
    private int _ticks;
    private int _stage;
    private bool _skip;

    // Hook-fired counters (callback runs on the game's main thread — Interlocked only).
    private int _passThroughFires;
    private int _organicFires;
    private long _organicStartMs;

    private GameClass? _env;
    private WaveIl2Cpp.Il2CppHook? _tickHook;
    private WaveIl2Cpp.Il2CppHook? _deltaHook;

    public override void OnLoad()
    {
        var log = Context.Log;

        if (!Tide.IsAvailable)
        {
            log.Error("Tide unavailable — is nami_loader loaded?");
            return;
        }

        if (Tide.ActiveBackend != Tide.Backend.Il2Cpp)
        {
            log.Info("Not an IL2CPP title — this probe only runs on IL2CPP games.");
            return;
        }

        log.Info($"Wave IL2CPP patching available={WaveIl2Cpp.IsAvailable}");
        log.Info($"IL2CPP executor ready={Tide.EnsureReady()}");
    }

    public override void OnUpdate()
    {
        if (_stage > 5)
        {
            return;
        }

        // The IL2CPP executor needs the game window; OnUpdate retries until it exists.
        if (!Tide.EnsureReady())
        {
            return;
        }
        if (++_ticks < 10)
        {
            return;
        }

        try
        {
            RunStages();
        }
        catch (Exception ex)
        {
            Context.Log.Error($"demo crashed: {ex}");
            _stage = 99;
        }
    }

    private void RunStages()
    {
        var log = Context.Log;

        switch (_stage)
        {
            case 0:
            {
                // A. Baseline — the un-hooked get_TickCount returns real millisecond values.
                _env = GameClass.Resolve("mscorlib", "System", "Environment");
                var values = new List<int>();
                for (var i = 0; i < 5; i++)
                {
                    values.Add(_env.CallStaticValue("get_TickCount", Array.Empty<TideValue>(), TideType.I32).Int32);
                }

                log.Info($"[A baseline] get_TickCount = {string.Join(", ", values)}");
                log.Info(values.Any(v => v != 0)
                    ? "[A baseline] PASS — real values without any hook"
                    : "[A baseline] FAIL — expected nonzero tick values");
                _stage = 1;
                break;
            }

            case 1:
            {
                // B1. Install the hook.
                _tickHook = WaveIl2Cpp.Hook("mscorlib", "System", "Environment", "get_TickCount",
                    0, OnTickHook, "tideprobe-il2cpp-patch");
                log.Info($"[B install] hook installed: {_tickHook}");
                _stage = 2;
                break;
            }

            case 2:
            {
                // B2. Pass-through — every call must both fire the callback AND return a real value.
                _passThroughFires = 0;
                var values = new List<int>();
                for (var i = 0; i < 5; i++)
                {
                    values.Add(_env!.CallStaticValue("get_TickCount", Array.Empty<TideValue>(), TideType.I32).Int32);
                }

                log.Info($"[B passthrough] get_TickCount = {string.Join(", ", values)}");
                log.Info($"[B passthrough] callback fired {_passThroughFires}/5 times");
                log.Info(_passThroughFires == 5 && values.All(v => v != 0)
                    ? "[B passthrough] PASS — trampoline preserves the original result"
                    : "[B passthrough] FAIL");
                _stage = 3;
                break;
            }

            case 3:
            {
                // C. Skip — the callback returns true; the original is never called and 0 is returned.
                _skip = true;
                var skipped = _env!.CallStaticValue("get_TickCount", Array.Empty<TideValue>(), TideType.I32).Int32;
                _skip = false;
                log.Info($"[C skip] get_TickCount = {skipped} (expected 0)");
                log.Info(skipped == 0 ? "[C skip] PASS — original skipped, return 0" : "[C skip] FAIL");
                _stage = 4;
                break;
            }

            case 4:
            {
                // D. Unhook — exact byte restore: real values again without the callback firing.
                _passThroughFires = 0;
                _tickHook!.Dispose();
                _tickHook = null;
                var restored = _env!.CallStaticValue("get_TickCount", Array.Empty<TideValue>(), TideType.I32).Int32;
                log.Info($"[D unhook] get_TickCount = {restored}, callback fired {_passThroughFires} times");
                log.Info(restored != 0 && _passThroughFires == 0
                    ? "[D unhook] PASS — exact restore"
                    : "[D unhook] FAIL");
                _stage = 5;
                break;
            }

            case 5:
            {
                // E. Organic — hook a per-frame method the GAME calls; count fires over ~3 s.
                if (_deltaHook is null)
                {
                    _deltaHook = WaveIl2Cpp.Hook("UnityEngine.CoreModule", "UnityEngine", "Time",
                        "get_deltaTime", 0, OnDeltaHook, "tideprobe-il2cpp-patch");
                    _organicFires = 0;
                    _organicStartMs = Environment.TickCount64;
                    log.Info("[E organic] Time.get_deltaTime hooked — counting game-driven fires for 3 s...");
                }
                else if (Environment.TickCount64 - _organicStartMs >= 3000)
                {
                    _deltaHook.Dispose();
                    _deltaHook = null;
                    log.Info($"[E organic] game-driven fires in 3 s = {_organicFires}");
                    log.Info(_organicFires > 0
                        ? "[E organic] PASS — the game itself called the hooked method"
                        : "[E organic] NOTE — game read Time.get_deltaTime 0 times this session (fine; phases A-D already proved the machinery)");
                    log.Info("TideProbe-IL2CPP-Patch verification complete");
                    _stage = 6;
                }

                break;
            }
        }
    }

    private bool OnTickHook(nint instance, nint* args, int argCount)
    {
        Interlocked.Increment(ref _passThroughFires);
        return _skip;
    }

    private bool OnDeltaHook(nint instance, nint* args, int argCount)
    {
        Interlocked.Increment(ref _organicFires);
        return false;
    }
}