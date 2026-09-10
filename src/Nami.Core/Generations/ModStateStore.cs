using System.Text.Json;
using Nami.Sdk;

namespace Nami.Core.Generations;

/// <summary>
/// Read-only snapshot of one mod's host-owned persisted state: an opaque JSON document plus its
/// schema version. Snapshots are detached immutable copies — later live writes never affect a
/// snapshot and vice versa. A null <see cref="Json"/> means the mod has no stored state.
/// </summary>
public sealed record ModStateSnapshot(string? Json, int SchemaVersion);

/// <summary>
/// Host-owned per-mod JSON state for the live runtime. State lives outside every ALC, so generation
/// replacement never takes it down with the old assemblies. Only plain JSON text plus a schema
/// version crosses this boundary (strings plus int): never <c>Type</c>, <c>MethodInfo</c>,
/// <c>Delegate</c>, <c>Task</c>, or live instances — the signatures make non-JSON unrepresentable.
/// </summary>
/// <remarks>
/// Validate-then-swap: every write validates its arguments and payload before swapping, so a bad
/// write throws with live state untouched. Migration runs against a read-only
/// <see cref="ModStateSnapshot"/> via <see cref="TryApplyMigratedState"/> and never touches the live
/// store; the staged result commits through <see cref="Commit"/> exactly once, together with the new
/// generation publication (the caller holds the coordinator transaction across that commit).
/// </remarks>
public sealed class ModStateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ModStateSnapshot> _states = new(StringComparer.Ordinal);

    /// <summary>Returns the stored JSON document, or null when the mod has no stored state.</summary>
    public string? Get(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (_gate)
        {
            return _states.TryGetValue(modId, out var snapshot) ? snapshot.Json : null;
        }
    }

    /// <summary>Returns the stored schema version (0 when nothing is stored).</summary>
    public int GetSchemaVersion(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (_gate)
        {
            return _states.TryGetValue(modId, out var snapshot) ? snapshot.SchemaVersion : 0;
        }
    }

    /// <summary>
    /// Replaces the stored state. Validates (non-empty id, non-null valid-JSON payload, non-negative
    /// version) before swapping; invalid input throws with live state untouched.
    /// </summary>
    public void Set(string modId, string json, int schemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentOutOfRangeException.ThrowIfNegative(schemaVersion);
        ValidateJson(modId, json);
        lock (_gate)
        {
            _states[modId] = new ModStateSnapshot(json, schemaVersion);
        }
    }

    /// <summary>Drops the stored state (no-op when absent).</summary>
    public void Clear(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (_gate)
        {
            _states.Remove(modId);
        }
    }

    /// <summary>Takes a detached read-only copy of one mod's state for migration staging.</summary>
    public ModStateSnapshot Snapshot(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        lock (_gate)
        {
            // The record is immutable with immutable members: sharing the instance is already detached.
            return _states.TryGetValue(modId, out var snapshot) ? snapshot : new ModStateSnapshot(null, 0);
        }
    }

    /// <summary>
    /// Commits a staged snapshot produced by <see cref="TryApplyMigratedState"/>. Validate-then-swap:
    /// invalid snapshots throw with live state untouched. A null-Json snapshot clears the entry.
    /// Called once per reload commit, together with the generation publication.
    /// </summary>
    public void Commit(string modId, ModStateSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Json is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(snapshot.SchemaVersion);
            ValidateJson(modId, snapshot.Json);
        }

        lock (_gate)
        {
            if (snapshot.Json is null)
            {
                _states.Remove(modId);
            }
            else
            {
                _states[modId] = snapshot;
            }
        }
    }

    /// <summary>
    /// Runs the candidate's <see cref="NamiPlugin.MigrateState"/> against a read-only snapshot and
    /// stages the result under <paramref name="targetVersion"/>. Never touches live state: the caller
    /// commits the staged snapshot via <see cref="Commit"/> together with the new generation, so a
    /// failed migration (throw, null, or invalid JSON) keeps the current state fully intact. Absent
    /// state (null Json) is a no-op success returning the snapshot unchanged. A null return from
    /// <c>MigrateState</c> is a failure — use <c>ClearState</c> to drop state explicitly.
    /// </summary>
    public static bool TryApplyMigratedState(
        ModStateSnapshot snapshot,
        NamiPlugin candidate,
        int targetVersion,
        out ModStateSnapshot? migrated,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentOutOfRangeException.ThrowIfNegative(targetVersion);

        if (snapshot.Json is null)
        {
            migrated = snapshot;
            error = null;
            return true;
        }

        string? result;
        try
        {
            result = candidate.MigrateState(snapshot.SchemaVersion, snapshot.Json);
        }
        catch (Exception ex)
        {
            migrated = null;
            error = $"MigrateState threw: {ex.GetBaseException().Message}";
            return false;
        }

        if (result is null)
        {
            migrated = null;
            error = "MigrateState returned null (use ClearState to drop state explicitly); current state kept.";
            return false;
        }

        try
        {
            ValidateJson("<migrated>", result);
        }
        catch (ArgumentException ex)
        {
            migrated = null;
            error = $"MigrateState produced invalid JSON: {ex.Message}";
            return false;
        }

        migrated = new ModStateSnapshot(result, targetVersion);
        error = null;
        return true;
    }

    /// <summary>
    /// Live per-mod <see cref="IModState"/> view for the current generation. The candidate under
    /// preparation never sees this: it migrates against a snapshot and commits with its generation.
    /// </summary>
    public IModState ForMod(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        return new LiveModState(this, modId);
    }

    private static void ValidateJson(string what, string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"State for '{what}' must be valid JSON: {ex.Message}", nameof(json));
        }
    }

    private sealed class LiveModState(ModStateStore store, string modId) : IModState
    {
        public int SchemaVersion => store.GetSchemaVersion(modId);
        public string? GetState() => store.Get(modId);
        public void SetState(string json, int schemaVersion) => store.Set(modId, json, schemaVersion);
        public void ClearState() => store.Clear(modId);
    }
}
