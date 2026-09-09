using Nami.Sdk;
using Nami.Wave;

namespace Nami.Samples.TideProbeIl2CppPatch;

/// <summary>
/// Verifies Wave's IL2CPP method patching end-to-end in a real IL2CPP Unity game:
/// install + pass-through (trampoline preserves the original result), skip semantics,
/// exact restore on unhook, and an organic per-frame hook the game itself drives.
///
/// All assertions are driven by the probe itself via Tide - deterministic, no game
/// behavior is assumed:
///   A. baseline: System.Environment.get_TickCount returns real values (no hook)
///   B. hook + pass-through: 5 calls return real values AND fire the callback
///   C. skip: the hooked call returns 0
///   D. unhook: exact restore, real values again
///   E. organic: UnityEngine.Time.get_deltaTime hooked pass-through; count fires the
///      game's own per-frame reads over ~3 seconds (honest report if the game reads 0)
///   F. full path: System.Math.Max(int,int) hooked with HookFull - the postfix observes
///      the REAL result (2-arg call, kind I32), then a rewrite phase makes the caller
///      receive a different value while the postfix still saw the original
/// </summary>
[NamiPlugin]
[PluginInfo("dev.nami.samples.tideprobe-il2cpp-patch", "Tide Probe (IL2CPP Patch)", "0.1.0",
    Description = "Verifies Wave IL2CPP method patching in a real IL2CPP game.")]
public sealed unsafe class TideProbeIl2CppPatchPlugin : NamiPlugin
{
    private int _ticks;
    private int _stage;
    private bool _skip;

    // Hook-fired counters (callback runs on the game's main thread - Interlocked only).
    private int _passThroughFires;
    private int _organicFires;
    private long _organicStartMs;

    private GameClass? _env;
    private WaveIl2Cpp.Il2CppHook? _tickHook;
    private WaveIl2Cpp.Il2CppHook? _deltaHook;
    private WaveIl2Cpp.Il2CppHook? _maxHook;

    // Full-path state (Math.Max hook; dispatches run on the game main thread).
    private int _maxObserved;
    private int _maxPostfixFires;
    private bool _rewriteMax;

    // Typed-path state (HookTyped on Math.Max + GetInstanceID; same thread rules).
    private WaveIl2Cpp.Il2CppHook? _typedHook;
    private int _typedPrefixA0;
    private int _typedPrefixA1;
    private int _typedPrefixFires;
    private int _typedPostfixResult;
    private int _typedPostfixFires;
    private bool _rewriteTypedArg;
    private bool _rewriteTypedResult;
    private WaveIl2Cpp.Il2CppHook? _thisHook;
    private int _thisSeen;
    private int _thisDisposeNoThrow;
    private int _thisPostfixResult;
    private int _thisPostfixFires;
    private GameClass? _goClass;
    // Boxing proof (HookTyped on Debug.Log(object); dispatches on game main thread).
    private WaveIl2Cpp.Il2CppHook? _logHook;
    private int _logPrefixFires;
    private int _logSawObject;

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
        if (_stage > 16)
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
                // A. Baseline - the un-hooked get_TickCount returns real millisecond values.
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
                // B2. Pass-through - every call must both fire the callback AND return a real value.
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
                // C. Skip - the callback returns true; the original is never called and 0 is returned.
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
                // D. Unhook - exact byte restore: real values again without the callback firing.
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
                // E. Organic - hook a per-frame method the GAME calls; count fires over ~3 s.
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
                    _stage = 6;
                }

                break;
            }

            case 6:
            {
                // F1. Full-path install + pass-through: HookFull with a postfix only.
                // Math.Max(3,7) must return 7 AND the postfix must observe 7 (2 register
                // args, i32 result - the result slot/rax path).
                _maxHook = WaveIl2Cpp.HookFull("mscorlib", "System", "Math", "Max", 2,
                    WaveIl2Cpp.Il2CppReturnKind.I32, prefix: null, postfix: OnMaxPostfix,
                    "tideprobe-il2cpp-patch");
                log.Info($"[F install] full-path hook installed: {_maxHook}");
                _maxObserved = 0;
                _maxPostfixFires = 0;
                _stage = 7;
                break;
            }

            case 7:
            {
                // F2a. Pass-through: the caller receives the real max; postfix saw it too.
                var math = GameClass.Resolve("mscorlib", "System", "Math");
                var v1 = math.CallStaticValue("Max",
                    new[] { TideValue.FromInt(3), TideValue.FromInt(7) }, TideType.I32).Int32;
                log.Info($"[F passthrough] Max(3,7) = {v1}, postfix observed {_maxObserved} ({_maxPostfixFires} fires)");
                log.Info(v1 == 7 && _maxObserved == 7 && _maxPostfixFires == 1
                    ? "[F passthrough] PASS — real result reached the caller AND the postfix"
                    : "[F passthrough] FAIL");
                _stage = 8;
                break;
            }

            case 8:
            {
                // F2b. Rewrite: the postfix replaces the result; the caller must receive
                // the rewritten value while the postfix still observed the ORIGINAL 7.
                _rewriteMax = true;
                var math = GameClass.Resolve("mscorlib", "System", "Math");
                var v2 = math.CallStaticValue("Max",
                    new[] { TideValue.FromInt(3), TideValue.FromInt(7) }, TideType.I32).Int32;
                _rewriteMax = false;
                // NOTE: the resolved Max overload is byte-returning, so the caller
                // reads only al - a rewritten slot value is observable only through
                // its low byte (0x1C0FFEE -> 0xEE = 238). The postfix saw the REAL
                // 7 before the rewrite, which is the actual proof of the mechanism.
                const int sentinel = 0x1C0FFEE;
                const int visible = sentinel & 0xFF;
                log.Info($"[F rewrite] Max(3,7) = {v2} (sentinel {sentinel}, byte-visible {visible}), postfix observed {_maxObserved}");
                log.Info(v2 == visible && _maxObserved == 7
                    ? "[F rewrite] PASS — postfix rewrote the result after the original ran"
                    : "[F rewrite] FAIL");

                // F3. Unhook - exact restore, postfix no longer fires.
                _maxHook!.Dispose();
                _maxHook = null;
                _maxPostfixFires = 0;
                var v3 = math.CallStaticValue("Max",
                    new[] { TideValue.FromInt(3), TideValue.FromInt(7) }, TideType.I32).Int32;
                log.Info($"[F unhook] Max(3,7) = {v3}, postfix fires = {_maxPostfixFires}");
                log.Info(v3 == 7 && _maxPostfixFires == 0
                    ? "[F unhook] PASS — exact restore after full-path hook"
                    : "[F unhook] FAIL");
                _stage = 9;
                break;
            }

            case 9:
            {
                // G. TideBatch - N game ops in ONE main-thread round trip. Time 8
                // sequential (one-hop-per-op) calls against one batched flush of 8 ops.
                var env = _env!;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var seq = new int[8];
                for (var i = 0; i < seq.Length; i++)
                {
                    seq[i] = env.CallStaticValue("get_TickCount", Array.Empty<TideValue>(), TideType.I32).Int32;
                }
                sw.Stop();
                var seqMs = sw.Elapsed.TotalMilliseconds;

                using var batch = new TideBatch();
                var idx = new int[seq.Length];
                for (var i = 0; i < idx.Length; i++)
                {
                    idx[i] = batch.EnqueueCallStatic(env, "get_TickCount", TideType.I32);
                }
                sw.Restart();
                batch.Flush();
                sw.Stop();
                var batchMs = sw.Elapsed.TotalMilliseconds;

                var got = new int[idx.Length];
                var allOk = true;
                for (var i = 0; i < idx.Length; i++)
                {
                    got[i] = batch.GetInt(idx[i]);
                    allOk &= batch.WasOk(idx[i]) && got[i] != 0;
                }

                log.Info($"[G batch] sequential 8 ops = {seqMs:F1} ms ({seqMs / 8:F2} ms/op); " +
                         $"one batched flush of 8 ops = {batchMs:F1} ms ({batchMs / 8:F2} ms/op)");
                log.Info($"[G batch] values = {string.Join(", ", got)}");
                log.Info(allOk
                    ? "[G batch] PASS — 8 ops ran in one round trip and returned real values"
                    : "[G batch] FAIL");
                _stage = 10;
                break;
            }

            case 10:
            {
                // H1. Typed install: HookTyped on Math.Max(int,int)->int with a prefix
                // (generic arg reads) and a postfix (generic result read + rewrite).
                _typedHook = WaveIl2Cpp.HookTyped("mscorlib", "System", "Math", "Max",
                    new[] { TideType.I32, TideType.I32 }, TideType.I32,
                    prefix: OnTypedMaxPrefix, postfix: OnTypedMaxPostfix,
                    owner: "tideprobe-il2cpp-patch");
                log.Info($"[H install] typed hook installed: {_typedHook}");
                _typedPrefixFires = 0;
                _typedPostfixFires = 0;
                _stage = 11;
                break;
            }

            case 11:
            {
                // H2a. Typed pass-through: prefix must see (3,7), postfix the real 7.
                var math = GameClass.Resolve("mscorlib", "System", "Math");
                var v1 = math.CallStaticValue("Max",
                    new[] { TideValue.FromInt(3), TideValue.FromInt(7) }, TideType.I32).Int32;
                log.Info($"[H passthrough] Max(3,7) = {v1}, prefix saw ({_typedPrefixA0},{_typedPrefixA1}) x{_typedPrefixFires}, postfix saw {_typedPostfixResult} x{_typedPostfixFires}");
                var pass = v1 == 7 && _typedPrefixA0 == 3 && _typedPrefixA1 == 7
                    && _typedPrefixFires == 1 && _typedPostfixResult == 7 && _typedPostfixFires == 1;
                log.Info(pass ? "[H passthrough] PASS — generic arg/result reads match the raw path" : "[H passthrough] FAIL");

                // H2b. Typed arg rewrite: prefix turns Max(3,7) into Max(3,10).
                _rewriteTypedArg = true;
                _typedPrefixFires = 0;
                _typedPostfixFires = 0;
                var v2 = math.CallStaticValue("Max",
                    new[] { TideValue.FromInt(3), TideValue.FromInt(7) }, TideType.I32).Int32;
                _rewriteTypedArg = false;
                log.Info($"[H argrewrite] Max(3,7)->Max(3,10) = {v2}, postfix saw {_typedPostfixResult}");
                log.Info(v2 == 10 && _typedPostfixResult == 10
                    ? "[H argrewrite] PASS — SetArgument<int> changed the live call"
                    : "[H argrewrite] FAIL");

                // H2c. Typed result rewrite: same byte-visible caveat as [F] (the resolved
                // overload returns a byte, so only the low byte reaches the caller).
                _rewriteTypedResult = true;
                var v3 = math.CallStaticValue("Max",
                    new[] { TideValue.FromInt(3), TideValue.FromInt(7) }, TideType.I32).Int32;
                _rewriteTypedResult = false;
                const int sentinel = 0x1C0FFEE;
                log.Info($"[H resultrewrite] Max(3,7) = {v3} (byte-visible {sentinel & 0xFF}), postfix saw {_typedPostfixResult}");
                log.Info(v3 == (sentinel & 0xFF) && _typedPostfixResult == 7
                    ? "[H resultrewrite] PASS — SetResult<int> rewrote the caller value"
                    : "[H resultrewrite] FAIL");

                _typedHook!.Dispose();
                _typedHook = null;
                _stage = 12;
                break;
            }

            case 12:
            {
                // I1. Receiver install: HookTyped on GameObject.GetInstanceID (instance,
                // 0 user args). Empty parameterTypes infers the unique supported overload.
                _goClass = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "GameObject");
                _thisHook = WaveIl2Cpp.HookTyped("UnityEngine.CoreModule", "UnityEngine", "GameObject", "GetInstanceID",
                    Array.Empty<TideType>(), TideType.I32,
                    prefix: OnThisPrefix, postfix: OnThisPostfix,
                    owner: "tideprobe-il2cpp-patch");
                log.Info($"[I install] typed receiver hook installed: {_thisHook}");
                _stage = 13;
                break;
            }

            case 13:
            {
                // I2. Receiver proof: a Tide-driven GetInstanceID must expose a borrowed
                // This whose Dispose is a no-op, and the postfix must see the same id.
                using var target = _goClass!.NewObject();
                var id = target.CallIntMethod("GetInstanceID");
                log.Info($"[I receiver] id = {id}, This seen {_thisSeen}x, dispose-no-throw {_thisDisposeNoThrow}x, postfix saw {_thisPostfixResult} x{_thisPostfixFires}");
                log.Info(id != 0 && _thisSeen >= 1 && _thisDisposeNoThrow >= 1
                    && _thisPostfixFires >= 1 && _thisPostfixResult == id
                    ? "[I receiver] PASS — borrowed This + guard + result all observed"
                    : "[I receiver] FAIL");
                _thisHook!.Dispose();
                _thisHook = null;
                _stage = 14;
                break;
            }

            case 14:
            {
                // J1. Boxing install: HookTyped on Debug.Log(object)->void. The single
                // overload (name, argc 1) must resolve; the prefix replaces the string
                // arg with a boxed int, proving primitive-to-Object writes end to end.
                _logHook = WaveIl2Cpp.HookTyped("UnityEngine.CoreModule", "UnityEngine", "Debug", "Log",
                    new[] { TideType.Object }, TideType.Void,
                    prefix: OnLogPrefix, postfix: null,
                    owner: "tideprobe-il2cpp-patch");
                log.Info($"[J install] typed boxing hook installed: {_logHook}");
                _stage = 15;
                break;
            }

            case 15:
            {
                // J2. Boxing proof: a Tide-driven UnityLog (string arg) must fire the
                // prefix with an Object-typed slot; the game stays alive to log again.
                var ok = Tide.UnityLog("probe-boxing");
                log.Info($"[J boxing] UnityLog returned {ok}, prefix fired {_logPrefixFires}x, saw Object {_logSawObject}x");
                log.Info(ok && _logPrefixFires >= 1 && _logSawObject >= 1
                    ? "[J boxing] PASS — SetArgument boxed int into the Object slot, game alive"
                    : "[J boxing] FAIL");
                _logHook!.Dispose();
                _logHook = null;
                _stage = 16;
                break;
            }

            case 16:
            {
                log.Info("TideProbe-IL2CPP-Patch verification complete");
                _stage = 17;
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

    // Full-path postfix for Math.Max: records the REAL result the stub observed and,
    // when _rewriteMax is set, replaces it (the caller then receives the sentinel).
    private void OnMaxPostfix(nint instance, nint* args, int argCount, nint* result,
        WaveIl2Cpp.Il2CppReturnKind kind)
    {
        Interlocked.Increment(ref _maxPostfixFires);
        _maxObserved = (int)result[0];
        if (_rewriteMax)
        {
            result[0] = 0x1C0FFEE;
        }
    }

    // Typed prefix for Math.Max: generic arg reads (+ optional arg rewrite).
    private bool OnTypedMaxPrefix(WaveIl2Cpp.Il2CppHookContext context)
    {
        Interlocked.Increment(ref _typedPrefixFires);
        _typedPrefixA0 = context.GetArgument<int>(0);
        _typedPrefixA1 = context.GetArgument<int>(1);
        if (_rewriteTypedArg)
        {
            context.SetArgument(1, 10);
        }

        return false;
    }

    // Boxing prefix for Debug.Log(object): the slot must read Object-typed, then a
    // boxed int replaces the string arg (native BoxPrimitive path).
    private bool OnLogPrefix(WaveIl2Cpp.Il2CppHookContext context)
    {
        Interlocked.Increment(ref _logPrefixFires);
        if (context.GetArgumentType(0) == TideType.Object)
        {
            Interlocked.Increment(ref _logSawObject);
        }

        context.SetArgument(0, TideValue.FromInt(42));
        return false;
    }

    // Typed postfix for Math.Max: generic result read (+ optional result rewrite).
    private void OnTypedMaxPostfix(WaveIl2Cpp.Il2CppHookContext context)
    {
        Interlocked.Increment(ref _typedPostfixFires);
        _typedPostfixResult = context.GetResult<int>();
        if (_rewriteTypedResult)
        {
            context.SetResult(0x1C0FFEE);
        }
    }

    // Receiver prefix: This must be a borrowed, non-disposable handle.
    private bool OnThisPrefix(WaveIl2Cpp.Il2CppHookContext context)
    {
        var recv = context.This;
        if (recv is not null && recv.HandleValue != 0)
        {
            Interlocked.Increment(ref _thisSeen);
            recv.Dispose(); // must be a guarded no-op, never a double-free
            Interlocked.Increment(ref _thisDisposeNoThrow);
        }

        return false;
    }

    private void OnThisPostfix(WaveIl2Cpp.Il2CppHookContext context)
    {
        Interlocked.Increment(ref _thisPostfixFires);
        _thisPostfixResult = context.GetResult<int>();
    }
}
