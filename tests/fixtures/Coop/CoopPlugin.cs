using Nami.Sdk;

namespace Nami.Fixtures.Coop;

/// <summary>
/// Presence tracker: the realistic cooperation sample for the live mod runtime, written the way
/// a real author would write it. Each tick counts into host-owned JSON state, a background task
/// loops on <see cref="IPluginContext.Lifetime"/> writing heartbeats, an owned timer flushes
/// periodically, a presence event is wired with plain += and an <c>OnCleanup</c> unsubscribe
/// (<c>Subscribe</c> lives on the host-side <c>GenerationResources</c> and is not surfaced on
/// <see cref="IPluginContext"/>, so it is unreachable from mod code), <c>OnCleanup</c> flushes
/// final state, and <c>MigrateState</c> handles the v1 to v2 schema bump (adds <c>session</c>).
/// </summary>
/// <remarks>
/// Activation discipline (statics live in the ALC): a test-side type reference such as
/// <c>CoopPlugin.UpdateCount</c> would read the default-context copy, NOT the loaded
/// generation's copy. Behavior switches are driven by environment variables read INSIDE the
/// ALC (<c>NAMI_COOP_SCHEMA</c> = current schema, default 1; <c>NAMI_COOP_SESSION</c> = session
/// name for schema 2, default fresh id; <c>NAMI_COOP_IGNORE_CANCELLATION</c>=1 makes the
/// heartbeat loop ignore <c>Lifetime</c>, exiting only on <c>NAMI_COOP_RELEASE</c>=1). The
/// public statics below stay as in-ALC observable counters only (read back via reflection into
/// the loaded assembly — never set from test code).
/// </remarks>
[NamiPlugin]
[PluginInfo("dev.nami.fixtures.coop", "Coop Fixture", "1.0.0", Description = "Test fixture: realistic cooperation sample (presence tracker).")]
public sealed class CoopPlugin : NamiPlugin
{
    // In-ALC observable counters (in-ALC reads/writes only; tests read them back via
    // reflection on the generation's own type and never assign them test-side).
    public static int UpdateCount;
    public static int Heartbeats;
    public static int CancelObserved;
    public static int HeartbeatDone;
    public static int TimerFlushes;
    public static int TimerDisposed;
    public static int UnloadCount;
    public static int MigrateCalls;
    public static int EventObserved;
    public static int EventAttached;
    public static string? SessionId;

    /// <summary>Presence event chain: subscribed on load, detached by the retirement cleanup.</summary>
    public static event Action? PresenceChanged;

    private readonly object _gate = new();
    private int _ticks;
    private int _flushes;
    private int _schema = 1;
    private string? _session;
    private bool _ignoreCancellation;

    public override void OnLoad()
    {
        // Env read inside the ALC (see class remarks).
        _schema = CurrentSchema();
        _ignoreCancellation = string.Equals(
            Environment.GetEnvironmentVariable("NAMI_COOP_IGNORE_CANCELLATION"), "1", StringComparison.Ordinal);

        // Capture the calling generation's lifetime early: reading it during teardown throws.
        var lifetime = Context.Lifetime;

        // Restore persisted presence carried from the previous generation (if any).
        _ticks = TryGetInt(Context.State.GetState(), "ticks");
        _flushes = TryGetInt(Context.State.GetState(), "flushes");
        _session = TryGetString(Context.State.GetState(), "session");
        if (string.IsNullOrEmpty(_session) || Context.State.SchemaVersion < _schema)
        {
            _session = SanitizeSession(Environment.GetEnvironmentVariable("NAMI_COOP_SESSION"))
                ?? Guid.NewGuid().ToString("N")[..8];
            lock (_gate)
            {
                PersistLocked();
            }
        }

        SessionId = _session;

        // Background heartbeat, cooperative via Lifetime (tracked so retirement waits for it).
        // The loop body is static and captures nothing instance-bound: only the host-side
        // cancellation token crosses into it.
        Context.Track(HeartbeatLoopAsync(lifetime, _ignoreCancellation), "coop-heartbeat");

        // Periodic flush timer, owned by the calling generation (disposed on its retire path).
        var timer = new FlushTimer(TickFlush);
        Context.Own(timer);

        // Subscribe lives on the host-side GenerationResources, not on IPluginContext, so a
        // real author wires plain += with an OnCleanup unsubscribe. The handler is static, so
        // the subscription never captures the instance.
        PresenceChanged += OnPresenceChanged;
        EventAttached = 1;
        Context.OnCleanup(() =>
        {
            PresenceChanged -= OnPresenceChanged;
            EventAttached = 0;
            Flush();
            return ValueTask.CompletedTask;
        }, "coop-presence-cleanup");

        Context.Log.Info($"coop loaded (schema {_schema}, session {_session})");
    }

    public override void OnUpdate()
    {
        Interlocked.Increment(ref UpdateCount);
        lock (_gate)
        {
            _ticks++;
            PersistLocked();
        }

        PresenceChanged?.Invoke();
    }

    public override string? MigrateState(int fromVersion, string json)
    {
        Interlocked.Increment(ref MigrateCalls);
        // Runs inside the candidate's ALC against a read-only snapshot: pure JSON in/out.
        // Only reshape when this build actually carries the newer schema; a same-schema
        // reload keeps the document byte-identical so the host stages nothing new.
        if (fromVersion < 2 && CurrentSchema() >= 2)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ticks = root.TryGetProperty("ticks", out var t) && t.TryGetInt32(out var n) ? n : 0;
            var flushes = root.TryGetProperty("flushes", out var f) && f.TryGetInt32(out var m) ? m : 0;
            return $$"""{"ticks":{{ticks}},"flushes":{{flushes}},"session":"migrated"}""";
        }

        return json;
    }

    public override void OnUnload()
    {
        Interlocked.Increment(ref UnloadCount);
        Context.Log.Info("coop unloaded");
    }

    private static void OnPresenceChanged() => Interlocked.Increment(ref EventObserved);

    private void TickFlush()
    {
        lock (_gate)
        {
            _flushes++;
            Interlocked.Increment(ref TimerFlushes);
            PersistLocked();
        }
    }

    private void Flush()
    {
        lock (_gate)
        {
            PersistLocked();
        }
    }

    /// <summary>
    /// Merges (never blind-overwrites) the host-owned document. Retirement runs strictly
    /// post-publication, so by the time our cleanup flush runs the live store may already hold
    /// the successor's document: max-merge keeps ticks monotonic and preserves fields (such as
    /// the v2 session) this generation never owned. Callers hold <see cref="_gate"/>.
    /// </summary>
    private void PersistLocked()
    {
        var stored = Context.State.GetState();
        var storedVersion = Context.State.SchemaVersion;
        var ticks = Math.Max(TryGetInt(stored, "ticks"), _ticks);
        var flushes = Math.Max(TryGetInt(stored, "flushes"), _flushes);
        var storedSession = TryGetString(stored, "session");
        string? session = _schema >= 2 ? _session : null;
        if (storedSession is not null && (session is null || storedVersion > _schema))
        {
            session = storedSession;
        }

        var version = Math.Max(_schema, storedVersion);
        Context.State.SetState(
            session is null
                ? $$"""{"ticks":{{ticks}},"flushes":{{flushes}}}"""
                : $$"""{"ticks":{{ticks}},"flushes":{{flushes}},"session":"{{session}}"}""",
            version);
    }

    /// <summary>
    /// Cooperative heartbeat: observes <paramref name="lifetime"/> and exits when retirement
    /// starts. Static with no instance captures, so the tracked task never extends the
    /// generation's reach beyond the host-side token. Dishonest mode (env
    /// <c>NAMI_COOP_IGNORE_CANCELLATION</c>=1) never consults the token and exits only on
    /// <c>NAMI_COOP_RELEASE</c>=1, modeling work that refuses to cooperate.
    /// </summary>
    private static async Task HeartbeatLoopAsync(CancellationToken lifetime, bool ignoreCancellation)
    {
        try
        {
            if (ignoreCancellation)
            {
                while (!string.Equals(
                    Environment.GetEnvironmentVariable("NAMI_COOP_RELEASE"), "1", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref Heartbeats);
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }
            else
            {
                while (!lifetime.IsCancellationRequested)
                {
                    Interlocked.Increment(ref Heartbeats);
                    await Task.Delay(10, lifetime).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref CancelObserved, 1);
        }
        finally
        {
            if (!ignoreCancellation && lifetime.IsCancellationRequested)
            {
                Interlocked.Exchange(ref CancelObserved, 1);
            }

            Interlocked.Exchange(ref HeartbeatDone, 1);
        }
    }

    private static int CurrentSchema()
        => int.TryParse(Environment.GetEnvironmentVariable("NAMI_COOP_SCHEMA"), out var v) && v >= 1 ? v : 1;

    private static string? SanitizeSession(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var clean = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length == 0 ? null : clean;
    }

    private static int TryGetInt(string? json, string property)
    {
        try
        {
            if (json is null)
            {
                return 0;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var element) &&
                element.TryGetInt32(out var value)
                ? value
                : 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return 0;
        }
    }

    private static string? TryGetString(string? json, string property)
    {
        try
        {
            if (json is null)
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var element) &&
                element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ownable periodic flusher: disposing stops the timer and records exactly-once disposal
    /// in the generation's ALC. Holds only the flush delegate; the generation's disposal walk
    /// drops it together with the retired generation.
    /// </summary>
    private sealed class FlushTimer : IDisposable
    {
        private readonly Action _flush;
        private readonly Timer _timer;
        private int _disposed;

        public FlushTimer(Action flush)
        {
            _flush = flush;
            _timer = new Timer(_ => _flush(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _timer.Dispose();
                Interlocked.Increment(ref TimerDisposed);
            }
        }
    }
}
