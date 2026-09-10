using System.Reflection;
using Nami.Sdk;

namespace Nami.Fixtures.Hostile;

/// <summary>
/// Hostile fixture for the live-runtime stress suites (Phase 7): exercises every retention
/// vector the reclamation path must survive — event subscription, timer, background task,
/// cancellation loop, Wave hook, Tide call, static state, delegate registration,
/// disposable tracking, slow shutdown, and startup failure. Each vector sits behind a
/// public static toggle (all default off except UseDisposable), with static
/// counters/flags the suite asserts on. Wave/Tide are touched via reflection so this
/// fixture keeps the siblings' Sdk-only reference shape; outcomes are recorded in
/// WaveHookInstalled/WaveHookError and TideCallAttempted/TideAvailable.
/// </summary>
/// <remarks>
/// Activation discipline (statics live in the ALC): a test-side type reference such as
/// <c>HostilePlugin.FailOnLoad = true</c> would set the default-context copy, NOT the loaded
/// generation's copy, so behavior switches are driven by environment variables read INSIDE the
/// ALC at activation time (<c>NAMI_HOSTILE_FAILONLOAD=1</c> throws in <c>OnLoad</c>;
/// <c>NAMI_HOSTILE_ONVALIDATE=0</c> makes <c>OnValidate</c> return false;
/// <c>NAMI_HOSTILE_SLOWUNLOAD_MS=&lt;n&gt;</c> sleeps that long in <c>OnUnload</c>;
/// <c>NAMI_HOSTILE_MIGRATE=throw|invalid</c> fails <c>MigrateState</c>). Tests set these with
/// try/finally; the public statics below stay as in-ALC observable counters only (read back
/// via reflection into the loaded assembly — never set from test code).
/// </remarks>
[NamiPlugin]
[PluginInfo("dev.nami.fixtures.hostile", "Hostile Fixture", "1.0.0", Description = "Test fixture: opt-in retention vectors for reload stress.")]
public sealed class HostilePlugin : NamiPlugin
{
    // In-ALC toggles (in-ALC sets only; tests drive behavior through NAMI_HOSTILE_* env vars —
    // test-side static sets would hit the default-context copy). Default off except UseDisposable.
    public static bool FailOnLoad;
    public static bool SlowUnload;
    public static int SlowUnloadMs = 1500;
    public static bool UseEventSubscription;
    public static bool UseTimer;
    public static bool UseBackgroundTask;
    public static bool UseCancellationLoop;
    public static bool UseWaveHook;
    public static bool UseTideCall;
    public static bool UseStaticState;
    public static bool UseDelegateRegistration;
    public static bool UseDisposable = true;

    // Observations.
    public static int UpdateCount;
    public static int EventFiredCount;
    public static int TimerFiredCount;
    public static int TaskIterations;
    public static int LoopIterations;
    public static int HookTargetCalls;
    public static int WaveHookObserved;
    public static bool WaveHookInstalled;
    public static string? WaveHookError;
    public static bool TideCallAttempted;
    public static bool TideAvailable;
    public static string? TideError;
    public static int DisposedCount;
    public static int UnloadedCount;

    /// <summary>Objects deliberately retained across reloads when <see cref="UseStaticState"/>.</summary>
    public static readonly List<object?> Retained = new();

    /// <summary>Delegate registrations that root the instance/ALC while subscribed.</summary>
    public static Action? RegisteredCallbacks;

    /// <summary>Hostile event chain: subscribed, never unsubscribed (the leak is the point).</summary>
    public static event Action? Ping;

    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;
    private Task? _loopTask;
    private TrackingDisposable? _disposable;

    /// <summary>Clears toggles, counters, and retained roots between suite cases.</summary>
    public static void Reset()
    {
        FailOnLoad = false;
        SlowUnload = false;
        SlowUnloadMs = 1500;
        UseEventSubscription = false;
        UseTimer = false;
        UseBackgroundTask = false;
        UseCancellationLoop = false;
        UseWaveHook = false;
        UseTideCall = false;
        UseStaticState = false;
        UseDelegateRegistration = false;
        UseDisposable = true;
        UpdateCount = 0;
        EventFiredCount = 0;
        TimerFiredCount = 0;
        TaskIterations = 0;
        LoopIterations = 0;
        HookTargetCalls = 0;
        WaveHookObserved = 0;
        WaveHookInstalled = false;
        WaveHookError = null;
        TideCallAttempted = false;
        TideAvailable = false;
        TideError = null;
        DisposedCount = 0;
        UnloadedCount = 0;
        Retained.Clear();
        RegisteredCallbacks = null;
        Ping = null;
    }

    /// <summary>Fires <see cref="Ping"/> for subscriber-count assertions.</summary>
    public static void RaisePing() => Ping?.Invoke();

    public override void OnLoad()
    {
        // Env read inside the ALC (see class remarks): test-side static sets would hit the
        // wrong copy. The static toggle still works for in-ALC sets.
        if (FailOnLoad || string.Equals(
                Environment.GetEnvironmentVariable("NAMI_HOSTILE_FAILONLOAD"), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("hostile startup failure (toggle)");
        }

        Context.Log.Info("hostile loaded");

        if (UseDisposable)
        {
            _disposable = new TrackingDisposable();
        }

        if (UseEventSubscription)
        {
            Ping += OnPing;
        }

        if (UseTimer)
        {
            _timer = new Timer(_ => Interlocked.Increment(ref TimerFiredCount), null, 0, 50);
        }

        if (UseBackgroundTask || UseCancellationLoop)
        {
            _cts = new CancellationTokenSource();
        }

        if (UseBackgroundTask)
        {
            var token = _cts!.Token;
            _backgroundTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref TaskIterations);
                    try
                    {
                        await Task.Delay(10, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        if (UseCancellationLoop)
        {
            var token = _cts!.Token;
            _loopTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref LoopIterations);
                    try
                    {
                        await Task.Delay(10, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        if (UseWaveHook)
        {
            TryInstallWaveHook();
        }

        if (UseTideCall)
        {
            TryTideCall();
        }

        if (UseStaticState)
        {
            Retained.Add(this);
        }

        if (UseDelegateRegistration)
        {
            RegisteredCallbacks += OnRegisteredCallback;
        }
    }

    public override void OnUpdate()
    {
        Interlocked.Increment(ref UpdateCount);
    }

    public override bool OnValidate()
    {
        // Read inside the ALC at validation time: NAMI_HOSTILE_ONVALIDATE=0 rejects the
        // candidate (the current generation keeps running).
        if (string.Equals(
                Environment.GetEnvironmentVariable("NAMI_HOSTILE_ONVALIDATE"), "0", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public override string? MigrateState(int fromVersion, string json)
    {
        // Read inside the ALC at migration time: throw|invalid fails the migration (current
        // state kept); anything else keeps the input as-is.
        var mode = Environment.GetEnvironmentVariable("NAMI_HOSTILE_MIGRATE");
        if (string.Equals(mode, "throw", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("hostile migration failure (env)");
        }

        if (string.Equals(mode, "invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "{not-json";
        }

        return json;
    }

    public override void OnUnload()
    {
        UnloadedCount++;
        // Env read inside the ALC at unload time: NAMI_HOSTILE_SLOWUNLOAD_MS=<n> forces a slow
        // shutdown of n ms (the static toggles still work for in-ALC sets).
        var slowUnload = SlowUnload;
        var slowUnloadMs = SlowUnloadMs;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("NAMI_HOSTILE_SLOWUNLOAD_MS"), out var envSlowMs))
        {
            slowUnload = true;
            slowUnloadMs = envSlowMs;
        }

        if (slowUnload)
        {
            Thread.Sleep(slowUnloadMs);
        }

        try
        {
            _cts?.Cancel();
            _backgroundTask?.Wait(TimeSpan.FromSeconds(2));
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Hostile shutdown must still reach disposal below.
        }
        finally
        {
            _cts?.Dispose();
        }

        _timer?.Dispose();
        _disposable?.Dispose();
        Context.Log.Info("hostile unloaded");
    }

    private void OnPing() => Interlocked.Increment(ref EventFiredCount);

    private void OnRegisteredCallback() => Interlocked.Increment(ref EventFiredCount);

    private static void HookTarget() => Interlocked.Increment(ref HookTargetCalls);

    private static void OnWaveObserved() => Interlocked.Increment(ref WaveHookObserved);

    private static void TryInstallWaveHook()
    {
        try
        {
            var waveType = Type.GetType("Nami.Wave.Wave, Nami.Wave");
            if (waveType is null)
            {
                WaveHookError = "Nami.Wave not loadable";
                return;
            }

            var target = (MethodBase)typeof(HostilePlugin).GetMethod(
                nameof(HookTarget), BindingFlags.Static | BindingFlags.NonPublic)!;
            var hook = waveType.GetMethod("Hook",
                new[] { typeof(MethodBase), typeof(string), typeof(Func<bool>), typeof(Action) });
            if (hook is null)
            {
                WaveHookError = "Wave.Hook shape not found";
                return;
            }

            hook.Invoke(null, new object?[] { target, "hostile", null, (Action)OnWaveObserved });
            WaveHookInstalled = true;
        }
        catch (Exception ex)
        {
            WaveHookError = ex.Message;
        }
    }

    private static void TryTideCall()
    {
        TideCallAttempted = true;
        try
        {
            var tideType = Type.GetType("Nami.Tide.Tide, Nami.Tide");
            if (tideType is null)
            {
                TideError = "Nami.Tide not loadable";
                return;
            }

            var available = tideType.GetProperty("IsAvailable", BindingFlags.Public | BindingFlags.Static);
            if (available is null)
            {
                TideError = "Tide.IsAvailable not found";
                return;
            }

            TideAvailable = (bool)available.GetValue(null)!;
        }
        catch (Exception ex)
        {
            TideError = ex.Message;
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Increment(ref DisposedCount);
            }
        }
    }
}
