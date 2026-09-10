using System.Text.Json;
using Nami.Core.Generations;
using Nami.Sdk;

namespace Nami.Core;

/// <summary>
/// Live-runtime file surface: <c>status.json</c> (transitions only) and the
/// <c>reload-history.json</c> ring (last 50). Host-owned DTOs only, camelCase; write failures are
/// swallowed — a status write never fails a reload. Paths match the CLI readers:
/// <c>&lt;root&gt;/status.json</c>, <c>&lt;root&gt;/reload-history.json</c>.
/// Locking: takes <c>_gate</c> briefly; never takes <c>_commitGate</c> (commit paths call in
/// <c>_commitGate → _gate</c> order).
/// </summary>
public sealed partial class Chainloader
{
    private static readonly JsonSerializerOptions StatusJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Bounded history ring (last 50) plus per-mod last-reload cells for status.json.
    private readonly List<HistoryEntry> _history = new();
    private readonly Dictionary<string, LastReload> _lastReloads = new(StringComparer.OrdinalIgnoreCase);

    private sealed record HistoryEntry(
        string ModId, int From, int To, bool Success, string? Error, long DurationMs, string At);

    private sealed record LastReload(bool Success, long DurationMs, string At);

    private void RecordHistory(string modId, int from, int to, bool success, string? error, TimeSpan duration)
    {
        var at = DateTimeOffset.UtcNow.ToString("o");
        var durationMs = (long)duration.TotalMilliseconds;
        lock (_gate)
        {
            _history.Add(new HistoryEntry(modId, from, to, success, error, durationMs, at));
            while (_history.Count > 50)
            {
                _history.RemoveAt(0);
            }

            _lastReloads[modId] = new LastReload(success, durationMs, at);
        }

        PersistHistory();
    }

    private void PersistHistory()
    {
        try
        {
            List<HistoryEntry> snapshot;
            lock (_gate)
            {
                snapshot = new List<HistoryEntry>(_history);
            }

            File.WriteAllText(
                Path.Combine(_rootDirectory, "reload-history.json"),
                JsonSerializer.Serialize(snapshot, StatusJson));
        }
        catch
        {
            // A status write never fails a reload.
        }
    }

    /// <summary>
    /// Writes <c>status.json</c> for the current transition. Best-effort and exception-free by
    /// contract; callers invoke on transitions only (commit, retire, quarantine, reclamation,
    /// shutdown) — never per-frame.
    /// </summary>
    private void WriteStatus()
    {
        try
        {
            var snapshot = Volatile.Read(ref _currentSnapshot);
            List<object> mods = new();
            foreach (var generation in snapshot.Generations.Values)
            {
                var resources = generation.Resources;
                LastReload? last = null;
                lock (_gate)
                {
                    _lastReloads.TryGetValue(generation.Manifest.Id, out last);
                }

                mods.Add(new
                {
                    id = generation.Manifest.Id,
                    current = generation.Generation,
                    lifetime = generation.Lifetime.ToString(),
                    health = HealthOf(generation),
                    activeExecutions = generation.ActiveExecutions,
                    // No separate timer tracking exists (a Timer is an IDisposable): owned
                    // disposables — timers included — are reported as timers.
                    tasks = resources?.TrackedTaskCount ?? 0,
                    timers = resources?.OwnedCount ?? 0,
                    subscriptions = resources?.SubscriptionCount ?? 0,
                    waveBindings = generation.HookBindings.Count,
                    reloadCount = generation.ReloadCount,
                    lastReload = last is null
                        ? null
                        : new { success = last.Success, durationMs = last.DurationMs, at = last.At },
                    // Never dereferences blindly: a Failed record must never carry a null
                    // LoadContext (see PrepareCandidate), but a runtime-null here must degrade
                    // to a marker, never NRE inside this swallowed try (which would lose the
                    // whole file, not just the entry).
                    alc = generation.LoadContext is { } alcContext ? alcContext.Name ?? "<unnamed>" : "<no-context>",
                });
            }

            List<object> retiring = new();
            lock (_gate)
            {
                foreach (var state in _retiring)
                {
                    var generation = state.Generation;
                    var blockers = new List<string>();
                    var active = generation.ActiveExecutions;
                    if (active > 0)
                    {
                        blockers.Add($"{active} active execution(s)");
                    }

                    retiring.Add(new
                    {
                        modId = generation.Manifest.Id,
                        generation = generation.Generation,
                        lifetime = generation.Lifetime.ToString(),
                        blockers,
                    });
                }

                foreach (var info in _reclaiming.Where(i => !i.ObservedCollected))
                {
                    retiring.Add(new
                    {
                        modId = info.ModId,
                        generation = info.GenerationId,
                        lifetime = info.Lifetime,
                        blockers = info.Blockers,
                    });
                }
            }

            var root = new
            {
                version = snapshot.Version,
                updatedAt = DateTimeOffset.UtcNow.ToString("o"),
                mods,
                retiring,
            };

            File.WriteAllText(
                Path.Combine(_rootDirectory, "status.json"),
                JsonSerializer.Serialize(root, StatusJson));
        }
        catch
        {
            // A status write never fails a reload.
        }
    }
}
