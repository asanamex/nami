using System.Reflection;

namespace Nami.Core.Generations;

/// <summary>
/// Which Wave patch shape a dispatch slot carries. Mirrors the Wave entry point the
/// slot was registered through. Wave patch semantics are never changed by the host;
/// only the callback a stable trampoline dispatches to is swapped.
/// </summary>
public enum PatchKind
{
    /// <summary>The gate half of <c>Wave.Hook</c>: a <c>Func&lt;bool&gt;</c> vote. Same frozen scope as <c>Wave.Hook</c> (parameterless void targets only).</summary>
    CallbackGate,
    /// <summary>The observer half of <c>Wave.Hook</c>: an <c>Action</c> run on every call.</summary>
    CallbackObserver,
    /// <summary>The <c>prefix</c> argument of <c>Wave.Patch</c>.</summary>
    CallbackPrefix,
    /// <summary>The <c>postfix</c> argument of <c>Wave.Patch</c>.</summary>
    CallbackPostfix,
    /// <summary>
    /// The <c>transpiler</c> argument of <c>Wave.Patch</c>. Never participates in callback
    /// swaps: the generated body is the artifact, so a new body is prepared and published
    /// while the old body stays valid for in-flight execution under retire, drain, reclaim.
    /// </summary>
    Transpiler,
    /// <summary><c>WaveIl2Cpp.Hook</c>: fast-path native detour (up to 4 register arguments).</summary>
    Il2CppHook,
    /// <summary><c>WaveIl2Cpp.HookFull</c>: full-path native detour (results plus stack arguments).</summary>
    Il2CppFull,
    /// <summary><c>WaveIl2Cpp.HookTyped</c>: typed full-path native detour.</summary>
    Il2CppTyped,
}

/// <summary>
/// How a binding's callback is wired to its persistent patch entry, snapshotted once at
/// registration and never re-queried. The callback must be provably separable from the
/// persistent entry for a slot to classify as live-dispatchable: a callback baked into
/// generated code never does.
/// </summary>
public enum BindingMechanism
{
    /// <summary>Stable trampoline reads the current snapshot and dispatches to the current callback; the persistent Wave entry never changes on swap.</summary>
    TrampolineDispatch,
    /// <summary>Callback baked into a generated body (IL-copy site or transpiled body): swapping the callback means rebuilding the artifact at commit.</summary>
    BakedIntoGeneratedBody,
    /// <summary>Stable native entry point separate from the managed callback (IL2CPP dispatch stubs). Mechanically separable, operationally unproven.</summary>
    NativeEntry,
}

/// <summary>
/// What a generation swap may do with a binding. Reported, never forced: the host swaps
/// what classifies live, rebuilds what needs it at commit, and refuses the rest.
/// </summary>
public enum ReloadCapability
{
    /// <summary>Generation swap is a snapshot publish; zero Wave rebuild, zero native rewrite.</summary>
    FullyLiveReloadable,
    /// <summary>Swap is safe only at a host safe point (commit drain / Tide safe point); no rebuild needed.</summary>
    SafePointRequired,
    /// <summary>Swap re-registers at commit (<c>Unpatch</c> old plus <c>Patch</c> new); the native rewrite is accepted and logged.</summary>
    RequiresPatchRebuild,
    /// <summary>Not swappable live; reported to the operator, never attempted. Recovery is a new validated transaction, never reactivation without proof.</summary>
    RestartRequired,
    /// <summary>Backend or kind the host does not support at all. Never produced by <see cref="GenerationPatchClassifier.Classify"/>; set explicitly by host policy.</summary>
    Unsupported,
}

/// <summary>
/// Eligibility of one generation's binding. Minimal by design: eligibility flips once per
/// swap, after the new snapshot publishes; callback references drop only after quiesce.
/// </summary>
public enum BindingState
{
    /// <summary>Eligible for new execution via the current snapshot.</summary>
    Active,
    /// <summary>Removed from eligibility: receives no new execution. The callback reference stays alive until quiescence drains in-flight leases, then is released.</summary>
    Retired,
}

/// <summary>
/// One generation's ownership record for one dispatch slot.
/// Which generation: (<c>ModId</c>, <c>GenerationId</c>) names the exact candidate generation
/// that registered it, so per-generation health (failures, quarantine, last error) never taints siblings.
/// Is-current: a binding is current while <see cref="BindingState"/> is <see cref="BindingState.Active"/>
/// and the current snapshot resolves the slot to this generation; snapshot resolution is the
/// is-current check, never this record alone.
/// Safe-to-invoke: only a callback resolved from the current snapshot under an execution lease
/// (see <see cref="GenerationDispatcher.ResolveCurrent"/>); never invoke a retired binding directly.
/// Retire order per slot: publish the new snapshot first, then mark the old binding retired
/// (removing it from eligibility), then drop the old callback reference only after quiescence.
/// </summary>
/// <param name="ModId">Logical mod id (also the Wave owner prefix).</param>
/// <param name="GenerationId">Owning generation number within the mod.</param>
/// <param name="SlotKey">Mod-scoped slot name; unique per mod.</param>
/// <param name="Kind">Patch shape; mirrors the Wave entry point.</param>
/// <param name="Mechanism">Wiring snapshotted once at registration; never re-queried.</param>
/// <param name="Capability">Classified reload behavior; reported, never forced.</param>
/// <param name="State">Eligibility; flips Active to Retired once per swap (use <c>with</c>: records stay immutable).</param>
/// <param name="Owner">Host Wave owner id (<c>$"nami:gen:{modId}:{slotKey}"</c>); registered with Wave exactly once per slot.</param>
public sealed record WaveBinding(
    string ModId,
    int GenerationId,
    string SlotKey,
    PatchKind Kind,
    BindingMechanism Mechanism,
    ReloadCapability Capability,
    BindingState State,
    string Owner);

/// <summary>
/// The live entry for one slot in the snapshot dispatch map. Slots live in the immutable
/// <c>RuntimeSnapshot</c> (map owned by the snapshot; this record is the value type).
/// Every dispatch reads the current snapshot once, so readers see exactly one snapshot and
/// a reload can never expose a mixed graph (A=v2/B=v1/C=v2). A held pre-commit reference
/// still resolves the old generation after publish: snapshot immutability, not locking.
/// </summary>
/// <param name="ModId">Logical mod id owning the slot.</param>
/// <param name="SlotKey">Mod-scoped slot name; unique per mod.</param>
/// <param name="Kind">Patch shape; mirrors the Wave entry point.</param>
/// <param name="Current">Current generation's callback; invoke only under an execution lease acquired through the same atomic gate as retirement.</param>
/// <param name="CurrentGeneration">Generation number that supplied <paramref name="Current"/>.</param>
public sealed record DispatchSlot(
    string ModId,
    string SlotKey,
    PatchKind Kind,
    Delegate Current,
    int CurrentGeneration);

/// <summary>
/// Pure capability rules for Wave bindings. The binding implementation must prove the
/// callback separable from the persistent patch entry: a callback baked into generated
/// code never classifies as live-dispatchable.
/// </summary>
public static class GenerationPatchClassifier
{
    /// <summary>
    /// Classifies a binding. Gate/observer/prefix/postfix via <see cref="BindingMechanism.TrampolineDispatch"/>
    /// is <see cref="ReloadCapability.FullyLiveReloadable"/>; any <see cref="PatchKind.Transpiler"/> or
    /// <see cref="BindingMechanism.BakedIntoGeneratedBody"/> is <see cref="ReloadCapability.RequiresPatchRebuild"/>;
    /// any IL2CPP kind, IL2CPP backend, or <see cref="BindingMechanism.NativeEntry"/> is at minimum
    /// <see cref="ReloadCapability.SafePointRequired"/>; a null (unknown: no site yet) mechanism is
    /// <see cref="ReloadCapability.RestartRequired"/> (reported, never forced). Anything else unmatched
    /// is <see cref="ReloadCapability.RestartRequired"/>. Never returns <see cref="ReloadCapability.Unsupported"/>;
    /// hosts set that explicitly by policy.
    /// </summary>
    /// <param name="kind">Patch shape of the slot.</param>
    /// <param name="isIl2CppBackend">True when the game runs the IL2CPP backend.</param>
    /// <param name="mechanism">Wiring snapshotted at registration; null when no site exists yet or the engine is unknown.</param>
    public static ReloadCapability Classify(PatchKind kind, bool isIl2CppBackend, BindingMechanism? mechanism)
    {
        // No site yet (or an unknown engine): nothing proven separable. Report, never force.
        if (mechanism is null)
        {
            return ReloadCapability.RestartRequired;
        }

        // A transpiler-bearing body is a generated artifact, not a swappable callback;
        // anything baked into generated code rebuilds at commit (Unpatch old plus Patch new).
        if (kind == PatchKind.Transpiler || mechanism == BindingMechanism.BakedIntoGeneratedBody)
        {
            return ReloadCapability.RequiresPatchRebuild;
        }

        // Native dispatch is at minimum a safe-point swap: a stable native entry proven
        // separable from its managed callback never rebuilds for an ordinary callback swap.
        // IL2CPP separability is mechanically present but operationally unproven, so hosts
        // report RestartRequired for these slots until a proven-separable entry exists.
        if (isIl2CppBackend
            || kind is PatchKind.Il2CppHook or PatchKind.Il2CppFull or PatchKind.Il2CppTyped
            || mechanism == BindingMechanism.NativeEntry)
        {
            return ReloadCapability.SafePointRequired;
        }

        // Plain managed callbacks via a stable trampoline swap with a snapshot publish.
        return kind switch
        {
            PatchKind.CallbackGate or PatchKind.CallbackObserver or PatchKind.CallbackPrefix or PatchKind.CallbackPostfix
                when mechanism == BindingMechanism.TrampolineDispatch => ReloadCapability.FullyLiveReloadable,
            _ => ReloadCapability.RestartRequired,
        };
    }

    /// <summary>
    /// Classifies a binding with a known mechanism. Same rules as <see cref="Classify(PatchKind, bool, BindingMechanism?)"/>.
    /// </summary>
    /// <param name="kind">Patch shape of the slot.</param>
    /// <param name="isIl2CppBackend">True when the game runs the IL2CPP backend.</param>
    /// <param name="mechanism">Wiring snapshotted at registration.</param>
    public static ReloadCapability Classify(PatchKind kind, bool isIl2CppBackend, BindingMechanism mechanism)
        => Classify(kind, isIl2CppBackend, (BindingMechanism?)mechanism);

    /// <summary>
    /// Snapshots the wiring mechanism once at registration. Never re-query: the engine flaps with
    /// site membership, so the registration-time value is stored on the binding.
    /// Reflection-only seam (same style as the Chainloader Wave teardown): Nami.Core must not
    /// reference Nami.Wave, where Wave is optional, so this calls <c>Wave.GetPatchEngine</c> through
    /// <c>Type.GetType("Nami.Wave.Wave, Nami.Wave")</c> and maps the result name: Fast to
    /// <see cref="BindingMechanism.TrampolineDispatch"/>, ILCopy to
    /// <see cref="BindingMechanism.BakedIntoGeneratedBody"/>. IL2CPP registrations bypass the query
    /// (no MethodBase path parity) and return <see cref="BindingMechanism.NativeEntry"/> by construction.
    /// A missing Wave assembly, a missing method, a failed call, None (no site yet), or an unknown
    /// engine returns null, which classifies <see cref="ReloadCapability.RestartRequired"/>.
    /// </summary>
    /// <param name="target">Wave patch target being registered.</param>
    /// <param name="isIl2Cpp">True for IL2CPP slots (address-based, not MethodBase-based).</param>
    public static BindingMechanism? MechanismFor(MethodBase target, bool isIl2Cpp)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (isIl2Cpp)
        {
            return BindingMechanism.NativeEntry;
        }

        Type? wave;
        try
        {
            wave = Type.GetType("Nami.Wave.Wave, Nami.Wave", throwOnError: false);
        }
        catch
        {
            return null;
        }

        if (wave is null)
        {
            return null;
        }

        var getEngine = wave.GetMethod("GetPatchEngine", BindingFlags.Public | BindingFlags.Static);
        if (getEngine is null)
        {
            return null;
        }

        object? result;
        try
        {
            result = getEngine.Invoke(null, new object?[] { target });
        }
        catch
        {
            return null;
        }

        return result?.ToString() switch
        {
            nameof(FastEngineHint.Fast) => BindingMechanism.TrampolineDispatch,
            nameof(FastEngineHint.ILCopy) => BindingMechanism.BakedIntoGeneratedBody,
            _ => null,
        };
    }

    /// <summary>
    /// Names of the <c>WavePatchEngine</c> values, duplicated as strings so Nami.Core can map the
    /// reflection result without referencing Nami.Wave. Must match the Wave enum member names exactly.
    /// </summary>
    private enum FastEngineHint
    {
        /// <summary>Matches <c>WavePatchEngine.None</c> (no site yet); maps to null, never to a mechanism.</summary>
        None,
        /// <summary>Matches <c>WavePatchEngine.Fast</c>.</summary>
        Fast,
        /// <summary>Matches <c>WavePatchEngine.ILCopy</c>.</summary>
        ILCopy,
    }
}

/// <summary>
/// Resolves dispatch slots against a caller-provided snapshot. Holds no mutable slot table
/// of its own: slots live in the immutable <c>RuntimeSnapshot</c>, and every dispatch reads
/// the current snapshot once, then the integrator-owned lease gate acquires execution on the
/// resolved generation (lookup-then-lease in two steps is forbidden: a retire between them
/// could reclaim before invoke). Lease acquisition wraps <see cref="ResolveCurrent"/>: the
/// callback and generation return together through one fused resolution.
/// Retire order per slot: publish the new snapshot first, then remove old bindings from
/// eligibility, then drop old callback references only after quiescence.
/// </summary>
public static class GenerationDispatcher
{
    /// <summary>
    /// Host Wave owner id for a slot. Each slot registers with Wave exactly once under this
    /// owner a stable trampoline (snapshot read, lease, invoke current callback); a generation
    /// swap publishes a new snapshot with zero Wave rebuild and zero native rewrite for
    /// dispatchable slots.
    /// </summary>
    /// <param name="modId">Logical mod id.</param>
    /// <param name="slotKey">Mod-scoped slot name.</param>
    public static string SlotOwnerFor(string modId, string slotKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotKey);
        return $"nami:gen:{modId}:{slotKey}";
    }

    /// <summary>
    /// Pure re-resolution of a previously observed slot against a snapshot: returns the callback
    /// and generation the snapshot currently holds for the slot, together, so lease acquisition
    /// can wrap a single resolution. A missing slot means its generation is retired (retired gets
    /// no new execution), so this throws <c>GenerationRetiredException</c>. Never invoke the
    /// returned callback without the execution lease; the lease gate (integrator-owned
    /// <c>AcquireCurrentGeneration</c>) is what keeps entry ordered against retirement.
    /// </summary>
    /// <param name="slot">Previously observed slot (provides mod, key, and the generation to blame).</param>
    /// <param name="snapshot">Snapshot to resolve against (normally the current one, read once).</param>
    internal static (Delegate Callback, int Generation) ResolveCurrent(DispatchSlot slot, RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.DispatchSlots.TryGetValue(SlotOwnerFor(slot.ModId, slot.SlotKey), out var current))
        {
            return (current.Current, current.CurrentGeneration);
        }

        throw new GenerationRetiredException(slot.ModId, slot.CurrentGeneration);
    }
}
