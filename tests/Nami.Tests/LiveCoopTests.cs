using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Generations;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Tests;

/// <summary>
/// Realistic-mod cooperation proof for the live mod runtime, exercised through the Coop
/// presence-tracker fixture the way a real author would write it (background worker honoring
/// <c>Lifetime</c>, owned timer, plain event += with <c>OnCleanup</c> unsubscribe,
/// <c>OnCleanup</c> state flush, JSON state carry, <c>MigrateState</c> schema bump):
/// a cooperative reload retires cleanly with state carried and migrated, while a generation
/// whose loop ignores cancellation survives as a named survivor that blocks reclamation until
/// released — the honest boundary, shown with a real task rather than a unit stub.
/// Location note: this lives under tests/fixtures (not samples/) because only fixtures are
/// build-wired into Nami.Tests via ProjectReference, which is what puts CoopPlugin.dll next
/// to the test host for the copy-into-mods precedent below.
/// </summary>
public class LiveCoopTests
{
    private const string CoopId = "dev.nami.fixtures.coop";

    private sealed record CoopResult(
        string[] Reloaded,
        string[] Failed,
        int V1Generation,
        int V2Generation,
        int V1Updates,
        int V1Events,
        int V1Beats,
        int V1Flushes,
        int V1Unload,
        int V1CancelObserved,
        int V1HeartbeatDone,
        int V1TimerDisposed,
        int V1EventAttached,
        int V1MigrateCalls,
        bool V1ReportCompleted,
        string[] V1ReportSurvivors,
        int CarriedTicks,
        string? CarriedSession,
        int CarriedVersion,
        int V2MigrateCalls,
        string? V2SessionId,
        int V2Updates,
        int FinalTicks,
        string? FinalSession,
        int FinalVersion,
        int V2Unload,
        int V2CancelObserved,
        int V2TimerDisposed,
        bool V2ReportCompleted,
        string[] V2ReportSurvivors,
        WeakReference V1Weak,
        WeakReference V2Weak);

    private sealed record HostileCoopData(
        int V1Generation,
        string[] Reloaded,
        string[] Failed,
        bool ReportCompleted,
        string[] ReportSurvivors,
        int CancelObserved,
        int HeartbeatDone,
        int UnloadCount,
        int TimerDisposed,
        int EventAttached,
        string[] StatusBlockers);

    private sealed record IgnoringRun(
        HostileCoopData Data,
        WeakReference V1Weak,
        Chainloader Loader,
        GameDirFixture Fixture);

    [Fact]
    public void CooperativeReload_CarriesState_RetiresCleanly_ReclaimsBothGenerations()
    {
        var result = RunCooperativeReload();

        Assert.Equal(new[] { CoopId }, result.Reloaded);
        Assert.Empty(result.Failed);
        Assert.True(result.V2Generation > result.V1Generation);

        // v1 served ticks before retirement: 5 driven updates, 5 events, live heartbeat/timer.
        Assert.Equal(5, result.V1Updates);
        Assert.Equal(5, result.V1Events);
        Assert.True(result.V1Beats > 0);
        Assert.True(result.V1Flushes > 0);

        // v1 retired exactly once and cooperatively: OnUnload once, cancellation observed,
        // heartbeat done, timer disposed once, event detached, no migration on first boot.
        Assert.Equal(1, result.V1Unload);
        Assert.Equal(1, result.V1CancelObserved);
        Assert.Equal(1, result.V1HeartbeatDone);
        Assert.Equal(1, result.V1TimerDisposed);
        Assert.Equal(0, result.V1EventAttached);
        Assert.Equal(0, result.V1MigrateCalls);
        Assert.True(result.V1ReportCompleted);
        Assert.Empty(result.V1ReportSurvivors);

        // State carried across the reload with the v1->v2 schema bump applied.
        Assert.Equal(5, result.CarriedTicks);
        Assert.Equal("coop-session-2", result.CarriedSession);
        Assert.Equal(2, result.CarriedVersion);
        Assert.Equal(1, result.V2MigrateCalls);
        Assert.Equal("coop-session-2", result.V2SessionId);

        // v2 serves from the carried base: 3 more ticks continue the count, shape preserved.
        Assert.Equal(3, result.V2Updates);
        Assert.Equal(8, result.FinalTicks);
        Assert.Equal("coop-session-2", result.FinalSession);
        Assert.Equal(2, result.FinalVersion);

        // v2 shuts down as cleanly as v1 retired.
        Assert.Equal(1, result.V2Unload);
        Assert.Equal(1, result.V2CancelObserved);
        Assert.Equal(1, result.V2TimerDisposed);
        Assert.True(result.V2ReportCompleted);
        Assert.Empty(result.V2ReportSurvivors);

        // Neither retired context is rooted anymore.
        AssertCollected(result.V1Weak, "v1");
        AssertCollected(result.V2Weak, "v2");
    }

    [Fact]
    public async Task IgnoringLoop_SurvivesRetirement_AsNamedSurvivor_BlocksReclamationUntilReleased()
    {
        // Env try/finally (Hostile precedent): the candidate reads these inside its own ALC,
        // and same-class placement keeps these env tests sequential.
        SetCoopEnv(schema: "1", session: null, ignoreCancellation: "1", release: null);
        try
        {
            var run = RunIgnoringReload();

            // The reload itself succeeds (the survivor never fails the publication); the
            // retirement report names the ignoring task, which never observed cancellation.
            Assert.Equal(new[] { CoopId }, run.Data.Reloaded);
            Assert.Empty(run.Data.Failed);
            Assert.False(run.Data.ReportCompleted);
            Assert.Contains("coop-heartbeat", run.Data.ReportSurvivors);
            Assert.Equal(0, run.Data.CancelObserved);
            Assert.Equal(0, run.Data.HeartbeatDone);

            // Teardown order still holds around the survivor: OnUnload ran once, the owned
            // timer was disposed once, the event detached — only the task survived.
            Assert.Equal(1, run.Data.UnloadCount);
            Assert.Equal(1, run.Data.TimerDisposed);
            Assert.Equal(0, run.Data.EventAttached);

            // status.json reports the same blocker on the retiring record.
            Assert.Contains(run.Data.StatusBlockers, b => b.Contains("coop-heartbeat"));

            // The running task anchors its ALC past Unload(): observably still alive.
            AssertStillAlive(run.V1Weak);

            // Releasing the loop lets both generations' loops exit; shutdown then reclaims.
            // The final collection assert runs in its own frame so only the weak crosses it.
            await Task.Delay(100); // Lets the pre-release heartbeat visibly run first.
            ReleaseAndCollect(run.Loader, run.Fixture, run.V1Weak);
        }
        finally
        {
            SetCoopEnv(schema: null, session: null, ignoreCancellation: null, release: null);
        }
    }

    /// <summary>
    /// Runs the whole cooperative scenario in a dead-on-return frame so no test local can
    /// root a retired generation across the collection loop. Returns detached values (ints,
    /// strings, string arrays) plus weak ALC trackers only — never generations, instances,
    /// or ALC-loaded types.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CoopResult RunCooperativeReload()
    {
        SetCoopEnv(schema: "1", session: null, ignoreCancellation: null, release: null);
        try
        {
            using var fixture = new GameDirFixture();
            fixture.WriteConfig();
            CopyPlugins(fixture, "CoopPlugin.dll");

            var hub = new LogHub { MinimumLevel = LogLevel.Warn };
            var chainloader = new Chainloader(fixture.Root, NamiConfig.Load(fixture.Root), hub);
            chainloader.LoadAll();

            var v1 = chainloader.Plugins.Single(p => p.Manifest.Id == CoopId);
            var v1Gen = v1.Generation;
            for (var i = 0; i < 5; i++)
            {
                chainloader.UpdateAll();
            }

            WaitForCoopCounter(v1, "Heartbeats", TimeSpan.FromSeconds(10));
            WaitForCoopCounter(v1, "TimerFlushes", TimeSpan.FromSeconds(10));
            var v1Updates = GetCoopInt(v1, "UpdateCount");
            var v1Events = GetCoopInt(v1, "EventObserved");
            var v1Beats = GetCoopInt(v1, "Heartbeats");
            var v1Flushes = GetCoopInt(v1, "TimerFlushes");
            var v1MigrateCalls = GetCoopInt(v1, "MigrateCalls");
            var v1Weak = LiveGates.AlcWeakOf(v1);

            // v2 boots under schema 2 with a named session: same DLL bytes, new generation.
            SetCoopEnv(schema: "2", session: "coop-session-2", ignoreCancellation: null, release: null);
            var reload = chainloader.Reload(CoopId);
            var v2 = chainloader.Plugins.Single(p => p.Manifest.Id == CoopId);

            var v1Outcome = RetirementOutcome(chainloader, CoopId, v1Gen);
            var v1Unload = GetCoopInt(v1, "UnloadCount");
            var v1Cancel = GetCoopInt(v1, "CancelObserved");
            var v1Done = GetCoopInt(v1, "HeartbeatDone");
            var v1Disposed = GetCoopInt(v1, "TimerDisposed");
            var v1Attached = GetCoopInt(v1, "EventAttached");
            var v1EventsAfter = GetCoopInt(v1, "EventObserved");

            var carried = ReadPresence(v2.Context.State.GetState());
            var carriedVersion = v2.Context.State.SchemaVersion;
            var v2MigrateCalls = GetCoopInt(v2, "MigrateCalls");
            var v2SessionId = GetCoopString(v2, "SessionId");

            for (var i = 0; i < 3; i++)
            {
                chainloader.UpdateAll();
            }

            var v2Updates = GetCoopInt(v2, "UpdateCount");
            var final = ReadPresence(v2.Context.State.GetState());
            var finalVersion = v2.Context.State.SchemaVersion;

            chainloader.Shutdown();
            // Shutdown retires but leaves the chainloader registered in the static TideMetrics
            // registry (and record.Current pointing at v2): Dispose unregisters, so the whole
            // island — loader, records, retired generations — becomes collectible together.
            chainloader.Dispose();
            var v2Outcome = RetirementOutcome(chainloader, CoopId, v2.Generation);
            var v2Unload = GetCoopInt(v2, "UnloadCount");
            var v2Cancel = GetCoopInt(v2, "CancelObserved");
            var v2Disposed = GetCoopInt(v2, "TimerDisposed");
            var v2Weak = LiveGates.AlcWeakOf(v2);

            Assert.Equal(v1Events, v1EventsAfter); // No new v1 execution after retirement.
            return new CoopResult(
                reload.Reloaded.ToArray(),
                reload.Failed.ToArray(),
                v1Gen,
                v2.Generation,
                v1Updates,
                v1Events,
                v1Beats,
                v1Flushes,
                v1Unload,
                v1Cancel,
                v1Done,
                v1Disposed,
                v1Attached,
                v1MigrateCalls,
                v1Outcome.Completed,
                v1Outcome.Survivors.ToArray(),
                carried.Ticks,
                carried.Session,
                carriedVersion,
                v2MigrateCalls,
                v2SessionId,
                v2Updates,
                final.Ticks,
                final.Session,
                finalVersion,
                v2Unload,
                v2Cancel,
                v2Disposed,
                v2Outcome.Completed,
                v2Outcome.Survivors.ToArray(),
                v1Weak,
                v2Weak);
        }
        finally
        {
            SetCoopEnv(schema: null, session: null, ignoreCancellation: null, release: null);
        }
    }

    /// <summary>
    /// Loads a cancellation-ignoring v1 and reloads it. Returns the host handles (which never
    /// root the retired ALC) for the release phase alongside the detached observations.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IgnoringRun RunIgnoringReload()
    {
        var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "CoopPlugin.dll");

        var hub = new LogHub { MinimumLevel = LogLevel.Warn };
        var chainloader = new Chainloader(fixture.Root, NamiConfig.Load(fixture.Root), hub);
        chainloader.LoadAll();

        var v1 = chainloader.Plugins.Single(p => p.Manifest.Id == CoopId);
        var v1Gen = v1.Generation;
        for (var i = 0; i < 3; i++)
        {
            chainloader.UpdateAll();
        }

        WaitForCoopCounter(v1, "Heartbeats", TimeSpan.FromSeconds(10));

        var reload = chainloader.Reload(CoopId);
        var outcome = RetirementOutcome(chainloader, CoopId, v1Gen);
        var data = new HostileCoopData(
            v1Gen,
            reload.Reloaded.ToArray(),
            reload.Failed.ToArray(),
            outcome.Completed,
            outcome.Survivors.ToArray(),
            GetCoopInt(v1, "CancelObserved"),
            GetCoopInt(v1, "HeartbeatDone"),
            GetCoopInt(v1, "UnloadCount"),
            GetCoopInt(v1, "TimerDisposed"),
            GetCoopInt(v1, "EventAttached"),
            ReadRetiringBlockers(fixture.Root, CoopId, v1Gen));
        var weak = LiveGates.AlcWeakOf(v1);
        return new IgnoringRun(data, weak, chainloader, fixture);
    }

    /// <summary>
    /// Releases the ignoring loops, shuts down, disposes the fixture, and proves the retired
    /// ALC was anchored only by its running task. Own frame: only the weak crosses it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReleaseAndCollect(Chainloader loader, GameDirFixture fixture, WeakReference v1Weak)
    {
        Environment.SetEnvironmentVariable("NAMI_COOP_RELEASE", "1");
        Task.Delay(1000).GetAwaiter().GetResult();
        loader.Shutdown();
        loader.Dispose();
        fixture.Dispose();
        LiveGates.CollectUntilGone([v1Weak]);
        Assert.False(
            v1Weak.IsAlive,
            "coop v1 ALC survived collection after its ignoring task exited; the task was not the only anchor.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertStillAlive(WeakReference tracker)
    {
        LiveGates.CollectUntilGone([tracker]);
        Assert.True(tracker.IsAlive, "coop v1 ALC collected while its ignoring task still runs; the anchor is gone.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCollected(WeakReference tracker, string label)
    {
        LiveGates.CollectUntilGone([tracker]);
        Assert.False(tracker.IsAlive, $"coop {label} ALC survived collection; a retired reference is still rooted.");
    }

    private static void SetCoopEnv(string? schema, string? session, string? ignoreCancellation, string? release)
    {
        Environment.SetEnvironmentVariable("NAMI_COOP_SCHEMA", schema);
        Environment.SetEnvironmentVariable("NAMI_COOP_SESSION", session);
        Environment.SetEnvironmentVariable("NAMI_COOP_IGNORE_CANCELLATION", ignoreCancellation);
        Environment.SetEnvironmentVariable("NAMI_COOP_RELEASE", release);
    }

    private static void CopyPlugins(GameDirFixture fixture, params string[] pluginDlls)
    {
        Directory.CreateDirectory(fixture.ModsDir);
        var outputDir = AppContext.BaseDirectory;
        foreach (var pluginDll in pluginDlls)
        {
            File.Copy(Path.Combine(outputDir, pluginDll), Path.Combine(fixture.ModsDir, pluginDll));
        }

        File.Copy(Path.Combine(outputDir, "Nami.Sdk.dll"), Path.Combine(fixture.ModsDir, "Nami.Sdk.dll"));
    }

    private static object? GetCoopStatic(ModGeneration generation, string name)
    {
        // Resolves the field on the ALC-loaded type, so reads hit the generation's own
        // statics — never the default-context copy a test-side type reference would reach.
        var field = generation.Instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Coop field '{name}' not found.");
        return field.GetValue(null);
    }

    private static int GetCoopInt(ModGeneration generation, string name) => (int)GetCoopStatic(generation, name)!;

    private static string? GetCoopString(ModGeneration generation, string name) => (string?)GetCoopStatic(generation, name);

    private static void WaitForCoopCounter(ModGeneration generation, string field, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (GetCoopInt(generation, field) > 0)
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.Fail($"Timed out waiting for coop {field} > 0.");
    }

    private static RetirementReport RetirementOutcome(Chainloader loader, string modId, int generationNumber)
    {
        var prop = typeof(Chainloader).GetProperty(
                "RetirementRecords", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Chainloader.RetirementRecords not found.");
        var records = (System.Collections.IEnumerable)prop.GetValue(loader)!;
        foreach (var record in records)
        {
            var type = record.GetType();
            var id = (string)type.GetProperty("ModId")!.GetValue(record)!;
            var gen = (int)type.GetProperty("GenerationId")!.GetValue(record)!;
            if (string.Equals(id, modId, StringComparison.OrdinalIgnoreCase) && gen == generationNumber)
            {
                return type.GetProperty("Outcome")!.GetValue(record) as RetirementReport
                    ?? throw new InvalidOperationException(
                        $"No retirement outcome for '{modId}' generation {generationNumber}.");
            }
        }

        throw new InvalidOperationException($"No retirement record for '{modId}' generation {generationNumber}.");
    }

    private static (int Ticks, int Flushes, string? Session) ReadPresence(string? json)
    {
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (
            root.GetProperty("ticks").GetInt32(),
            root.GetProperty("flushes").GetInt32(),
            root.TryGetProperty("session", out var session) ? session.GetString() : null);
    }

    private static string[] ReadRetiringBlockers(string root, string modId, int generationNumber)
    {
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "status.json")));
        var retiring = status.RootElement.GetProperty("retiring").EnumerateArray().ToList();
        var entry = retiring.Single(
            e => e.GetProperty("modId").GetString() == modId &&
                e.GetProperty("generation").GetInt32() == generationNumber);
        return entry.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()!).ToArray();
    }
}
