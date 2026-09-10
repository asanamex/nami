using System.Diagnostics;
using Nami.Sdk;

namespace Nami.Core.Generations;

/// <summary>
/// Outcome of retiring one generation's tracked resources.
/// </summary>
/// <param name="Completed">True when every tracked item retired cleanly within budget (no survivors).</param>
/// <param name="Survivors">
/// Names of items that did not retire: tasks still running when the budget expired, disposal/cleanup
/// actions that threw, or subscriptions that failed to detach. Survivors may block ALC reclamation;
/// they are reported, never forcibly terminated.
/// </param>
public sealed record RetirementReport(bool Completed, IReadOnlyList<string> Survivors);

/// <summary>
/// Tracks everything a single mod generation owns so retirement can release it deterministically:
/// disposables, async disposables, background tasks, cleanup callbacks, and event subscriptions.
/// Release order is reverse registration (later registrations release first). Every registered action
/// runs at most once: the retirement walk and early-release handles share one claim flag, and
/// <see cref="DisposeAsync(TimeSpan)"/> is idempotent.
/// </summary>
/// <remarks>
/// Tracked vs untracked: only work handed to this instance retires deterministically. Anything else
/// is untracked, and Nami guarantees nothing about it:
/// <list type="bullet">
/// <item>a bare <c>new Timer()</c> never handed to <see cref="Own(IDisposable)"/>
/// (<see cref="System.Threading.Timer"/> is an <see cref="IDisposable"/>: hand it over to track it);</item>
/// <item>pending <c>RegisteredWaitHandle</c> callbacks still targeting the generation's assemblies;</item>
/// <item>mod-created strong <c>GCHandle</c>s (<c>Normal</c>/<c>Pinned</c>; <c>Weak</c> and
/// <c>WeakTrackResurrection</c> handles do not root their target and are excluded from this concern);</item>
/// <item>threads still running generation code — Nami never terminates threads or tasks;</item>
/// <item>references that stay generally reachable through ordinary GC reachability (for example values
/// still flowing with <c>AsyncLocal&lt;T&gt;</c>/<c>ExecutionContext</c>) keep their targets alive like
/// any other reachable reference; that is plain reachability, not a special root.</item>
/// </list>
/// Untracked leftovers surface as retention symptoms (the ALC never collects). Tracked items that fail
/// to retire are reported as <see cref="RetirementReport.Survivors"/> — reported, never force-reclaimed.
/// Cancellation is cooperative throughout: <see cref="Lifetime"/> signals retirement, waits end at the
/// budget, and abandoned work keeps running. No new timer types exist.
/// </remarks>
public sealed class GenerationResources
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _journal = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ILog _log;
    private int _sequence;
    private Task<RetirementReport>? _disposeTask;

    /// <summary>Creates resource tracking for one generation, logging through a hub-routed log.</summary>
    public GenerationResources(ILog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Canceled when this generation's retirement starts (<see cref="BeginRetirement"/> or
    /// <see cref="DisposeAsync(TimeSpan)"/>). Long-running mod work should observe it; cancellation
    /// is cooperative — Nami reports survivors but never terminates threads or tasks. The underlying
    /// source is never disposed: mod code may hold this token past reclamation.
    /// </summary>
    public CancellationToken Lifetime => _lifetimeCts.Token;

    /// <summary>Signals retirement start by canceling <see cref="Lifetime"/>. Idempotent.</summary>
    internal void BeginRetirement() => _lifetimeCts.Cancel();

    /// <summary>Live (unreleased) owned disposables; diagnostics for status.json.</summary>
    public int OwnedCount => CountLive(EntryKind.SyncDisposable, EntryKind.AsyncDisposable);

    /// <summary>Live (unfinished) tracked tasks; diagnostics for status.json.</summary>
    public int TrackedTaskCount => CountLive(EntryKind.TrackedTask);

    /// <summary>Live (unrun) cleanup callbacks; diagnostics for status.json.</summary>
    public int CleanupCount => CountLive(EntryKind.Cleanup);

    /// <summary>Live (attached) event subscriptions; diagnostics for status.json.</summary>
    public int SubscriptionCount => CountLive(EntryKind.Subscription);

    /// <summary>Takes ownership of a disposable (e.g. a <c>Timer</c>); disposed on the retirement path.</summary>
    public void Own(IDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);
        Add(new Entry(EntryKind.SyncDisposable, NameOf(disposable)) { Sync = disposable }, nameof(Own));
    }

    /// <summary>Takes ownership of an async disposable; disposed on the retirement path.</summary>
    public void Own(IAsyncDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);
        Add(new Entry(EntryKind.AsyncDisposable, NameOf(disposable)) { Async = disposable }, nameof(Own));
    }

    /// <summary>
    /// Tracks a background task so retirement waits for it (up to budget) and observes its outcome.
    /// Tracking never cancels or terminates the task: combine the task body with
    /// <see cref="Lifetime"/> for cooperative shutdown.
    /// </summary>
    public void Track(Task task, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        Add(new Entry(EntryKind.TrackedTask, string.IsNullOrWhiteSpace(name) ? NextName("task") : name)
        {
            Task = task,
        }, nameof(Track));
    }

    /// <summary>Registers a cleanup callback to run on the retirement path, exactly once.</summary>
    public void OnCleanup(Func<ValueTask> cleanup, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        Add(new Entry(EntryKind.Cleanup, string.IsNullOrWhiteSpace(name) ? NextName("cleanup") : name)
        {
            Cleanup = cleanup,
        }, nameof(OnCleanup));
    }

    /// <summary>
    /// Subscribes <paramref name="handler"/> and returns a handle that unsubscribes. Subscribe-once,
    /// cleanup-once: retirement detaches every still-attached handler, and detaching twice (handle
    /// plus retirement, in any order) removes exactly once. The handler is held strongly so the chain
    /// is deterministic and never depends on GC timing. Detach failures are logged, never thrown.
    /// </summary>
    public IDisposable Subscribe<T>(Action<Action<T>> add, Action<Action<T>> remove, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(handler);
        // Subscribe first: a throwing add reports to the caller and journals nothing.
        add(handler);
        // The closure captures handler strongly by construction: deterministic lifetime, no GC reliance.
        var entry = new Entry(EntryKind.Subscription, NextName($"event:{typeof(T).Name}"))
        {
            Unsubscribe = () => remove(handler),
        };
        Add(entry, nameof(Subscribe));
        return new Subscription(entry, _log);
    }

    /// <summary>
    /// Retires everything tracked, in reverse registration order. Idempotent: concurrent and repeat
    /// callers share one run. Each item is exception-isolated (failures are logged and listed as
    /// survivors); synchronous disposables run inline, while async disposal, cleanups, and tracked
    /// tasks are awaited up to the remaining <paramref name="budget"/>. Pass
    /// <see cref="Timeout.InfiniteTimeSpan"/> to wait without bound (diagnostics only, never per-frame).
    /// Cancels <see cref="Lifetime"/> first so cooperative work can observe retirement.
    /// </summary>
    public Task<RetirementReport> DisposeAsync(TimeSpan budget)
    {
        lock (_gate)
        {
            _disposeTask ??= RetireAsync(budget);
            return _disposeTask;
        }
    }

    private async Task<RetirementReport> RetireAsync(TimeSpan budget)
    {
        _lifetimeCts.Cancel();

        List<Entry> pending;
        lock (_gate)
        {
            pending = new List<Entry>(_journal);
        }

        pending.Reverse(); // Later registrations release first.

        var infinite = budget == Timeout.InfiniteTimeSpan;
        var started = Stopwatch.GetTimestamp();
        TimeSpan Remaining() => infinite ? Timeout.InfiniteTimeSpan : Max(TimeSpan.Zero, budget - Stopwatch.GetElapsedTime(started));

        var survivors = new List<string>();
        foreach (var entry in pending)
        {
            if (!entry.TryClaim())
            {
                continue; // Released early (e.g. unsubscribed handle): exactly-once.
            }

            try
            {
                var survivor = await RetireOneAsync(entry, Remaining, infinite).ConfigureAwait(false);
                if (survivor is not null)
                {
                    survivors.Add(survivor);
                }
            }
            catch (Exception ex)
            {
                // Belt-and-braces: RetireOneAsync isolates per-item failures itself; this guards the walk.
                _log.Error($"Resource cleanup '{entry.Name}' failed: {ex}");
                survivors.Add(entry.Name);
            }
        }

        return new RetirementReport(survivors.Count == 0, survivors.ToArray());
    }

    /// <summary>Retires one claimed entry; returns the survivor name, or null when fully retired.</summary>
    private async Task<string?> RetireOneAsync(Entry entry, Func<TimeSpan> remaining, bool infinite)
    {
        switch (entry.Kind)
        {
            case EntryKind.SyncDisposable:
                // Synchronous Dispose runs inline: a blocking implementation can overrun the budget.
                // Mod contract: Dispose must be prompt. Nami never abandons a sync dispose mid-call.
                try
                {
                    entry.Sync!.Dispose();
                    return null;
                }
                catch (Exception ex)
                {
                    _log.Error($"Disposal of '{entry.Name}' threw: {ex}");
                    return entry.Name;
                }

            case EntryKind.Subscription:
                try
                {
                    entry.Unsubscribe!();
                    return null;
                }
                catch (Exception ex)
                {
                    _log.Error($"Event detach '{entry.Name}' threw: {ex}");
                    return entry.Name;
                }

            case EntryKind.TrackedTask:
            {
                var task = entry.Task!;
                if (task.IsCompleted)
                {
                    ObserveSettledTask(task, entry.Name);
                    return null;
                }

                if (!await WaitForBudgetAsync(task, entry.Name, remaining, infinite).ConfigureAwait(false))
                {
                    return entry.Name;
                }

                ObserveSettledTask(task, entry.Name);
                return null;
            }

            case EntryKind.AsyncDisposable:
            {
                Task dispose;
                try
                {
                    dispose = entry.Async!.DisposeAsync().AsTask();
                }
                catch (Exception ex)
                {
                    _log.Error($"Async disposal of '{entry.Name}' threw: {ex}");
                    return entry.Name;
                }

                if (!await WaitForBudgetAsync(dispose, entry.Name, remaining, infinite).ConfigureAwait(false))
                {
                    return entry.Name;
                }

                ObserveSettledTask(dispose, entry.Name);
                return null;
            }

            default: // EntryKind.Cleanup
            {
                Task cleanup;
                try
                {
                    cleanup = entry.Cleanup!().AsTask();
                }
                catch (Exception ex)
                {
                    _log.Error($"Cleanup '{entry.Name}' threw: {ex}");
                    return entry.Name;
                }

                if (!await WaitForBudgetAsync(cleanup, entry.Name, remaining, infinite).ConfigureAwait(false))
                {
                    return entry.Name;
                }

                ObserveSettledTask(cleanup, entry.Name);
                return null;
            }
        }

        throw new InvalidOperationException($"Unknown resource kind '{entry.Kind}'.");
    }

    /// <summary>
    /// Waits for <paramref name="task"/> up to the remaining budget. True when settled (ran, faulted,
    /// or canceled — all observed and logged); false on budget expiry, leaving the task running
    /// cooperatively (a survivor that may block reclamation; never terminated).
    /// </summary>
    private async Task<bool> WaitForBudgetAsync(Task task, string name, Func<TimeSpan> remaining, bool infinite)
    {
        if (!task.IsCompleted)
        {
            if (infinite)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Resource '{name}' ended with {ex.GetType().Name}: {ex.Message}");
                }

                return true;
            }

            var wait = remaining();
            if (wait <= TimeSpan.Zero)
            {
                _log.Warn($"Resource '{name}' did not finish within the retirement budget; " +
                    "no longer waiting (it keeps running cooperatively and may block reclamation).");
                return false;
            }

            var winner = await Task.WhenAny(task, Task.Delay(wait)).ConfigureAwait(false);
            if (!ReferenceEquals(winner, task))
            {
                _log.Warn($"Resource '{name}' did not finish within the retirement budget; " +
                    "no longer waiting (it keeps running cooperatively and may block reclamation).");
                return false;
            }
        }

        return true;
    }

    /// <summary>Observes an already-settled task (marks exceptions observed) and logs its end state.</summary>
    private void ObserveSettledTask(Task task, string name)
    {
        if (task.IsFaulted)
        {
            _log.Warn($"Tracked task '{name}' ended faulted: {task.Exception?.GetBaseException().Message}");
        }
        else if (task.IsCanceled)
        {
            _log.Debug($"Tracked task '{name}' ended canceled.");
        }
    }

    private void Add(Entry entry, string operation)
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                throw new InvalidOperationException(
                    $"Cannot {operation} on a retiring generation: scheduling APIs " +
                    "(Own/Track/OnCleanup/Subscribe) are closed during teardown.");
            }

            _journal.Add(entry);
        }
    }

    private int CountLive(params EntryKind[] kinds)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var entry in _journal)
            {
                if (!entry.Settled && Array.IndexOf(kinds, entry.Kind) >= 0)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static string NameOf(object item) => item.GetType().FullName ?? item.GetType().Name;

    private string NextName(string prefix) => $"{prefix}#{Interlocked.Increment(ref _sequence)}";

    private enum EntryKind
    {
        SyncDisposable,
        AsyncDisposable,
        TrackedTask,
        Cleanup,
        Subscription,
    }

    private sealed class Entry(EntryKind kind, string name)
    {
        public EntryKind Kind { get; } = kind;
        public string Name { get; } = name;
        public IDisposable? Sync;
        public IAsyncDisposable? Async;
        public Task? Task;
        public Func<ValueTask>? Cleanup;
        public Action? Unsubscribe;
        private int _claimed;

        public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
        public bool Settled => _claimed != 0;
    }

    private sealed class Subscription(Entry entry, ILog log) : IDisposable
    {
        public void Dispose()
        {
            if (!entry.TryClaim())
            {
                return; // Cleanup-once: retirement already detached it.
            }

            try
            {
                entry.Unsubscribe?.Invoke();
            }
            catch (Exception ex)
            {
                log.Error($"Event detach '{entry.Name}' threw: {ex}");
            }
        }
    }
}
