using System.Diagnostics;
using System.Reflection;
using Nami.Core.Plugins;
using Nami.Sdk;

namespace Nami.Core.Generations;

/// <summary>
/// One staged dispatch-slot change carried from preparation to commit. The snapshot row
/// (<see cref="Slot"/>) and the ownership record (<see cref="Binding"/>) always travel
/// together; the Wave work decision (register new / rebuild / snapshot-only) is made at
/// commit time against the pre-commit snapshot under the commit gate, so concurrent
/// prepares can never act on a stale existence check.
/// </summary>
internal sealed class SlotStage
{
    public required string ModId { get; init; }
    public required string SlotKey { get; init; }
    public required PatchKind Kind { get; init; }
    public required DispatchSlot Slot { get; init; }
    public required WaveBinding Binding { get; init; }

    /// <summary>CoreCLR patch target for gate/observer/prefix/postfix/transpiler slots; null for IL2CPP.</summary>
    public MethodBase? Target { get; init; }

    /// <summary>
    /// Host-owned trampoline for gate/observer/prefix/postfix slots (same delegate type as the
    /// generation callback; reads the current snapshot, leases, invokes). Null for
    /// transpiler/IL2CPP slots, which register the generation callback directly.
    public Delegate? Trampoline { get; init; }
    /// <summary>True when the callback type lives outside every collectible ALC (register-once trampoline).</summary>
    public bool Stable { get; init; }

    /// <summary>
    /// Generation callback registered directly with Wave (transpiler + IL2CPP slots only).
    /// A transpiler value must be a <c>Nami.Wave.WaveTranspiler</c> at runtime; anything else
    /// fails preparation with a clear error instead of a Wave binding crash.
    /// </summary>
    public Delegate? DirectCallback { get; init; }

    public Delegate? DirectPostfix { get; init; }

    // IL2CPP address parts (IL2CPP slots only).
    public string? Il2CppAssembly { get; init; }
    public string? Il2CppNs { get; init; }
    public string? Il2CppKlass { get; init; }
    public string? Il2CppMethod { get; init; }
    public int Il2CppArgCount { get; init; }
    public int Il2CppReturnKind { get; init; }
    public IReadOnlyList<int>? Il2CppParameterTypes { get; init; }
    public int? Il2CppReturnType { get; init; }
}

/// <summary>
/// One fully prepared but unpublished candidate generation: ALC loaded, instance attached,
/// state migrated into staging, resources created, hooks recorded, validation passed.
/// Owned by exactly one <see cref="ReloadTransaction"/> until commit disposes or publishes it.
/// </summary>
internal sealed class PreparedCandidate
{
    public required PluginManifest Manifest { get; init; }
    public required PluginLoadContext LoadContext { get; init; }
    public required NamiPlugin Instance { get; init; }
    public required PluginContext Context { get; init; }
    public required ModGeneration Generation { get; init; }
    public required List<SlotStage> SlotStages { get; init; }
    public ModStateSnapshot? StagedState { get; set; }
    public int TargetStateVersion { get; set; }
}

/// <summary>
/// Coordinator transaction for one reload request. Preparation may run anywhere, but only one
/// commit mutates the runtime snapshot at a time (a single commit can atomically replace
/// several mods, so per-mod locking is insufficient). New requests bump
/// <see cref="ModRecord.PendingSequence"/>; uncommitted candidates are superseded at explicit
/// checkpoints (after load, after validate): a newer sequence disposes the candidate, logs
/// <c>superseded</c>, and restarts preparation. Convergence holds because every transaction
/// commits something strictly newer than what it replaced.
/// </summary>
internal sealed class ReloadTransaction
{
    internal ReloadTransaction(string modId, long sequence)
    {
        ModId = modId;
        Sequence = sequence;
        PrepTime = Stopwatch.StartNew();
    }

    /// <summary>Trigger mod id (the id the reload was requested for; the transaction may cover its dependents too).</summary>
    public string ModId { get; }

    /// <summary>Sequence captured from the record(s) at request time; checkpoints compare against live <see cref="ModRecord.PendingSequence"/>.</summary>
    public long Sequence { get; }

    /// <summary>Single-mod candidate (kept for the simple trigger-only shape; multi-mod graphs use <see cref="Prepared"/>).</summary>
    public ModGeneration? Candidate { get; internal set; }

    /// <summary>All ids this transaction will replace, in load order (trigger first, then dependents).</summary>
    public List<string> ReloadIds { get; } = new();

    /// <summary>Prepared candidates by mod id (case-insensitive).</summary>
    public Dictionary<string, PreparedCandidate> Prepared { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ids the new discovery no longer has a manifest for (file removed / disabled): retired without replacement.</summary>
    public List<string> Removed { get; } = new();

    /// <summary>Per-id rejection reasons for candidates that failed preparation or validation (old generation kept).</summary>
    public Dictionary<string, string> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per-id sequences captured at request time; commit re-checks each against live <see cref="ModRecord.PendingSequence"/>.</summary>
    public Dictionary<string, long> Sequences { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Pre-commit generation per id (null when the id was not loaded); history and retire use this.</summary>
    public Dictionary<string, ModGeneration?> Previous { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Ids to commit, in dependency (load) order.</summary>
    public List<string> CommitOrder { get; } = new();

    public Stopwatch PrepTime { get; }
    public DateTimeOffset? CommitTime { get; internal set; }
    public bool Superseded { get; internal set; }
}
