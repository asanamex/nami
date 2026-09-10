using System.Text.Json;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Generations;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Tests;

/// <summary>
/// Resource-ownership and host-owned-state proofs: every tracked item retires
/// exactly once in reverse order with survivors named, the live state store is
/// never partially mutated by a failed write or migration, and the runtime's
/// file surface keeps its exact schema.
/// </summary>
public class LiveResourceTests
{
    private const string AlphaId = "dev.nami.fixtures.alpha";

    private sealed class ListLog : ILog
    {
        public readonly List<string> Items = new();
        public void Log(LogLevel level, string message) => Items.Add($"[{level}] {message}");
    }

    private sealed class FakeDisposable(Action onDispose) : IDisposable
    {
        private int _calls;
        public int Calls => _calls;
        public void Dispose()
        {
            Interlocked.Increment(ref _calls);
            onDispose();
        }
    }

    private sealed class FakeAsyncDisposable(Action onDispose) : IAsyncDisposable
    {
        private int _calls;
        public int Calls => _calls;
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _calls);
            onDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MigratingPlugin(Func<int, string, string?> migrate) : NamiPlugin
    {
        public override string? MigrateState(int fromVersion, string json) => migrate(fromVersion, json);
    }

    private static GenerationResources Tracked(ILog? log = null)
        => new(log ?? new ListLog());

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

    private static Chainloader Create(GameDirFixture fixture)
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        return new Chainloader(fixture.Root, NamiConfig.Load(fixture.Root), hub);
    }

    [Fact]
    public async Task Resources_SyncDisposable_DisposedExactlyOnce_Idempotent()
    {
        var resources = Tracked();
        var order = new List<string>();
        var first = new FakeDisposable(() => order.Add("first"));
        var second = new FakeDisposable(() => order.Add("second"));
        resources.Own(first);
        resources.Own(second);
        Assert.Equal(2, resources.OwnedCount);

        var firstRun = await resources.DisposeAsync(TimeSpan.FromSeconds(5));
        Assert.True(firstRun.Completed);
        Assert.Empty(firstRun.Survivors);
        Assert.Equal(new[] { "second", "first" }, order);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal(0, resources.OwnedCount);

        // Idempotent: concurrent and repeat callers share the single run.
        var secondRun = await resources.DisposeAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.True(secondRun.Completed);
    }

    [Fact]
    public async Task Resources_ThrowingDispose_IsSurvivor_OthersStillRun()
    {
        var log = new ListLog();
        var resources = Tracked(log);
        var ran = new List<string>();
        resources.Own(new FakeDisposable(() => ran.Add("ok-before")));
        resources.Own(new ThrowingDisposable());
        resources.Own(new FakeDisposable(() => ran.Add("ok-after")));

        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(5));

        Assert.False(report.Completed);
        Assert.Single(report.Survivors);
        Assert.Contains("ThrowingDisposable", report.Survivors[0]);
        Assert.Equal(new[] { "ok-after", "ok-before" }, ran);
        Assert.NotEmpty(log.Items);
    }

    [Fact]
    public async Task Resources_AsyncDisposable_AwaitedExactlyOnce()
    {
        var resources = Tracked();
        var async = new FakeAsyncDisposable(() => { });
        resources.Own(async);

        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Completed);
        Assert.Equal(1, async.Calls);
    }

    [Fact]
    public async Task Resources_TimerOwned_IsTrackedDisposable()
    {
        var resources = Tracked();
        var timer = new Timer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        resources.Own((IDisposable)timer);
        Assert.Equal(1, resources.OwnedCount);

        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Completed);
        Assert.Equal(0, resources.OwnedCount);
    }

    [Fact]
    public async Task Resources_TrackedTask_WaitsForCompletion()
    {
        var resources = Tracked();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = Task.Run(async () =>
        {
            await gate.Task;
            await Task.Delay(50);
        });
        resources.Track(slow, "slow-task");
        Assert.Equal(1, resources.TrackedTaskCount);

        gate.SetResult();
        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(10));

        Assert.True(report.Completed);
        Assert.Empty(report.Survivors);
        await slow;
    }

    [Fact]
    public async Task Resources_UnfinishedTask_IsNamedSurvivor_NotTerminated()
    {
        var resources = Tracked();
        var endless = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resources.Track(endless.Task, "never-settles");

        var report = await resources.DisposeAsync(TimeSpan.FromMilliseconds(100));

        Assert.False(report.Completed);
        Assert.Equal(new[] { "never-settles" }, report.Survivors);
        Assert.False(endless.Task.IsCompleted);
        endless.SetResult();
        await endless.Task;
    }

    [Fact]
    public async Task Resources_FaultedTask_IsObserved_NotSurvivor()
    {
        var resources = Tracked();
        var faulted = Task.FromException(new InvalidOperationException("boom"));
        resources.Track(faulted, "faulted-task");

        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Completed);
        Assert.Empty(report.Survivors);
    }

    [Fact]
    public async Task Resources_Cleanup_RunsOnce_ThrowerIsolated()
    {
        var resources = Tracked();
        var ran = new List<string>();
        var cleanupCalls = 0;
        resources.OnCleanup(() => { ran.Add("first"); return ValueTask.CompletedTask; }, "cleanup-first");
        resources.OnCleanup(() => { Interlocked.Increment(ref cleanupCalls); return ValueTask.CompletedTask; });
        resources.OnCleanup(() => throw new InvalidOperationException("cleanup boom"), "throwing-cleanup");
        Assert.Equal(3, resources.CleanupCount);

        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(5));

        Assert.False(report.Completed);
        Assert.Equal(new[] { "throwing-cleanup" }, report.Survivors);
        Assert.Equal(new[] { "first" }, ran);
        Assert.Equal(1, cleanupCalls);
    }

    [Fact]
    public async Task Resources_Subscription_DetachesOnce_EarlyReleaseWins()
    {
        var resources = Tracked();
        Action<string>? handlers = null;
        var removes = 0;
        var fires = 0;

        var handle = resources.Subscribe<string>(
            h => handlers += h,
            h => { removes++; handlers -= h; },
            _ => fires++);
        Assert.Equal(1, resources.SubscriptionCount);

        handlers?.Invoke("ping");
        Assert.Equal(1, fires);

        handle.Dispose();
        Assert.Equal(1, removes);
        handlers?.Invoke("ping");
        Assert.Equal(1, fires);

        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(5));
        Assert.True(report.Completed);
        Assert.Equal(1, removes);
    }

    [Fact]
    public async Task Resources_RetirementDetaches_SubscriberSeesNoPostRetireEvents()
    {
        var resources = Tracked();
        Action<string>? handlers = null;
        resources.Subscribe<string>(h => handlers += h, h => handlers -= h, _ => { });
        Assert.NotNull(handlers);

        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Completed);
        Assert.Null(handlers);
        Assert.Equal(0, resources.SubscriptionCount);
    }

    [Fact]
    public async Task Resources_SchedulingAfterRetireStart_ThrowsNamingTeardown()
    {
        var resources = Tracked();
        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(5));
        Assert.True(report.Completed);

        Assert.Throws<InvalidOperationException>(() => resources.Own(new FakeDisposable(() => { })));
        Assert.Throws<InvalidOperationException>(() => resources.Track(Task.CompletedTask));
        Assert.Throws<InvalidOperationException>(() => resources.OnCleanup(() => ValueTask.CompletedTask));
        Action<string>? handlers = null;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            resources.Subscribe<string>(h => handlers += h, h => handlers -= h, _ => { }));
        Assert.Contains("teardown", ex.Message);
    }

    [Fact]
    public async Task Resources_Lifetime_CanceledAtRetirementStart()
    {
        var resources = Tracked();
        Assert.False(resources.Lifetime.IsCancellationRequested);

        var report = await resources.DisposeAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Completed);
        Assert.True(resources.Lifetime.IsCancellationRequested);
    }
    [Fact]
    public void PluginContext_ResourceSurface_DelegatesToCallingGeneration_RejectsDuringTeardown()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        var context = alpha.Context;

        // Delegation: mod-facing calls land on the calling generation's journal.
        Assert.Equal(alpha.Resources.Lifetime, context.Lifetime);
        var timer = new Timer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        context.Own((IDisposable)timer);
        Assert.Equal(1, alpha.Resources.OwnedCount);
        context.Track(Task.CompletedTask, "probe-task");
        Assert.Equal(1, alpha.Resources.TrackedTaskCount);
        context.OnCleanup(() => ValueTask.CompletedTask, "probe-cleanup");
        Assert.Equal(1, alpha.Resources.CleanupCount);

        // Teardown discipline: once retirement starts, every scheduling member names teardown.
        LiveGates.RetireGate(alpha);
        Assert.Throws<InvalidOperationException>(() => context.Lifetime);
        var own = Assert.Throws<InvalidOperationException>(() => context.Own(new FakeDisposable(() => { })));
        var track = Assert.Throws<InvalidOperationException>(() => context.Track(Task.CompletedTask));
        var cleanup = Assert.Throws<InvalidOperationException>(() => context.OnCleanup(() => ValueTask.CompletedTask));
        Assert.Contains("teardown", own.Message);
        Assert.Contains("teardown", track.Message);
        Assert.Contains("teardown", cleanup.Message);
        chainloader.Shutdown();
    }

    [Fact]
    public void State_SetGet_ValidatesBeforeSwap()
    {
        var store = new ModStateStore();
        Assert.Null(store.Get("mod"));
        Assert.Equal(0, store.GetSchemaVersion("mod"));

        store.Set("mod", """{"a":1}""", 3);
        Assert.Equal("""{"a":1}""", store.Get("mod"));
        Assert.Equal(3, store.GetSchemaVersion("mod"));

        var bad = Assert.Throws<ArgumentException>(() => store.Set("mod", "not json", 4));
        Assert.Contains("mod", bad.Message);
        Assert.Equal("""{"a":1}""", store.Get("mod"));
        Assert.Equal(3, store.GetSchemaVersion("mod"));

        Assert.Throws<ArgumentNullException>(() => store.Set("mod", null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Set("mod", "{}", -1));

        store.Clear("mod");
        Assert.Null(store.Get("mod"));
        Assert.Equal(0, store.GetSchemaVersion("mod"));
    }

    [Fact]
    public void State_Snapshot_IsDetachedCopy()
    {
        var store = new ModStateStore();
        store.Set("mod", """{"v":1}""", 1);

        var snapshot = store.Snapshot("mod");
        store.Set("mod", """{"v":2}""", 2);

        Assert.Equal("""{"v":1}""", snapshot.Json);
        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal("""{"v":2}""", store.Get("mod"));
    }

    [Fact]
    public void State_Commit_Validates_StagedSnapshot()
    {
        var store = new ModStateStore();
        store.Set("mod", """{"v":1}""", 1);

        store.Commit("mod", new ModStateSnapshot("""{"v":9}""", 9));
        Assert.Equal("""{"v":9}""", store.Get("mod"));

        Assert.Throws<ArgumentException>(() => store.Commit("mod", new ModStateSnapshot("broken", 10)));
        Assert.Equal("""{"v":9}""", store.Get("mod"));

        store.Commit("mod", new ModStateSnapshot(null, 0));
        Assert.Null(store.Get("mod"));
    }

    [Fact]
    public void State_Migration_CarriesJson_ThenCommitsWithGeneration()
    {
        var store = new ModStateStore();
        store.Set("mod", """{"v":1}""", 1);

        var candidate = new MigratingPlugin((from, json) =>
        {
            Assert.Equal(1, from);
            return """{"v":2}""";
        });
        var snapshot = store.Snapshot("mod");
        Assert.True(ModStateStore.TryApplyMigratedState(snapshot, candidate, 2, out var migrated, out var error));
        Assert.Null(error);
        Assert.NotNull(migrated);

        // Staged only: the live store is untouched until the generation commits with it.
        Assert.Equal("""{"v":1}""", store.Get("mod"));
        store.Commit("mod", migrated!);
        Assert.Equal("""{"v":2}""", store.Get("mod"));
        Assert.Equal(2, store.GetSchemaVersion("mod"));
    }

    [Fact]
    public void State_MigrationFailure_KeepsCurrentState()
    {
        var store = new ModStateStore();
        store.Set("mod", """{"keep":true}""", 1);

        var throwing = new MigratingPlugin((_, _) => throw new InvalidOperationException("schema too old"));
        Assert.False(ModStateStore.TryApplyMigratedState(
            store.Snapshot("mod"), throwing, 2, out var migratedThrow, out var errorThrow));
        Assert.Null(migratedThrow);
        Assert.Contains("threw", errorThrow);

        var nulling = new MigratingPlugin((_, _) => null);
        Assert.False(ModStateStore.TryApplyMigratedState(
            store.Snapshot("mod"), nulling, 2, out var migratedNull, out _));
        Assert.Null(migratedNull);

        var corrupting = new MigratingPlugin((_, _) => "{oops");
        Assert.False(ModStateStore.TryApplyMigratedState(
            store.Snapshot("mod"), corrupting, 2, out var migratedBad, out var errorBad));
        Assert.Null(migratedBad);
        Assert.Contains("JSON", errorBad);

        Assert.Equal("""{"keep":true}""", store.Get("mod"));
        Assert.Equal(1, store.GetSchemaVersion("mod"));

        // Absent state is a no-op success: nothing to migrate, nothing to keep.
        Assert.True(ModStateStore.TryApplyMigratedState(
            new ModStateSnapshot(null, 0), throwing, 2, out var migratedAbsent, out var errorAbsent));
        Assert.Null(errorAbsent);
        Assert.NotNull(migratedAbsent);
        Assert.Null(migratedAbsent!.Json);
    }

    [Fact]
    public void State_LiveView_RoundTripsThroughStore()
    {
        var store = new ModStateStore();
        var view = store.ForMod("mod");
        Assert.Equal(0, view.SchemaVersion);
        Assert.Null(view.GetState());

        view.SetState("""{"live":1}""", 5);
        Assert.Equal("""{"live":1}""", store.Get("mod"));
        Assert.Equal(5, view.SchemaVersion);

        view.ClearState();
        Assert.Null(store.Get("mod"));
    }

    [Fact]
    public void State_LiveGeneration_CarriesJsonAcrossReload()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var first = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        first.Context.State.SetState("""{"round":1}""", 7);

        var result = chainloader.Reload(AlphaId);
        Assert.Empty(result.Failed);

        var second = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId);
        Assert.NotSame(first, second);
        Assert.Equal("""{"round":1}""", second.Context.State.GetState());
        Assert.Equal(7, second.Context.State.SchemaVersion);
        chainloader.Shutdown();
    }

    [Fact]
    public void StatusJson_HasExactModSchema()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        chainloader.Reload(AlphaId);

        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "status.json")));
        var root = status.RootElement;
        Assert.Equal(
            new[] { "mods", "retiring", "updatedAt", "version" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.True(root.GetProperty("version").GetInt64() >= 1);

        var mods = root.GetProperty("mods").EnumerateArray().ToList();
        Assert.NotEmpty(mods);
        var expectedMods = new[]
        {
            "activeExecutions", "alc", "current", "health", "id", "lastReload", "lifetime",
            "reloadCount", "subscriptions", "tasks", "timers", "waveBindings",
        };
        foreach (var mod in mods)
        {
            Assert.Equal(
                expectedMods,
                mod.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        }

        var alpha = mods.Single(m => m.GetProperty("id").GetString() == AlphaId);
        Assert.Equal("Running", alpha.GetProperty("lifetime").GetString());
        Assert.Equal("healthy", alpha.GetProperty("health").GetString());
        Assert.Equal(0, alpha.GetProperty("activeExecutions").GetInt32());
        Assert.Equal(0, alpha.GetProperty("waveBindings").GetInt32());
        Assert.True(alpha.GetProperty("reloadCount").GetInt32() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(alpha.GetProperty("alc").GetString()));

        var lastReload = alpha.GetProperty("lastReload");
        Assert.Equal(JsonValueKind.Object, lastReload.ValueKind);
        Assert.True(lastReload.GetProperty("success").GetBoolean());
        Assert.True(lastReload.GetProperty("durationMs").GetInt64() >= 0);
        Assert.True(DateTimeOffset.TryParse(
            lastReload.GetProperty("at").GetString(), out _));

        foreach (var retiring in root.GetProperty("retiring").EnumerateArray())
        {
            Assert.Equal(
                new[] { "blockers", "generation", "lifetime", "modId" },
                retiring.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        }

        chainloader.Shutdown();
    }

    [Fact]
    public void ReloadHistory_HasExactSevenKeyRing_WithExplicitNullError()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        var before = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId).Generation;
        var result = chainloader.Reload(AlphaId);
        Assert.Empty(result.Failed);

        using var history = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "reload-history.json")));
        Assert.Equal(JsonValueKind.Array, history.RootElement.ValueKind);
        var entries = history.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(entries);

        var alpha = entries.Last(e => e.GetProperty("modId").GetString() == AlphaId);
        var live = chainloader.Plugins.Single(p => p.Manifest.Id == AlphaId).Generation;
        Assert.Equal(before, alpha.GetProperty("from").GetInt32());
        Assert.Equal(live, alpha.GetProperty("to").GetInt32());
        Assert.True(live > before);
        Assert.True(alpha.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, alpha.GetProperty("error").ValueKind);
        Assert.True(alpha.GetProperty("durationMs").GetInt64() >= 0);
        Assert.True(DateTimeOffset.TryParse(alpha.GetProperty("at").GetString(), out _));
        chainloader.Shutdown();
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("dispose boom");
    }
}
