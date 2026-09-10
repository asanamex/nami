using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Nami.Core.Configuration;
using Nami.Core.Generations;
using Nami.Core.Logging;
using Nami.Core.Plugins;
using Nami.Core.Profiling;
using Nami.Sdk;

namespace Nami.Core;

/// <summary>Outcome of a hot-reload operation.</summary>
/// <param name="Reloaded">Plugin ids that were reloaded into a new generation, in load order.</param>
/// <param name="Unloaded">Plugin ids that were unloaded (file removed / disabled).</param>
/// <param name="Failed">Plugin ids whose new generation failed to load; failed ids keep their previous generation running.</param>
public sealed record ReloadResult(
    IReadOnlyList<string> Reloaded,
    IReadOnlyList<string> Unloaded,
    IReadOnlyList<string> Failed)
{
    public bool AnyChanges => Reloaded.Count > 0 || Unloaded.Count > 0 || Failed.Count > 0;
}

/// <summary>Event payload for plugin reloads.</summary>
public sealed class HotReloadEventArgs(string pluginId, int oldGeneration, int newGeneration, bool success, string? error)
{
    public string PluginId { get; } = pluginId;
    public int OldGeneration { get; } = oldGeneration;
    public int NewGeneration { get; } = newGeneration;
    public bool Success { get; } = success;
    public string? Error { get; } = error;
    /// <summary>Current generation record for this event, when known.</summary>
    public ModGeneration? Generation { get; init; }
}

/// <summary>Runtime context handed to a plugin (implements the SDK's <see cref="IPluginContext"/>).</summary>
public sealed class PluginContext(PluginManifest manifest, LogHub hub, Chainloader chainloader,
    ModProfiler profiler, IPluginConfig config, IGenerationHooks hooks, IModState state)
    : IPluginContext
{
    public PluginInfo Info { get; } = new(manifest.Id, manifest.Name, manifest.Version)
    {
        Authors = manifest.Authors,
        Description = manifest.Description
    };

    public ILog Log { get; } = new PluginLog(manifest.Id, hub);
    public IModMetrics Profiler { get; } = profiler;
    public IPluginConfig Config { get; } = config;
    /// <summary>Generation-scoped Wave hook surface, supplied per generation by the reload coordinator.</summary>
    public IGenerationHooks Hooks { get; } = hooks;
    /// <summary>Host-owned persisted JSON state for this mod.</summary>
    public IModState State { get; } = state;
    /// <summary>Owning generation, bound by the coordinator after the generation record exists.</summary>
    internal ModGeneration? Generation { get; set; }

    /// <summary>
    /// The calling generation's resources. Scheduling from a retiring instance would outlive the
    /// generation, so every resource member rejects with <see cref="InvalidOperationException"/>
    /// naming teardown once retirement has started (same predicate as
    /// <see cref="RequestReload"/>).
    /// </summary>
    private GenerationResources ActiveResources(string operation)
    {
        var generation = Generation
            ?? throw new InvalidOperationException(
                $"Cannot {operation} before '{manifest.Id}' is attached to a generation.");
        if (generation.IsRetired || generation.Lifetime is
            LifetimeState.Retiring or LifetimeState.Quiescing or LifetimeState.Reclaiming or
            LifetimeState.UnloadRequested or LifetimeState.Collected or LifetimeState.RetirementBlocked)
        {
            throw new InvalidOperationException(
                $"Cannot {operation} on '{manifest.Id}' generation {generation.Generation}: " +
                "the generation is in teardown; scheduling work that would outlive it is forbidden.");
        }

        return generation.Resources;
    }

    /// <inheritdoc />
    public CancellationToken Lifetime => ActiveResources(nameof(Lifetime)).Lifetime;

    /// <inheritdoc />
    public void Track(Task task, string? name = null) =>
        ActiveResources(nameof(Track)).Track(task, name);

    /// <inheritdoc />
    public void OnCleanup(Func<ValueTask> cleanup, string? name = null) =>
        ActiveResources(nameof(OnCleanup)).OnCleanup(cleanup, name);

    /// <inheritdoc />
    public void Own(IDisposable disposable) => ActiveResources(nameof(Own)).Own(disposable);

    /// <inheritdoc />
    public void Own(IAsyncDisposable disposable) => ActiveResources(nameof(Own)).Own(disposable);
    internal Chainloader Chainloader { get; } = chainloader;

    public bool RequestReload()
    {
        // Scheduling from a retiring instance would outlive the generation: reject, naming teardown.
        if (Generation is not null && (Generation.IsRetired || Generation.Lifetime is
            LifetimeState.Retiring or LifetimeState.Quiescing or LifetimeState.Reclaiming or
            LifetimeState.UnloadRequested or LifetimeState.Collected or LifetimeState.RetirementBlocked))
        {
            throw new InvalidOperationException(
                $"RequestReload from '{manifest.Id}' generation {Generation.Generation} rejected: " +
                "the generation is in teardown; scheduling work that would outlive it is forbidden.");
        }

        return Chainloader.RequestReload(manifest.Id);
    }
}

/// <summary>
/// Loads plugins discovered in the mods directory into isolated, unloadable load contexts,
/// calls lifecycle methods, applies crash quarantine, and - when hot reload is enabled -
/// watches the mods directory so edited/rebuild plugins reload live into a new generation.
///
/// Live model: candidate generations prepare privately, validate, and publish as one immutable
/// host-owned <see cref="RuntimeSnapshot"/> in a single <see cref="Interlocked.Exchange{T}(ref T, T)"/>
/// root replacement. The old snapshot then retires (reverse dependency order), drains ordinary
/// execution (quiesce), runs teardown exactly once, and releases its ALC for best-effort
/// reclamation. ALC unload is deferred reclamation, never the hot-swap mechanism: the swap is a
/// pointer publication; collection is observed afterward. Three invariants govern everything: a
/// retired generation receives no new execution; a generation is never reclaimed while tracked
/// execution can enter it; a failed candidate cannot destroy the last known-good generation.
///
/// All structural changes flow through a command queue drained between update ticks, so
/// reloads never race a running <see cref="OnUpdate"/>. Preparation may run on ThreadPool
/// threads (watcher path); commits always run on the drain (the CoreCLR safe point).
/// </summary>
public sealed partial class Chainloader : IDisposable, Nami.Sdk.ITideOpSink
{
    private readonly string _rootDirectory;
    private readonly string _modsDirectory;
    private readonly LogHub _hub;
    private readonly Dictionary<string, ModRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<Action> _commands = new();
    private readonly Lock _gate = new();
    private readonly ModStateStore _stateStore = new();
    private readonly object _commitGate = new();

    // The only visible active set. Published once per commit with Interlocked.Exchange — never
    // per-node sequential publishes. Dispatch and queries read the reference once per operation,
    // so readers always observe exactly one generation graph (never a mixed A=v2/B=v1/C=v2).
    private RuntimeSnapshot _currentSnapshot;
    private long _snapshotVersion;
    private long _sequenceSource;

    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _requestsWatcher;
    private Timer? _debounceTimer;
    private readonly Dictionary<string, PendingFile> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _watcherDirty;
    private bool _requestsDirty;
    private long _tickCounter;
    private int _nextGeneration;
    private ModProfiler? _currentProfiler;
    private bool _disposed;
    // Pre-unload retiring generations (strong refs; diagnostics + teardown driving). Dropped at
    // UnloadRequested: post-unload tracking is weak-only (see _reclaiming / _retirementRecords),
    // so the host never roots a reclaiming ALC.
    private readonly List<RetireState> _retiring = new();
    private readonly List<ReclaimInfo> _reclaiming = new();
    private readonly List<RetirementRecord> _retirementRecords = new();

    private sealed class RetireState
    {
        public required ModGeneration Generation;
        public bool ReleaseSlots;
        public bool IsQuarantine;
        public int Drives;
    }

    private sealed class PendingFile
    {
        public long Size;
        public DateTimeOffset MtimeUtc;
        public int StableWindows;
        public int Retries;
        public bool Missing;
    }

    private sealed class ReclaimInfo
    {
        public required string ModId;
        public required int GenerationId;
        public required RetirementReport? Outcome;
        public required WeakReference AlcTracker;
        public required string[] Blockers;
        public bool ObservedCollected;
        // Actual lifetime string at unload time (usually UnloadRequested; Quarantined for the
        // quarantine path, which never walks the Reclaiming chain). Status reports this verbatim
        // instead of hardcoding UnloadRequested.
        public required string Lifetime;
    }

    public NamiConfig Config { get; }
    public LogHub LogHub => _hub;
    /// <summary>Host-owned per-mod JSON state; the coordinator commits migrated snapshots through it.</summary>
    internal ModStateStore StateStore => _stateStore;

    /// <summary>Current generations only; retiring records never execute and are never listed here.</summary>
    public IReadOnlyList<ModGeneration> Plugins
    {
        get
        {
            var snapshot = Volatile.Read(ref _currentSnapshot);
            // The snapshot maps are built fresh per publish and never mutated after, so lock-free
            // enumeration is safe; ToArray freezes the moment for the caller.
            return snapshot.Generations.Values.ToArray();
        }
    }

    /// <summary>Current plus retiring generations (diagnostics only; retiring records never execute).</summary>
    internal IReadOnlyList<ModGeneration> AllGenerations
    {
        get
        {
            var snapshot = Volatile.Read(ref _currentSnapshot);
            lock (_gate)
            {
                return snapshot.Generations.Values.Concat(_retiring.Select(r => r.Generation)).ToArray();
            }
        }
    }

    internal IReadOnlyList<RetirementRecord> RetirementRecords
    {
        get
        {
            lock (_gate)
            {
                return _retirementRecords.ToArray();
            }
        }
    }

    public string ModsDirectory => _modsDirectory;
    public event Action<ModGeneration>? PluginQuarantined;
    public event Action<HotReloadEventArgs>? PluginReloaded;

    public Chainloader(string rootDirectory, NamiConfig? config = null, LogHub? hub = null)
    {
        _rootDirectory = rootDirectory;
        Config = config ?? new NamiConfig { RootPath = rootDirectory };
        _hub = hub ?? new LogHub();
        _modsDirectory = Path.Combine(rootDirectory, "mods");
        _currentSnapshot = new RuntimeSnapshot(0,
            new Dictionary<string, ModGeneration>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DispatchSlot>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        Nami.Sdk.TideMetrics.Register(this);
    }

    /// <summary>
    /// Receives Tide op latencies from the bridge (via <see cref="Nami.Sdk.TideMetrics"/>)
    /// and attributes them to the plugin whose <c>OnUpdate</c> is currently running. Ops fired
    /// outside a plugin's update tick (e.g. the boot bridge self-test) are unattributed.
    /// </summary>
    void Nami.Sdk.ITideOpSink.RecordTideOp(double milliseconds) => _currentProfiler?.RecordTideOp(milliseconds);

    public void Initialize()
    {
        Directory.CreateDirectory(_modsDirectory);
    }

    /// <summary>Discovers plugin manifests in the mods directory without loading them.</summary>
    public IReadOnlyList<PluginManifest> DiscoverPlugins() => PluginDiscoverer.Discover(_modsDirectory, _hub);

    /// <summary>
    /// Discovers, resolves, loads and activates plugins in dependency order.
    /// Returns the set of plugins that failed to load.
    /// </summary>
    public IReadOnlyList<PluginManifest> LoadAll()
    {
        Initialize();
        var manifests = DiscoverPlugins();
        var resolution = DependencyResolver.Resolve(manifests);

        foreach (var (id, reason) in resolution.Skipped)
        {
            _hub.Log("chainloader", LogLevel.Warn, $"Skipping plugin '{id}': {reason}");
        }

        foreach (var manifest in resolution.LoadOrder)
        {
            LoadOne(manifest);
        }

        WriteStatus();
        return Plugins.Select(p => p.Manifest).ToList();
    }

    /// <summary>
    /// Glob match for <c>enabledPlugins</c> entries: <c>*</c> spans any run (including dots),
    /// <c>?</c> spans one char, case-insensitive; anything else is literal.
    /// </summary>
    internal static bool EnabledMatch(string pattern, string id)
    {
        var regex = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*", ".*", StringComparison.Ordinal)
                .Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return regex.IsMatch(id);
    }

    private ModRecord GetOrCreateRecord(string id)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
            {
                record = new ModRecord(id) { State = _stateStore };
                _records[id] = record;
            }
            else if (record.State is null)
            {
                record.State = _stateStore;
            }

            return record;
        }
    }

    private static void SetLifetime(ModGeneration generation, LifetimeState to) =>
        generation.Lifetime = generation.Lifetime.TransitionTo(to);

    // ------------------------------------------------------------------
    // Generation hooks: per-generation IGenerationHooks implementation
    // ------------------------------------------------------------------

    /// <summary>Resolves the generation-scoped hooks for one load. One instance per generation.</summary>
    private GenerationHooksImpl GenerationHooksFor(string modId, int generation) =>
        new(this, modId, generation);

    /// <summary>
    /// Raw hook registration collected during a candidate's <c>OnLoad</c>. Wave is never touched
    /// here: registration records the binding (mechanism snapshotted once, never re-queried) and
    /// builds the host trampoline; the commit applies Wave work at the safe point.
    /// </summary>
    private sealed class RawRegistration
    {
        public required PatchKind Kind;
        public required string SlotKey = "";
        public MethodBase? Target;
        public Delegate? Trampoline;
        /// <summary>Generation callback for CoreCLR slots (snapshot value dispatched by the trampoline). Prep-lifetime only.</summary>
        public Delegate? SourceCallback;
        public Delegate? DirectCallback;
        public Delegate? DirectPostfix;
        public BindingMechanism? Mechanism;
        public ReloadCapability Capability;
        public bool Stable;
        public string? Il2CppAssembly;
        public string? Il2CppNs;
        public string? Il2CppKlass;
        public string? Il2CppMethod;
        public int Il2CppArgCount;
        public int Il2CppReturnKind;
        public IReadOnlyList<int>? Il2CppParameterTypes;
        public int? Il2CppReturnType;
    }

    private sealed class GenerationHooksImpl(Chainloader chainloader, string modId, int generation) : IGenerationHooks
    {
        private readonly HashSet<string> _slotKeys = new(StringComparer.Ordinal);
        public readonly List<RawRegistration> Registrations = new();

        private void ClaimSlot(string slotKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(slotKey);
            if (!_slotKeys.Add(slotKey))
            {
                // Same rule as Wave's duplicate owner registration.
                throw new InvalidOperationException(
                    $"Mod '{modId}' already registered hook slot '{slotKey}' in generation {generation} (duplicate slotKey).");
            }
        }

        // Instance-level (per-generation) cache, never static: a process-wide static leaks one
        // test's Tide backend answer into every later Chainloader in the process (cross-test
        // pollution). The engine still flaps with site membership, so the value is snapshotted
        // once per generation and never re-queried within it.
        private bool? _il2CppBackend;

        private bool IsIl2CppBackend()
        {
            // Cached once: the engine flaps with site membership, so the registration-time value
            // is stored on the binding and never re-queried. Reflection-only; Tide is optional here.
            if (_il2CppBackend.HasValue)
            {
                return _il2CppBackend.Value;
            }

            try
            {
                var tide = Type.GetType("Nami.Tide.Tide, Nami.Tide", throwOnError: false);
                var backend = tide?.GetProperty("ActiveBackend", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                _il2CppBackend = string.Equals(backend?.ToString(), "Il2Cpp", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                _il2CppBackend = false;
            }

            return _il2CppBackend.Value;
        }

        private RawRegistration StageCoreClr(PatchKind kind, MethodBase target, string slotKey, Delegate callback)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(callback);
            ClaimSlot(slotKey);
            // Snapshot the wiring once at registration; never re-query (the engine flaps with site membership).
            var mechanism = GenerationPatchClassifier.MechanismFor(target, isIl2Cpp: false);
            var capability = GenerationPatchClassifier.Classify(kind, IsIl2CppBackend(), mechanism);
            var stable = WaveBridge.IsStableCallback(callback);
            if (!stable)
            {
                // Proven-separability failure, honestly reported: a generation-local delegate type
                // would root its ALC through a register-once trampoline, so this slot rebuilds at
                // commit (Unpatch old plus Patch new) instead of swapping live.
                capability = ReloadCapability.RequiresPatchRebuild;
                chainloader._hub.Log("chainloader", LogLevel.Info,
                    $"Slot '{modId}:{slotKey}' uses a generation-local callback type; RequiresPatchRebuild (reported, never forced).");
            }

            // Eager trampoline build: exotic shapes fail preparation (candidate rejected, old kept),
            // never mid-commit. Captures host state only — never the generation callback or its Type.
            var acquire = chainloader.TrampolineAcquire(modId, slotKey);
            var trampoline = WaveBridge.CreateTrampoline(callback.GetType(), acquire);
            var registration = new RawRegistration
            {
                Kind = kind,
                SlotKey = slotKey,
                Target = target,
                Trampoline = trampoline,
                SourceCallback = callback,
                Mechanism = mechanism,
                Capability = capability,
                Stable = stable,
            };
            Registrations.Add(registration);
            return registration;
        }

        public void HookGate(MethodBase target, string slotKey, Func<bool> gate)
        {
            ArgumentNullException.ThrowIfNull(gate);
            StageCoreClr(PatchKind.CallbackGate, target, slotKey, gate);
        }

        public void HookObserver(MethodBase target, string slotKey, Action observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            StageCoreClr(PatchKind.CallbackObserver, target, slotKey, observer);
        }

        public void PatchPrefix(MethodBase target, string slotKey, Delegate prefix)
        {
            ArgumentNullException.ThrowIfNull(prefix);
            StageCoreClr(PatchKind.CallbackPrefix, target, slotKey, prefix);
        }

        public void PatchPostfix(MethodBase target, string slotKey, Delegate postfix)
        {
            ArgumentNullException.ThrowIfNull(postfix);
            StageCoreClr(PatchKind.CallbackPostfix, target, slotKey, postfix);
        }

        public void PatchTranspile(MethodBase target, string slotKey, Delegate transpiler)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(transpiler);
            ClaimSlot(slotKey);
            // Transpiler-bearing slots never participate in callback swaps: the generated body is the
            // artifact. Capability is always RequiresPatchRebuild; the mechanism (when Wave answers)
            // is informational. A non-Wave delegate type fails here with a clear error.
            var mechanism = GenerationPatchClassifier.MechanismFor(target, isIl2Cpp: false);
            Registrations.Add(new RawRegistration
            {
                Kind = PatchKind.Transpiler,
                SlotKey = slotKey,
                Target = target,
                DirectCallback = transpiler,
                Mechanism = mechanism,
                Capability = ReloadCapability.RequiresPatchRebuild,
                Stable = false,
            });
        }

        private void StageIl2Cpp(PatchKind kind, string slotKey,
            string assembly, string ns, string klass, string method, int argCount,
            int returnKind, IReadOnlyList<int>? parameterTypes, int? returnType,
            Delegate? callback, Delegate? postfix)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assembly);
            ArgumentException.ThrowIfNullOrWhiteSpace(ns);
            ArgumentException.ThrowIfNullOrWhiteSpace(klass);
            ArgumentException.ThrowIfNullOrWhiteSpace(method);
            ClaimSlot(slotKey);
            // IL2CPP separability is mechanically present (stable native entry) but operationally
            // unproven, so the host reports RestartRequired until a proven-separable entry exists
            // (reported, never forced). At minimum SafePointRequired is honored: commit-time
            // re-registration runs on the tick drain. Callbacks register directly; native dispatch
            // is untouched wherever the Wave implementation keeps it stable across re-hooks.
            Registrations.Add(new RawRegistration
            {
                Kind = kind,
                SlotKey = slotKey,
                DirectCallback = callback,
                DirectPostfix = postfix,
                Mechanism = BindingMechanism.NativeEntry,
                Capability = ReloadCapability.RestartRequired,
                Stable = false,
                Il2CppAssembly = assembly,
                Il2CppNs = ns,
                Il2CppKlass = klass,
                Il2CppMethod = method,
                Il2CppArgCount = argCount,
                Il2CppReturnKind = returnKind,
                Il2CppParameterTypes = parameterTypes,
                Il2CppReturnType = returnType,
            });
        }

        public void HookIl2Cpp(string assembly, string ns, string klass, string method, int argCount, string slotKey, Delegate callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            ArgumentOutOfRangeException.ThrowIfNegative(argCount);
            if (argCount > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(argCount), "fast-path IL2CPP hooks expose up to 4 register arguments; use HookIl2CppFull.");
            }

            StageIl2Cpp(PatchKind.Il2CppHook, slotKey, assembly, ns, klass, method, argCount,
                0, null, null, callback, null);
        }

        public void HookIl2CppFull(string assembly, string ns, string klass, string method, int argCount, int returnKind, string slotKey, Delegate? prefix, Delegate? postfix)
        {
            if (prefix is null && postfix is null)
            {
                throw new ArgumentException("at least one of prefix/postfix must be supplied", nameof(prefix));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(argCount);
            if (argCount > 12)
            {
                throw new ArgumentOutOfRangeException(nameof(argCount), "HookIl2CppFull exposes up to 12 arguments.");
            }

            if (returnKind is < 0 or > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(returnKind), "returnKind is an Il2CppReturnKind tag (Void 0 .. F64 4).");
            }

            StageIl2Cpp(PatchKind.Il2CppFull, slotKey, assembly, ns, klass, method, argCount,
                returnKind, null, null, prefix, postfix);
        }

        public void HookIl2CppTyped(string assembly, string ns, string klass, string method, IReadOnlyList<int> parameterTypes, int? returnType, string slotKey, Delegate? prefix, Delegate? postfix)
        {
            ArgumentNullException.ThrowIfNull(parameterTypes);
            if (prefix is null && postfix is null)
            {
                throw new ArgumentException("at least one of prefix/postfix must be supplied", nameof(prefix));
            }

            StageIl2Cpp(PatchKind.Il2CppTyped, slotKey, assembly, ns, klass, method, parameterTypes.Count,
                0, parameterTypes, returnType, prefix, postfix);
        }
    }
    private static bool IsSuperseded(ModRecord record, ReloadTransaction transaction) =>
        IsSupersededFor(record, transaction, transaction.ModId);

    /// <summary>
    /// Per-id supersede check: graph transactions carry one sequence per id (Sequences); single-id
    /// transactions fall back to the scalar. A mismatch means a newer request arrived after this
    /// candidate started preparing.
    /// </summary>
    private static bool IsSupersededFor(ModRecord record, ReloadTransaction transaction, string id) =>
        transaction.Sequences.TryGetValue(id, out var expected)
            ? record.PendingSequence != expected
            : record.PendingSequence != transaction.Sequence;

    /// <summary>
    /// Builds the per-slot acquire closure for trampolines. Captures only host state (this plus
    /// strings): the trampoline it feeds never references a generation callback, Type, or ALC.
    /// </summary>
    private Func<(ExecutionLease Lease, Delegate Callback)> TrampolineAcquire(string modId, string slotKey) =>
        () => AcquireCurrentGeneration(modId, slotKey);

    /// <summary>
    /// Fused acquire-or-reject for dispatch: reads the current snapshot ONCE, resolves the slot's
    /// generation, and acquires the ordinary-execution lease through the same atomic gate retirement
    /// uses — then returns the callback and lease together. Lookup-then-lease in two steps is
    /// forbidden (a retire between them could reclaim before invoke); this single method is the
    /// only path from slot to invocation. Throws <see cref="GenerationRetiredException"/> when the
    /// slot is gone or its generation retired.
    /// </summary>
    internal (ExecutionLease Lease, Delegate Callback) AcquireCurrentGeneration(string modId, string slotKey)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (!snapshot.DispatchSlots.TryGetValue(GenerationDispatcher.SlotOwnerFor(modId, slotKey), out var slot))
        {
            throw new GenerationRetiredException(modId, -1);
        }

        if (!snapshot.Generations.TryGetValue(modId, out var generation) ||
            generation.Generation != slot.CurrentGeneration)
        {
            throw new GenerationRetiredException(modId, slot.CurrentGeneration);
        }

        var lease = generation.EnterExecution();
        return (lease, slot.Current);
    }

    // ------------------------------------------------------------------
    // Preparation pipeline (private prepare; commit under _commitGate)
    // ------------------------------------------------------------------

    /// <summary>
    /// Candidate staging state: the candidate migrates against a read-only snapshot of current
    /// state and stages into this object; the coordinator commits it together with the generation
    /// publication. Failed migration never touches the live store. After commit,
    /// <see cref="Promote"/> flips this view to forward to the live store, so the running
    /// generation's context never goes stale (no re-attach needed).
    /// </summary>
    private sealed class StagingModState : IModState
    {
        private readonly ModStateStore _store;
        private readonly string _modId;
        private string? _json;
        private int _version;
        private bool _promoted;

        public StagingModState(ModStateStore store, string modId, ModStateSnapshot snapshot)
        {
            _store = store;
            _modId = modId;
            _json = snapshot.Json;
            _version = snapshot.SchemaVersion;
        }

        public int SchemaVersion => _promoted ? _store.GetSchemaVersion(_modId) : _version;
        public string? GetState() => _promoted ? _store.Get(_modId) : _json;

        public void SetState(string json, int schemaVersion)
        {
            ArgumentNullException.ThrowIfNull(json);
            ArgumentOutOfRangeException.ThrowIfNegative(schemaVersion);
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(json);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ArgumentException($"State for '{_modId}' must be valid JSON: {ex.Message}", nameof(json));
            }

            if (_promoted)
            {
                _store.Set(_modId, json, schemaVersion);
                return;
            }

            _json = json;
            _version = schemaVersion;
        }

        public void ClearState()
        {
            if (_promoted)
            {
                _store.Clear(_modId);
                return;
            }

            _json = null;
            _version = 0;
        }

        public void Seed(string json, int version)
        {
            _json = json;
            _version = version;
        }

        public ModStateSnapshot Capture() => new(_json, _version);
        public void Promote() => _promoted = true;
    }


    private void DisposeCandidate(PreparedCandidate candidate)
    {
        try
        {
            candidate.LoadContext.Unload();
        }
        catch
        {
            // Best-effort: an unpublished candidate must never fail its transaction.
        }
    }
    /// <summary>Clears this transaction off every record it touched (only when still current).</summary>
    private void ClearTransaction(ReloadTransaction transaction)
    {
        lock (_gate)
        {
            foreach (var id in transaction.CommitOrder.Concat(transaction.Removed).Concat(transaction.Sequences.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_records.TryGetValue(id, out var entry) && ReferenceEquals(entry.Active, transaction))
                {
                    entry.Active = null;
                }
            }
        }
    }

    /// <summary>
    /// Prepares one candidate generation following the exact pipeline: discover source (caller
    /// supplies the manifest) → create generation → create ALC → resolve deps → load assemblies →
    /// instantiate → create context → attach → restore/migrate state (never partially mutating
    /// current state) → initialize (OnLoad under lease) → prepare resources → prepare hook
    /// bindings → validate (OnValidate false rejects) → consistency check → ready. Returns null
    /// when the candidate is rejected or superseded (reason recorded on the transaction); the
    /// previous generation keeps running in both cases.
    /// </summary>
    private PreparedCandidate? PrepareCandidate(
        ReloadTransaction transaction, PluginManifest manifest, ModRecord record, bool isReload)
    {
        record.Active = transaction;
        var generationNumber = NextGeneration();
        PluginLoadContext? loadContext = null;
        ModGeneration? generation = null;
        StagingModState? staging = null;
        GenerationHooksImpl? hooks = null;

        void Fail(string reason, bool unload = true)
        {
            transaction.Rejected[manifest.Id] = reason;
            _hub.Log("chainloader", LogLevel.Error, $"Candidate for '{manifest.Id}' rejected: {reason}");
            if (unload && loadContext is not null)
            {
                try
                {
                    loadContext.Unload();
                }
                catch
                {
                    // Best-effort.
                }
            }
        }

        try
        {
            // Create ALC. The host subscribes Unloading once per generation for transition logging.
            // Reachability proof: the ALC (publisher) holds a delegate targeting the chainloader
            // (host subscriber); no edge runs host→ALC through this subscription, so it cannot root
            // the collectible context. The subscription is never removed: removal would need a
            // strong ALC reference at exactly the moment we are dropping all of them.
            loadContext = new PluginLoadContext(manifest.AssemblyPath);
            loadContext.Unloading += OnAlcUnloading;

            // Resolve deps: the graph shape was validated by DependencyResolver over fresh discovery
            // (commit re-checks per-id resolvability); the ALC itself resolves lazily through its
            // AssemblyDependencyResolver (kept as-is; it is never reinvented).
            var assembly = loadContext.LoadPluginAssembly();
            var type = PluginDiscoverer.FindPluginType(assembly)
                ?? throw new InvalidOperationException("no [NamiPlugin] type found");

            // Supersede checkpoint 1 (after load): a newer sequence disposes the candidate, logs
            // superseded, and restarts preparation upstream.
            if (IsSupersededFor(record, transaction, manifest.Id))
            {
                transaction.Superseded = true;
                _hub.Log("chainloader", LogLevel.Info,
                    $"Candidate for '{manifest.Id}' superseded after load (sequence {transaction.Sequence}); restarting prepare.");
                loadContext.Unload();
                return null;
            }

            if (Activator.CreateInstance(type) is not NamiPlugin instance)
            {
                throw new InvalidOperationException($"plugin type {type.FullName} is not a NamiPlugin");
            }

            var profiler = new ModProfiler();
            var pluginConfig = Config.PluginConfig is { } sections &&
                               sections.TryGetValue(manifest.Id, out var section)
                ? new JsonPluginConfig(section)
                : JsonPluginConfig.Empty;
            hooks = GenerationHooksFor(manifest.Id, generationNumber);
            var snapshot = _stateStore.Snapshot(manifest.Id);
            staging = new StagingModState(_stateStore, manifest.Id, snapshot);
            var context = new PluginContext(manifest, _hub, this, profiler, pluginConfig, hooks, staging);
            instance.Attach(context);

            generation = new ModGeneration(manifest, instance, context, loadContext, generationNumber, profiler)
            {
                Lifetime = LifetimeState.Discovered,
            };
            generation.Resources = new GenerationResources(new PluginLog(manifest.Id, _hub));
            context.Generation = generation;
            SetLifetime(generation, LifetimeState.Preparing);
            SetLifetime(generation, LifetimeState.Loaded);

            // Restore/migrate state against the read-only snapshot; never partially mutate current state.
            if (ModStateStore.TryApplyMigratedState(snapshot, instance, snapshot.SchemaVersion,
                out var migrated, out var migrateError))
            {
                if (migrated is not null && migrated.Json != snapshot.Json)
                {
                    staging.Seed(migrated.Json!, migrated.SchemaVersion);
                }
            }
            else if (isReload)
            {
                Fail($"state migration failed ({migrateError}); current state kept");
                return null;
            }
            else
            {
                _hub.Log("chainloader", LogLevel.Warn,
                    $"State migration for '{manifest.Id}' failed ({migrateError}); keeping stored state.");
            }

            // Initialize: OnLoad runs under the ordinary-execution lease, like every host→mod entry.
            SetLifetime(generation, LifetimeState.Starting);
            try
            {
                using (generation.EnterExecution())
                {
                    instance.OnLoad();
                }
            }
            catch (Exception ex)
            {
                if (isReload)
                {
                    Fail($"OnLoad threw ({ex.GetBaseException().Message}); previous generation kept");
                    return null;
                }

                SetLifetime(generation, LifetimeState.Failed);
                generation.LastError = ex.GetBaseException().Message;
                _hub.Log("chainloader", LogLevel.Error, $"Plugin '{manifest.Id}' failed during OnLoad: {ex}");
            }

            // Prepare hook bindings from what OnLoad registered (Wave untouched until commit).
            var stages = new List<SlotStage>();
            if (generation.Lifetime != LifetimeState.Failed)
            {
                foreach (var raw in hooks.Registrations)
                {
                    var owner = GenerationDispatcher.SlotOwnerFor(manifest.Id, raw.SlotKey);
                    Delegate snapshotCallback;
                    if (raw.Kind is PatchKind.Transpiler or PatchKind.Il2CppHook or PatchKind.Il2CppFull or PatchKind.Il2CppTyped)
                    {
                        snapshotCallback = raw.DirectCallback
                            ?? throw new InvalidOperationException($"Slot '{manifest.Id}:{raw.SlotKey}' has no callback.");
                    }
                    else
                    {
                        // Snapshot holds the generation callback; the registered trampoline resolves it per call.
                        snapshotCallback = raw.SourceCallback
                            ?? throw new InvalidOperationException($"Slot '{manifest.Id}:{raw.SlotKey}' has no callback.");
                    }

                    stages.Add(new SlotStage
                    {
                        ModId = manifest.Id,
                        SlotKey = raw.SlotKey,
                        Kind = raw.Kind,
                        Slot = new DispatchSlot(manifest.Id, raw.SlotKey, raw.Kind, snapshotCallback, generationNumber),
                        Binding = new WaveBinding(manifest.Id, generationNumber, raw.SlotKey, raw.Kind,
                            raw.Mechanism ?? BindingMechanism.TrampolineDispatch, raw.Capability,
                            BindingState.Active, owner),
                        Target = raw.Target,
                        Trampoline = raw.Trampoline,
                        Stable = raw.Stable,
                        DirectCallback = raw.DirectCallback,
                        DirectPostfix = raw.DirectPostfix,
                        Il2CppAssembly = raw.Il2CppAssembly,
                        Il2CppNs = raw.Il2CppNs,
                        Il2CppKlass = raw.Il2CppKlass,
                        Il2CppMethod = raw.Il2CppMethod,
                        Il2CppArgCount = raw.Il2CppArgCount,
                        Il2CppReturnKind = raw.Il2CppReturnKind,
                        Il2CppParameterTypes = raw.Il2CppParameterTypes,
                        Il2CppReturnType = raw.Il2CppReturnType,
                    });
                }

                foreach (var stage in stages)
                {
                    generation.HookBindings.Add(stage.Binding);
                }
            }

            // Validate: OnValidate false rejects the candidate (current generation keeps running).
            if (generation.Lifetime != LifetimeState.Failed)
            {
                bool valid;
                try
                {
                    using (generation.EnterExecution())
                    {
                        valid = instance.OnValidate();
                    }
                }
                catch (Exception ex)
                {
                    valid = false;
                    generation.LastError = ex.GetBaseException().Message;
                    if (isReload)
                    {
                        Fail($"OnValidate threw ({ex.GetBaseException().Message}); previous generation kept");
                        return null;
                    }

                    SetLifetime(generation, LifetimeState.Failed);
                    _hub.Log("chainloader", LogLevel.Error, $"Plugin '{manifest.Id}' failed during OnValidate: {ex}");
                    valid = false;
                }

                if (!valid && generation.Lifetime != LifetimeState.Failed)
                {
                    if (isReload)
                    {
                        Fail("OnValidate returned false; previous generation kept");
                        return null;
                    }

                    SetLifetime(generation, LifetimeState.Failed);
                    _hub.Log("chainloader", LogLevel.Error, $"Plugin '{manifest.Id}' failed validation (OnValidate false).");
                }
            }

            // Supersede checkpoint 2 (after validate): same dispose/log/restart contract as checkpoint 1.
            if (IsSupersededFor(record, transaction, manifest.Id))
            {
                transaction.Superseded = true;
                _hub.Log("chainloader", LogLevel.Info,
                    $"Candidate for '{manifest.Id}' superseded after validate (sequence {transaction.Sequence}); restarting prepare.");
                loadContext.Unload();
                return null;
            }

            // Consistency check + 30s prepare cap.
            if (transaction.PrepTime.Elapsed > TimeSpan.FromSeconds(30))
            {
                Fail("preparation exceeded the 30s prepare cap; previous generation kept");
                return null;
            }

            // Ready: Starting (commit promotes to Running) or Failed (boot keeps a Failed record;
            // reload never reaches here as Failed — it returns null above).
            var prepared = new PreparedCandidate
            {
                Manifest = manifest,
                LoadContext = loadContext,
                Instance = instance,
                Context = context,
                Generation = generation,
                SlotStages = generation.Lifetime == LifetimeState.Failed ? new List<SlotStage>() : stages,
                StagedState = staging.Capture(),
                TargetStateVersion = staging.SchemaVersion,
            };
            if (generation.Lifetime == LifetimeState.Failed)
            {
                generation.HookBindings.Clear();
                // Failed boot records never enter the retirement pipeline, so without this
                // anything OnLoad tracked before failing would leak. Bounded, cooperative,
                // exceptions isolated; Shutdown repeats this idempotently for kept records.
                try
                {
                    var failedReport = generation.Resources.DisposeAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (!failedReport.Completed)
                    {
                        _hub.Log("chainloader", LogLevel.Warn,
                            $"Failed-load survivors for '{manifest.Id}': {string.Join(", ", failedReport.Survivors)}");
                    }
                }
                catch (Exception ex)
                {
                    _hub.Log("chainloader", LogLevel.Warn,
                        $"Failed-load disposal for '{manifest.Id}' reported: {ex.GetBaseException().Message}");
                }
            }

            transaction.Candidate = generation;
            return prepared;
        }
        catch (Exception ex)
        {
            var reason = $"load failed ({ex.GetBaseException().Message})";
            if (!isReload && generation is not null)
            {
                if (loadContext is null)
                {
                    // ALC construction itself failed: no load context exists to track, so no
                    // Failed record is built — a null LoadContext would NRE the status writer
                    // inside its swallowed try (losing the whole file, not just the entry).
                    Fail(reason, unload: false);
                    return null;
                }

                // Boot parity with the legacy Faulted path: the record exists but Failed.
                if (generation.Lifetime != LifetimeState.Failed)
                {
                    try
                    {
                        generation.Lifetime = generation.Lifetime.TransitionTo(LifetimeState.Failed);
                    }
                    catch
                    {
                        // Already terminal; keep the observed state.
                    }
                }

                generation.LastError = ex.GetBaseException().Message;
                _hub.Log("chainloader", LogLevel.Error, $"Failed to load plugin '{manifest.Id}': {ex}");
                // Same Failed-record rule as the in-try path: this generation never enters the
                // retirement pipeline, so dispose what it tracked before failing (bounded,
                // cooperative, exceptions isolated; Shutdown repeats idempotently).
                try
                {
                    var failedReport = generation.Resources.DisposeAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (!failedReport.Completed)
                    {
                        _hub.Log("chainloader", LogLevel.Warn,
                            $"Failed-load survivors for '{manifest.Id}': {string.Join(", ", failedReport.Survivors)}");
                    }
                }
                catch (Exception disposeEx)
                {
                    _hub.Log("chainloader", LogLevel.Warn,
                        $"Failed-load disposal for '{manifest.Id}' reported: {disposeEx.GetBaseException().Message}");
                }

                return new PreparedCandidate
                {
                    Manifest = manifest,
                    LoadContext = loadContext!,
                    Instance = generation.Instance,
                    Context = generation.Context,
                    Generation = generation,
                    SlotStages = new List<SlotStage>(),
                    StagedState = staging?.Capture(),
                    TargetStateVersion = 0,
                };
            }

            Fail(reason);
            return null;
        }
    }

    private void OnAlcUnloading(AssemblyLoadContext alc) =>
        _hub.Log("chainloader", LogLevel.Info, $"ALC unloading: {alc.Name ?? "<unnamed>"}");

    // ------------------------------------------------------------------
    // Commit (serialized; single Interlocked.Exchange per commit)
    // ------------------------------------------------------------------

    /// <summary>
    /// Best-effort Tide main-thread handshake before a commit. Tide is optional: when absent there
    /// is nothing to route and this returns true. Unity/main-thread work routes through
    /// <c>IsReady</c>/<c>EnsureReady</c>; a not-ready backend never fails the commit (the tick drain
    /// is already a CoreCLR safe point) — IL2CPP registration failures tolerate <c>-3</c>
    /// downstream instead.
    /// </summary>
    private bool EnsureTideReady()
    {
        try
        {
            var tide = Type.GetType("Nami.Tide.Tide, Nami.Tide", throwOnError: false);
            if (tide is null)
            {
                return true;
            }

            var isReady = tide.GetProperty("IsReady", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (isReady is true)
            {
                return true;
            }

            var ensured = tide.GetMethod("EnsureReady", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            _hub.Log("chainloader", LogLevel.Info, $"Tide EnsureReady at commit: ready={ensured}");
            return ensured is true;
        }
        catch (Exception ex)
        {
            _hub.Log("chainloader", LogLevel.Warn, $"Tide readiness check reported: {ex.GetBaseException().Message}");
            return true;
        }
    }

    private static bool IsToleratedNativeBusy(Exception ex) =>
        ex.GetBaseException().Message.Contains("-3") ||
        ex.GetBaseException().Message.Contains("main-thread executor", StringComparison.OrdinalIgnoreCase);

    private void RegisterSlot(SlotStage stage)
    {
        var owner = GenerationDispatcher.SlotOwnerFor(stage.ModId, stage.SlotKey);
        switch (stage.Kind)
        {
            case PatchKind.CallbackGate:
                WaveBridge.HookGate(stage.Target!, owner, (Func<bool>)stage.Trampoline!);
                break;
            case PatchKind.CallbackObserver:
                WaveBridge.HookObserver(stage.Target!, owner, (Action)stage.Trampoline!);
                break;
            case PatchKind.CallbackPrefix:
                WaveBridge.PatchPrefix(stage.Target!, owner, stage.Trampoline!);
                break;
            case PatchKind.CallbackPostfix:
                WaveBridge.PatchPostfix(stage.Target!, owner, stage.Trampoline!);
                break;
            case PatchKind.Transpiler:
                WaveBridge.PatchTranspiler(stage.Target!, owner, stage.DirectCallback!);
                break;
            case PatchKind.Il2CppHook:
                WaveBridge.Il2CppHook(stage.Il2CppAssembly!, stage.Il2CppNs!, stage.Il2CppKlass!,
                    stage.Il2CppMethod!, stage.Il2CppArgCount, stage.DirectCallback!, owner);
                break;
            case PatchKind.Il2CppFull:
                WaveBridge.Il2CppHookFull(stage.Il2CppAssembly!, stage.Il2CppNs!, stage.Il2CppKlass!,
                    stage.Il2CppMethod!, stage.Il2CppArgCount, stage.Il2CppReturnKind, owner,
                    stage.DirectCallback, stage.DirectPostfix);
                break;
            case PatchKind.Il2CppTyped:
                WaveBridge.Il2CppHookTyped(stage.Il2CppAssembly!, stage.Il2CppNs!, stage.Il2CppKlass!,
                    stage.Il2CppMethod!, stage.Il2CppParameterTypes ?? Array.Empty<int>(), stage.Il2CppReturnType,
                    owner, stage.DirectCallback, stage.DirectPostfix);
                break;
            default:
                throw new InvalidOperationException($"Unknown patch kind {stage.Kind} for slot '{stage.ModId}:{stage.SlotKey}'.");
        }
    }

    /// <summary>
    /// Re-registers a RequiresPatchRebuild slot at commit: <c>Unpatch</c> old plus <c>Patch</c> new.
    /// The native rewrite is accepted and logged. Runs strictly post-publication, so in-flight
    /// execution resolved against exactly one snapshot throughout.
    /// </summary>
    private void RebuildSlot(SlotStage stage)
    {
        var owner = GenerationDispatcher.SlotOwnerFor(stage.ModId, stage.SlotKey);
        var sw = Stopwatch.StartNew();
        WaveBridge.TeardownOwner(owner, msg => _hub.Log("chainloader", LogLevel.Info, msg));
        RegisterSlot(stage);
        sw.Stop();
        _hub.Log("chainloader", LogLevel.Info,
            $"Rebuilt Wave slot '{owner}' for generation {stage.Slot.CurrentGeneration} ({sw.Elapsed.TotalMilliseconds:F1} ms native rewrite accepted).");
    }

    private static string HealthOf(ModGeneration generation) =>
        generation.Lifetime == LifetimeState.Quarantined ? "quarantined"
        : generation.Lifetime == LifetimeState.Failed ? "failed"
        : generation.ConsecutiveFailures > 0 ? "degraded" : "healthy";
    // ------------------------------------------------------------------
    // Loading: single boot commit
    // ------------------------------------------------------------------

    /// <summary>Loads and activates a single plugin. If it throws during construction or OnLoad, it is disabled, not fatal.</summary>
    public ModGeneration? LoadOne(PluginManifest manifest)
    {
        if (Config.EnabledPlugins.Count > 0 && !Config.EnabledPlugins.Any(pattern => EnabledMatch(pattern, manifest.Id)))
        {
            _hub.Log("chainloader", LogLevel.Info, $"Plugin '{manifest.Id}' is disabled by config");
            return null;
        }

        var record = GetOrCreateRecord(manifest.Id);
        var sequence = Interlocked.Increment(ref _sequenceSource);
        lock (_gate)
        {
            record.PendingSequence = sequence;
        }

        var transaction = new ReloadTransaction(manifest.Id, sequence);
        transaction.Sequences[manifest.Id] = sequence;
        var prepared = PrepareCandidate(transaction, manifest, record, isReload: false);
        if (prepared is null)
        {
            record.Active = null;
            _hub.Log("chainloader", LogLevel.Error, $"Failed to load plugin '{manifest.Id}': {transaction.Rejected.GetValueOrDefault(manifest.Id, "rejected")}");
            WriteStatus();
            return null;
        }

        return CommitSingle(prepared, record, transaction);
    }

    /// <summary>
    /// Commits one boot-time candidate: registers genuinely new Wave slots, publishes a snapshot
    /// adding the generation, and promotes its staged state. Boot has no predecessor to keep, so a
    /// failed Wave registration degrades the record to Failed rather than rejecting the load.
    /// </summary>
    private ModGeneration? CommitSingle(PreparedCandidate prepared, ModRecord record, ReloadTransaction transaction)
    {
        lock (_commitGate)
        {
            if (record.PendingSequence != transaction.Sequence)
            {
                transaction.Superseded = true;
                DisposeCandidate(prepared);
                record.Active = null;
                return null;
            }

            var id = prepared.Manifest.Id;
            var preSnap = Volatile.Read(ref _currentSnapshot);
            var generation = prepared.Generation;

            if (generation.Lifetime != LifetimeState.Failed)
            {
                foreach (var stage in prepared.SlotStages)
                {
                    var owner = GenerationDispatcher.SlotOwnerFor(stage.ModId, stage.SlotKey);
                    if (preSnap.DispatchSlots.ContainsKey(owner))
                    {
                        continue; // boot-time duplicate owner: Wave would refuse; snapshot keeps the first.
                    }

                    try
                    {
                        RegisterSlot(stage);
                    }
                    catch (Exception ex)
                    {
                        SetLifetime(generation, LifetimeState.Failed);
                        generation.LastError = ex.GetBaseException().Message;
                        generation.HookBindings.Clear();
                        prepared.SlotStages.Clear();
                        _hub.Log("chainloader", LogLevel.Error,
                            $"Plugin '{id}' failed hook registration ({ex.GetBaseException().Message}); loaded as Failed.");
                        break;
                    }
                }
            }

            // Pre-publication finalization (same rule as CommitGraph): the boot generation
            // publishes already Running with its record cell pointing at it, never half-formed.
            if (generation.Lifetime == LifetimeState.Starting)
            {
                SetLifetime(generation, LifetimeState.Running);
            }

            record.Current = generation;
            record.Active = null;

            var generations = new Dictionary<string, ModGeneration>(preSnap.Generations, StringComparer.OrdinalIgnoreCase);
            generations[id] = generation;
            var slots = new Dictionary<string, DispatchSlot>(preSnap.DispatchSlots, StringComparer.Ordinal);
            foreach (var stage in prepared.SlotStages)
            {
                slots[GenerationDispatcher.SlotOwnerFor(stage.ModId, stage.SlotKey)] = stage.Slot;
            }

            var dependencies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (existingId, existing) in generations)
            {
                var manifest = existingId.Equals(id, StringComparison.OrdinalIgnoreCase) ? prepared.Manifest : existing.Manifest;
                dependencies[existingId] = manifest.Dependencies.Select(d => d.Id).ToArray();
            }

            var version = Interlocked.Increment(ref _snapshotVersion);
            var candidate = new RuntimeSnapshot(version, generations, slots, dependencies);
            // Single atomic root replacement publishes the boot generation; no per-node sequence.
            Interlocked.Exchange(ref _currentSnapshot, candidate);
            transaction.CommitTime = DateTimeOffset.UtcNow;

            _hub.Log("chainloader", LogLevel.Info, $"Loaded {prepared.Manifest.Id} {prepared.Manifest.Version} ({Path.GetFileName(prepared.Manifest.AssemblyPath)})");

            if (prepared.StagedState is not null)
            {
                try
                {
                    _stateStore.Commit(id, prepared.StagedState);
                }
                catch (Exception ex)
                {
                    _hub.Log("chainloader", LogLevel.Warn, $"State commit for '{id}' reported: {ex.GetBaseException().Message}");
                }
            }

            if (prepared.Context.State is StagingModState staging)
            {
                staging.Promote();
            }

            WriteStatus();
            return generation;
        }
    }

    // ------------------------------------------------------------------
    // Tick: update + command drain
    // ------------------------------------------------------------------

    /// <summary>Advances every active plugin's update loop and drains pending reload commands.</summary>
    public void UpdateAll()
    {
        DrainCommands();
        var snapshot = Volatile.Read(ref _currentSnapshot);

        foreach (var plugin in snapshot.Generations.Values)
        {
            if (plugin.Lifetime != LifetimeState.Running)
            {
                continue;
            }

            _currentProfiler = Config.Profiler.Enabled ? plugin.Profiler : null;
            if (Config.Profiler.Enabled)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    plugin.Update(this);
                }
                catch (GenerationRetiredException)
                {
                    // Retired between the snapshot read and entry: no new execution, skip.
                }

                sw.Stop();
                plugin.Profiler.RecordTick(sw.Elapsed.TotalMilliseconds);
            }
            else
            {
                try
                {
                    plugin.Update(this);
                }
                catch (GenerationRetiredException)
                {
                    // Same retire-vs-enter race: rejection is the correct outcome.
                }
            }
        }

        _currentProfiler = null;
        MaybeLogProfilerSummary();
    }

    private void MaybeLogProfilerSummary()
    {
        if (!Config.Profiler.Enabled)
        {
            return;
        }

        var intervalTicks = Math.Max(1, (int)(Config.Profiler.SummaryIntervalSeconds * 1000.0 / 16.0));
        if (Interlocked.Increment(ref _tickCounter) % intervalTicks != 0)
        {
            return;
        }

        var snapshot = Volatile.Read(ref _currentSnapshot);
        foreach (var plugin in snapshot.Generations.Values)
        {
            if (plugin.Lifetime == LifetimeState.Running && plugin.Profiler.HasSamples)
            {
                _hub.Log("profiler", LogLevel.Info,
                    $"mod '{plugin.Manifest.Id}' gen={plugin.Generation} {plugin.Profiler.ToSummaryLine()}");
            }
        }
    }

    /// <summary>Disables a plugin that has exceeded the quarantine threshold, keeping the game running.</summary>
    public void Quarantine(ModGeneration plugin, Exception cause)
    {
        // Quarantine is per (modId, generation): only a Running current can be quarantined, and the
        // retired generation is never reactivated (recovery is a new validated transaction).
        if (plugin.Lifetime != LifetimeState.Running)
        {
            return;
        }

        SetLifetime(plugin, LifetimeState.Quarantined);
        plugin.QuarantinedAt = DateTimeOffset.UtcNow;
        _hub.Log("chainloader", LogLevel.Error,
            $"Quarantining plugin '{plugin.Manifest.Id}' after {plugin.ConsecutiveFailures} consecutive failures: {cause.Message}");
        PluginQuarantined?.Invoke(plugin);
        // The retire gate rejects new ordinary execution immediately; teardown runs on the drain.
        plugin.RetireGate();
        EnqueueCommand(() => QuarantineTeardown(plugin));
    }

    /// <summary>
    /// Quarantine fast-path teardown: same ordering guarantees as the retirement pipeline (reject
    /// new, drain, OnUnload once, dispose, unload) but the record keeps its terminal
    /// <see cref="LifetimeState.Quarantined"/> marker instead of walking the Retiring chain, and a
    /// fresh snapshot without the mod publishes on the drain.
    /// </summary>
    private void QuarantineTeardown(ModGeneration plugin)
    {
        lock (_commitGate)
        {
            var id = plugin.Manifest.Id;
            var preSnap = Volatile.Read(ref _currentSnapshot);
            if (preSnap.Generations.TryGetValue(id, out var current) && ReferenceEquals(current, plugin))
            {
                var generations = new Dictionary<string, ModGeneration>(preSnap.Generations, StringComparer.OrdinalIgnoreCase);
                generations.Remove(id);
                var slots = new Dictionary<string, DispatchSlot>(preSnap.DispatchSlots, StringComparer.Ordinal);
                var prefix = $"nami:gen:{id}:";
                foreach (var key in slots.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    slots.Remove(key);
                }

                var dependencies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (existingId, existing) in generations)
                {
                    dependencies[existingId] = existing.Manifest.Dependencies.Select(d => d.Id).ToArray();
                }

                Interlocked.Exchange(ref _currentSnapshot,
                    new RuntimeSnapshot(Interlocked.Increment(ref _snapshotVersion), generations, slots, dependencies));
            }

            lock (_gate)
            {
                if (_retiring.All(r => !ReferenceEquals(r.Generation, plugin)))
                {
                    _retiring.Add(new RetireState { Generation = plugin, ReleaseSlots = true, IsQuarantine = true });
                }

                if (_records.TryGetValue(id, out var record) && ReferenceEquals(record.Current, plugin))
                {
                    record.Current = null;
                }
            }

            WriteStatus();
            if (!DrainAndTeardown(plugin, releaseSlots: true, isQuarantine: true))
            {
                EnqueueCommand(() => ReDriveTeardown(id, plugin.Generation));
            }
        }
    }

    // ------------------------------------------------------------------
    // Retirement pipeline: publish -> retire -> drain -> teardown -> reclaim
    // ------------------------------------------------------------------

    /// <summary>
    /// Retires one superseded generation in reverse-dependency order (callers order the sequence).
    /// Strictly post-publication: the new snapshot is already live, so this step only removes
    /// eligibility. Retirement issues here never roll back the published snapshot.
    /// </summary>
    private void RetireGeneration(ModGeneration old, bool releaseSlots)
    {
        if (old.Lifetime != LifetimeState.Running)
        {
            _hub.Log("chainloader", LogLevel.Info,
                $"Retire skipped for '{old.Manifest.Id}' generation {old.Generation} (lifetime {old.Lifetime}).");
            return;
        }

        SetLifetime(old, LifetimeState.Retiring);
        // Cooperative cancellation starts at retire start; survivors are reported, never terminated.
        old.Resources.BeginRetirement();
        var outstanding = old.RetireGate();
        // Per-slot retire order: the new snapshot already published; now remove old bindings from
        // eligibility. Callback references drop only after quiescence (see CompleteTeardown).
        for (var i = 0; i < old.HookBindings.Count; i++)
        {
            if (old.HookBindings[i].State == BindingState.Active)
            {
                old.HookBindings[i] = old.HookBindings[i] with { State = BindingState.Retired };
            }
        }

        // Direct (non-slot) Wave registrations keep teardown-at-retire semantics (brief unhooked
        // window, documented — not an error). Slot trampolines stay: the current snapshot routes them.
        WaveBridge.TeardownOwner(old.Manifest.Id, msg => _hub.Log("chainloader", LogLevel.Info, msg));

        lock (_gate)
        {
            _retiring.Add(new RetireState { Generation = old, ReleaseSlots = releaseSlots });
        }

        _hub.Log("chainloader", LogLevel.Info,
            $"Retiring '{old.Manifest.Id}' generation {old.Generation} ({outstanding} lease(s) outstanding).");
        SetLifetime(old, LifetimeState.Quiescing);
        WriteStatus();
        if (!DrainAndTeardown(old, releaseSlots, isQuarantine: false))
        {
            EnqueueCommand(() => ReDriveTeardown(old.Manifest.Id, old.Generation));
        }
    }

    /// <summary>
    /// Waits for ordinary leases to drain (quiesce), then completes teardown. Returns true when
    /// teardown finished; false when the drain budget expired (parked at RetirementBlocked with a
    /// re-drive enqueued). Cooperative throughout: Nami never terminates the lease holders.
    /// </summary>
    private bool DrainAndTeardown(ModGeneration generation, bool releaseSlots, bool isQuarantine)
    {
        var budget = TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        while (generation.ActiveExecutions > 0 && sw.Elapsed < budget)
        {
            Thread.Sleep(10);
        }

        if (generation.ActiveExecutions > 0)
        {
            if (generation.Lifetime == LifetimeState.Quiescing)
            {
                SetLifetime(generation, LifetimeState.RetirementBlocked);
            }

            _hub.Log("chainloader", LogLevel.Warn,
                $"Quiescence blocked for '{generation.Manifest.Id}' generation {generation.Generation}: " +
                $"{generation.ActiveExecutions} ordinary lease(s) still held; parked at RetirementBlocked (re-drive queued).");
            WriteStatus();
            return false;
        }

        return CompleteTeardown(generation, releaseSlots, isQuarantine);
    }

    /// <summary>
    /// Schedules one future re-drive after a backoff (fire-and-forget on the shared pool timer;
    /// no new timer types, no polling, no tick stall). Failures are logged, never thrown; a
    /// disposed chainloader drops the drive. Keeps a blocked teardown chain alive across ticks:
    /// each drive re-checks the drain, so a late-resolving drain still completes teardown.
    /// </summary>
    private void ScheduleReDrive(string modId, int generationNumber)
    {
        Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                EnqueueCommand(() => ReDriveTeardown(modId, generationNumber));
            }
            catch (Exception ex)
            {
                _hub.Log("chainloader", LogLevel.Warn,
                    $"Deferred re-drive for '{modId}' generation {generationNumber} reported: {ex.GetBaseException().Message}");
            }
        }, TaskScheduler.Default);
    }

    private void ReDriveTeardown(string modId, int generationNumber)
    {
        RetireState? state;
        lock (_gate)
        {
            state = _retiring.FirstOrDefault(r =>
                r.Generation.Manifest.Id.Equals(modId, StringComparison.OrdinalIgnoreCase) &&
                r.Generation.Generation == generationNumber);
            if (state is not null)
            {
                state.Drives++;
            }
        }

        if (state is null)
        {
            return; // Already reclaimed.
        }

        lock (_commitGate)
        {
            var generation = state.Generation;
            if (generation.ActiveExecutions > 0)
            {
                if (state.Drives >= 12)
                {
                    // Bounded patience (~60s: 12 drives spaced by the 5s backoff below): the drain
                    // is honestly stuck (a thread is wedged in mod code Nami will never terminate).
                    // The record stays RetirementBlocked with its blockers visible in status; the
                    // operator owns recovery. No polling beyond this.
                    _hub.Log("chainloader", LogLevel.Error,
                        $"Quiescence for '{modId}' generation {generationNumber} still blocked after ~60s; " +
                        "leaving RetirementBlocked (operator action required; ALC reclamation deferred).");
                    return;
                }

                // Deferred, never spun: a same-drain re-enqueue would busy-loop while the lease is
                // held (burning the whole drive budget in milliseconds and stalling the tick), so
                // the next drive is scheduled after a backoff. The chain stays alive across ticks
                // without polling; the lease release itself need not kick anything.
                ScheduleReDrive(modId, generationNumber);
                return;
            }

            if (generation.Lifetime == LifetimeState.RetirementBlocked)
            {
                // Drain resolved: back to Quiescing, then teardown.
                SetLifetime(generation, LifetimeState.Quiescing);
            }

            if (!CompleteTeardown(generation, state.ReleaseSlots, state.IsQuarantine))
            {
                EnqueueCommand(() => ReDriveTeardown(modId, generationNumber));
            }
        }
    }

    /// <summary>
    /// True when the current snapshot resolves <paramref name="owner"/> to a generation other
    /// than <paramref name="retiringGeneration"/> (a reload committed after the retire gate, so
    /// tearing this owner down would unpatch the replacement's live slot). An owner absent from
    /// the snapshot is stale-but-ours: teardown proceeds. Pure for testability.
    /// </summary>
    internal static bool IsOwnerSuperseded(string owner, int retiringGeneration, RuntimeSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.DispatchSlots.TryGetValue(owner, out var current) &&
            current.CurrentGeneration != retiringGeneration;
    }

    /// <summary>
    /// Post-quiescence teardown in contract order: enter teardown (gate-enforced) → OnUnload
    /// exactly once → dispose resources (report survivors) → release callback refs → request ALC
    /// unload → observe. Returns true when finished.
    /// </summary>
    private bool CompleteTeardown(ModGeneration generation, bool releaseSlots, bool isQuarantine)
    {
        if (!isQuarantine)
        {
            try
            {
                generation.EnterTeardown();
            }
            catch (InvalidOperationException ex)
            {
                _hub.Log("chainloader", LogLevel.Warn, $"Teardown deferred for '{generation.Manifest.Id}': {ex.Message}");
                return false;
            }
        }
        else if (generation.ActiveExecutions != 0)
        {
            // Quarantine path: Lifetime stays Quarantined (terminal — it never enters Quiescing
            // and no edge is added for it), so the EnterTeardown gate above cannot apply. The same
            // guarantee is asserted explicitly instead: the retire gate rejected new ordinary
            // execution at quarantine time, but leases held from BEFORE the quarantine still drain
            // first — OnUnload must never run under one. Defers exactly like the gate refusal
            // above (the caller queues a re-drive).
            _hub.Log("chainloader", LogLevel.Warn,
                $"Quarantine teardown deferred for '{generation.Manifest.Id}': {generation.ActiveExecutions} ordinary lease(s) still held.");
            return false;
        }
        // Non-quarantine teardown passed the gate; quarantine teardown passed the explicit
        // zero-lease assertion above (Lifetime stays Quarantined, terminal). Either way OnUnload
        // below cannot race ordinary execution.

        generation.RunOnUnloadOnce(msg => _hub.Log("chainloader", LogLevel.Warn, msg));

        RetirementReport report;
        try
        {
            // Bounded cooperative wait: tasks that ignore Lifetime keep running as survivors and may
            // block reclamation; they are reported, never terminated.
            report = generation.Resources.DisposeAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            report = new RetirementReport(false, new[] { $"dispose-walk: {ex.GetBaseException().Message}" });
        }

        string[] blockers = report.Completed ? Array.Empty<string>() : report.Survivors.ToArray();
        if (!report.Completed)
        {
            _hub.Log("chainloader", LogLevel.Warn,
                $"Retirement survivors for '{generation.Manifest.Id}' generation {generation.Generation}: {string.Join(", ", blockers)}");
        }

        if (releaseSlots)
        {
            // Removed (not replaced) mods: unregister per-slot Wave entries post-quiescence. Replaced
            // mods keep their slot trampolines: the current snapshot routes them to the new generation.
            // Generation-identity recheck per owner: a reload may have committed between this
            // generation's RetireGate and this drain (the quarantine path is the sharp edge — its
            // teardown is queued while the mod stays reloadable), so the same slot owner can now
            // serve a newer generation of the same mod. Unpatching it would tear down the
            // replacement's live slot; owners the snapshot resolves to another generation are
            // skipped (reported, never an error). No deterministic out-of-game test covers the
            // interleaving itself (fixtures register no Wave slots and Wave is absent here, so no
            // shared owner can exist); the predicate below is unit-tested instead (see
            // LiveDispatchTests.OwnerSuperseded_*).
            var live = Volatile.Read(ref _currentSnapshot);
            foreach (var binding in generation.HookBindings)
            {
                if (IsOwnerSuperseded(binding.Owner, generation.Generation, live))
                {
                    _hub.Log("chainloader", LogLevel.Info,
                        $"Skipping Wave teardown for '{binding.Owner}': now owned by a newer generation of '{binding.ModId}'.");
                    continue;
                }

                WaveBridge.TeardownOwner(binding.Owner, msg => _hub.Log("chainloader", LogLevel.Info, msg));
            }
        }

        // Callback references drop only after quiescence: the old snapshot (with old callbacks) is
        // already unreachable except by drained in-flight calls, and the ownership records clear here.
        generation.HookBindings.Clear();

        if (!isQuarantine)
        {
            SetLifetime(generation, LifetimeState.Reclaiming);
        }

        // Last uses of `generation` are below: scalars are captured, the (non-quarantine)
        // UnloadRequested edge is taken, and the strong _retiring entry is dropped BEFORE
        // requesting unload. Everything after the unload call uses scalars and the weak
        // tracker only, so no caller frame keeps ALC-internal references (assemblies, types,
        // the plugin instance) alive across Unload(): the JIT can reuse the generation slot
        // at the call. UnloadRequested lands just before the synchronous Unload request —
        // no await or yield sits between them, so no observer can interleave in the gap.
        // Re-drive safety: this section only runs when teardown will report finished (the
        // deferred path returns above), so dropping the _retiring entry here loses no drive.
        if (!isQuarantine)
        {
            SetLifetime(generation, LifetimeState.UnloadRequested);
        }

        var unloadedModId = generation.Manifest.Id;
        var unloadedGeneration = generation.Generation;
        var unloadedLifetime = generation.Lifetime.ToString();
        var alc = generation.LoadContext;
        lock (_gate)
        {
            _retiring.RemoveAll(r => ReferenceEquals(r.Generation, generation));
        }

        // `generation` is dead from here on by construction: the NoInlining helper takes only
        // the context and returns only the WeakReference (exactly the documented
        // ExecuteAndUnload shape). Collection is observed afterward, never forced (GC.Collect
        // appears in tests and diagnostics only, never on runtime paths).
        var tracker = ReleaseAlcNoInline(alc);

        lock (_gate)
        {
            _reclaiming.Add(new ReclaimInfo
            {
                ModId = unloadedModId,
                GenerationId = unloadedGeneration,
                Outcome = report,
                AlcTracker = tracker,
                Blockers = blockers,
                Lifetime = unloadedLifetime,
            });
            _retirementRecords.Add(new RetirementRecord(
                unloadedModId, unloadedGeneration, tracker, report));
        }

        _hub.Log("chainloader", LogLevel.Info,
            $"Unloaded '{unloadedModId}' (gen {unloadedGeneration}); reclamation observed best-effort (see status).");
        WriteStatus();
        ObserveReclamation();
        return true;
    }

    /// <summary>
    /// The code path that drops the last strong references and calls <c>Unload()</c>: isolated so
    /// this frame holds no ALC-internal references in locals across the call (only the context
    /// itself plus the fresh weak reference), exactly like the documented ExecuteAndUnload pattern.
    /// Returns only the <see cref="WeakReference"/>; collection is observed afterward, never forced
    /// (<c>GC.Collect</c> appears in tests and diagnostics only, never on runtime paths).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReleaseAlcNoInline(PluginLoadContext alc)
    {
        var tracker = new WeakReference(alc, trackResurrection: false);
        alc.Unload();
        return tracker;
    }

    /// <summary>
    /// Observes (never forces) collection of unloaded ALCs. Called on transitions (commit, teardown,
    /// shutdown), never per-frame: the hot path stays event-driven with no GC polling.
    /// In-memory generations end at UnloadRequested; Collected is recorded on the weak-side
    /// tracking once the tracker dies (the LifetimeState edge exists for direct transitions).
    /// </summary>
    private void ObserveReclamation()
    {
        var collected = new List<string>();
        lock (_gate)
        {
            foreach (var info in _reclaiming)
            {
                if (!info.ObservedCollected && !info.AlcTracker.IsAlive)
                {
                    info.ObservedCollected = true;
                    collected.Add($"{info.ModId}#{info.GenerationId}");
                }
            }
        }

        foreach (var id in collected)
        {
            _hub.Log("chainloader", LogLevel.Info, $"ALC collected: {id}.");
        }

        if (collected.Count > 0)
        {
            WriteStatus();
        }
    }

    // ------------------------------------------------------------------
    // Hot reload: coordinator (prepare anywhere, commit on the drain)
    // ------------------------------------------------------------------

    /// <summary>Queues a reload of <paramref name="pluginId"/> at the next safe point. Returns true if queued.</summary>
    public bool RequestReload(string pluginId)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (!snapshot.Generations.ContainsKey(pluginId))
        {
            return false;
        }

        // Bump first so in-flight background prepares supersede at their next checkpoint.
        var record = GetOrCreateRecord(pluginId);
        lock (_gate)
        {
            record.PendingSequence = Interlocked.Increment(ref _sequenceSource);
        }

        // Interactive requests commit synchronously on the drain (the test-visible contract:
        // queued now, applied on the next tick). File-watch bursts prepare in the background.
        EnqueueCommand(() => ReloadCore(pluginId));
        return true;
    }

    /// <summary>Reloads a plugin (and its transitive dependents) immediately. For tests and the CLI.</summary>
    public ReloadResult Reload(string pluginId) => ReloadCore(pluginId);

    /// <summary>
    /// Implements a reload: prepares the entire candidate graph privately (target plus transitive
    /// dependents), validates it, publishes ONE immutable snapshot, then retires the old graph.
    /// Publication is non-failing by construction (fully built and validated before the exchange;
    /// no user code, allocation-free of fallible work, I/O, or hook construction happens inside
    /// the publication itself). A D-only reload touches only D. A failed candidate keeps its
    /// previous generation running; retirement issues never roll back the published snapshot.
    /// </summary>
    private ReloadResult ReloadCore(string pluginId)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        var target = snapshot.Generations.Values
            .FirstOrDefault(p => p.Manifest.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            // Not currently loaded - for the watcher path this is a brand-new mod drop (or a
            // previously failed load): load it fresh if a manifest for the id exists on disk.
            var fresh = DiscoverPlugins()
                .FirstOrDefault(m => m.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (fresh is null)
            {
                _hub.Log("chainloader", LogLevel.Warn, $"Reload: '{pluginId}' is not loaded and has no manifest on disk");
                return new ReloadResult(Array.Empty<string>(), Array.Empty<string>(), new[] { pluginId });
            }

            var record = GetOrCreateRecord(fresh.Id);
            var sequence = Interlocked.Increment(ref _sequenceSource);
            lock (_gate)
            {
                record.PendingSequence = sequence;
            }

            var freshTx = new ReloadTransaction(fresh.Id, sequence);
            freshTx.Sequences[fresh.Id] = sequence;
            var prepared = PrepareCandidate(freshTx, fresh, record, isReload: false);
            if (prepared is null)
            {
                record.Active = null;
                PluginReloaded?.Invoke(new HotReloadEventArgs(pluginId, 0, 0, false, "load failed (see log)"));
                RecordHistory(pluginId, 0, 0, false, "load failed (see log)", freshTx.PrepTime.Elapsed);
                WriteStatus();
                return new ReloadResult(Array.Empty<string>(), Array.Empty<string>(), new[] { pluginId });
            }

            var loaded = CommitSingle(prepared, record, freshTx);
            if (loaded is null)
            {
                PluginReloaded?.Invoke(new HotReloadEventArgs(pluginId, 0, 0, false, "superseded before commit"));
                RecordHistory(pluginId, 0, 0, false, "superseded before commit", freshTx.PrepTime.Elapsed);
                WriteStatus();
                return new ReloadResult(Array.Empty<string>(), Array.Empty<string>(), new[] { pluginId });
            }

            _hub.Log("chainloader", LogLevel.Info, $"Discovered new plugin '{fresh.Id}' gen {loaded.Generation}");
            PluginReloaded?.Invoke(new HotReloadEventArgs(fresh.Id, 0, loaded.Generation, true, null) { Generation = loaded });
            RecordHistory(fresh.Id, 0, loaded.Generation, true, null, freshTx.PrepTime.Elapsed);
            WriteStatus();
            return new ReloadResult(new[] { fresh.Id }, Array.Empty<string>(), Array.Empty<string>());
        }

        var transaction = PrepareGraph(new[] { pluginId }, isReload: true);
        return CommitGraph(transaction);
    }

    /// <summary>
    /// Prepares the entire candidate graph for a reload set: expands the trigger to its transitive
    /// dependents, re-discovers manifests (new files and removals honored), resolves the full graph,
    /// and prepares one candidate per affected id in dependency order (initialization order A→B→C
    /// inside preparation). Pure preparation: no snapshot mutation, no Wave calls.
    /// </summary>
    private ReloadTransaction PrepareGraph(IReadOnlyList<string> triggerIds, bool isReload)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        var currents = snapshot.Generations.Values.ToArray();

        // Expand: trigger plus everything depending on it, transitively.
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in triggerIds)
        {
            expanded.Add(trigger);
            foreach (var dependent in FindTransitiveDependents(currents, trigger))
            {
                expanded.Add(dependent);
            }
        }

        var discovered = DiscoverPlugins().ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var resolution = DependencyResolver.Resolve(discovered.Values.ToList());
        var ordered = resolution.LoadOrder.Where(m => expanded.Contains(m.Id)).ToList();
        var removed = expanded.Where(id => !discovered.ContainsKey(id)).ToList();

        var transaction = new ReloadTransaction(triggerIds.Count == 1 ? triggerIds[0] : "<graph>", 0);
        transaction.ReloadIds.AddRange(ordered.Select(m => m.Id));
        transaction.Removed.AddRange(removed);
        transaction.CommitOrder.AddRange(ordered.Select(m => m.Id));
        foreach (var skipped in resolution.Skipped.Where(kv => expanded.Contains(kv.Key)))
        {
            transaction.Rejected[skipped.Key] = $"dependency resolution skipped it ({skipped.Value})";
        }

        foreach (var id in expanded)
        {
            var record = GetOrCreateRecord(id);
            var sequence = Interlocked.Increment(ref _sequenceSource);
            lock (_gate)
            {
                record.PendingSequence = sequence;
            }

            transaction.Sequences[id] = sequence;
            transaction.Previous[id] = snapshot.Generations.TryGetValue(id, out var old) ? old : null;
        }

        foreach (var manifest in ordered)
        {
            if (transaction.Rejected.ContainsKey(manifest.Id))
            {
                continue;
            }

            var record = GetOrCreateRecord(manifest.Id);
            var prepared = PrepareCandidate(transaction, manifest, record, isReload);
            if (prepared is not null)
            {
                transaction.Prepared[manifest.Id] = prepared;
            }
        }

        return transaction;
    }

    /// <summary>
    /// Commit-time Wave-work decision per slot: genuinely new slots were already registered
    /// pre-publication (no work here); existing slots reinstall when they register directly
    /// (transpiler/IL2CPP, no trampoline) or use a per-generation trampoline (!Stable, which
    /// would otherwise root the old ALC). Stable trampolines normally swap via the snapshot
    /// publish alone — except when the callback delegate TYPE changed across generations: the
    /// registered trampoline baked <c>castclass</c> &lt;v1-callbackType&gt;, so dispatching the
    /// new callback through it throws <c>InvalidCastException</c>. That mismatch forces
    /// <see cref="ReloadCapability.RequiresPatchRebuild"/> for the slot (re-register at commit),
    /// never an invalid cast at dispatch. Pure for testability.
    /// </summary>
    internal static bool NeedsSlotReinstall(SlotStage stage, IReadOnlyDictionary<string, DispatchSlot> preSlots)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(preSlots);
        var owner = GenerationDispatcher.SlotOwnerFor(stage.ModId, stage.SlotKey);
        if (!preSlots.TryGetValue(owner, out var previous))
        {
            return false;
        }

        if (stage.Trampoline is null || !stage.Stable)
        {
            return true;
        }

        return previous.Current.GetType() != stage.Slot.Current.GetType();
    }

    /// <summary>
    /// Commits a prepared graph under the single-commit gate: re-checks supersede per id, checks
    /// per-id consistency, registers genuinely new Wave slots, publishes the complete immutable
    /// snapshot with ONE exchange, re-registers rebuild slots post-publication, then retires the
    /// old graph in reverse dependency order (C→B→A). Never publishes nodes sequentially.
    /// </summary>
    private ReloadResult CommitGraph(ReloadTransaction transaction)
    {
        lock (_commitGate)
        {
            EnsureTideReady();
            var preSnap = Volatile.Read(ref _currentSnapshot);
            var committable = new List<string>();

            foreach (var id in transaction.CommitOrder)
            {
                if (!transaction.Prepared.TryGetValue(id, out var prepared))
                {
                    continue; // rejected during preparation; old generation kept.
                }

                if (!transaction.Sequences.TryGetValue(id, out var sequence) ||
                    GetOrCreateRecord(id).PendingSequence != sequence)
                {
                    transaction.Superseded = true;
                    DisposeCandidate(prepared);
                    transaction.Prepared.Remove(id);
                    _hub.Log("chainloader", LogLevel.Info,
                        $"Candidate for '{id}' superseded before commit; keeping previous generation.");
                    continue;
                }

                // Consistency: every dependency must resolve inside the post-commit map.
                var postKeys = new HashSet<string>(preSnap.Generations.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var removed in transaction.Removed)
                {
                    postKeys.Remove(removed);
                }

                foreach (var key in transaction.Prepared.Keys)
                {
                    postKeys.Add(key);
                }

                var missing = prepared.Manifest.Dependencies
                    .Select(d => d.Id).Where(dep => !postKeys.Contains(dep)).ToArray();
                if (missing.Length > 0)
                {
                    transaction.Rejected[id] = $"unsatisfied dependencies in the new graph ({string.Join(", ", missing)})";
                    DisposeCandidate(prepared);
                    transaction.Prepared.Remove(id);
                    continue;
                }

                committable.Add(id);
            }

            // Pre-publication Wave work for genuinely new slots only (invisible until publish, so a
            // registration failure still rejects the candidate with the old generation live).
            foreach (var id in committable.ToArray())
            {
                var prepared = transaction.Prepared[id];
                var slotFailed = false;
                foreach (var stage in prepared.SlotStages)
                {
                    var owner = GenerationDispatcher.SlotOwnerFor(stage.ModId, stage.SlotKey);
                    if (preSnap.DispatchSlots.ContainsKey(owner))
                    {
                        continue;
                    }

                    try
                    {
                        RegisterSlot(stage);
                    }
                    catch (Exception ex) when (IsToleratedNativeBusy(ex))
                    {
                        // Tolerate -3 (main-thread executor unavailable): keep the candidate, escalate
                        // the slot to RestartRequired, and report instead of forcing native behavior.
                        _hub.Log("chainloader", LogLevel.Warn,
                            $"Slot '{owner}' deferred: main-thread executor unavailable (-3 tolerated); reported RestartRequired.");
                        var index = prepared.Generation.HookBindings.FindIndex(b =>
                            b.SlotKey == stage.SlotKey && b.GenerationId == stage.Slot.CurrentGeneration);
                        if (index >= 0)
                        {
                            prepared.Generation.HookBindings[index] =
                                prepared.Generation.HookBindings[index] with { Capability = ReloadCapability.RestartRequired };
                        }
                    }
                    catch (Exception ex)
                    {
                        transaction.Rejected[id] = $"hook registration failed ({ex.GetBaseException().Message})";
                        DisposeCandidate(prepared);
                        transaction.Prepared.Remove(id);
                        slotFailed = true;
                        break;
                    }
                }

                if (slotFailed)
                {
                    committable.Remove(id);
                }
            }

            if (committable.Count == 0 && transaction.Removed.Count == 0)
            {
                foreach (var (id, reason) in transaction.Rejected)
                {
                    var oldGen = transaction.Previous.GetValueOrDefault(id)?.Generation ?? 0;
                    PluginReloaded?.Invoke(new HotReloadEventArgs(id, oldGen, 0, false, reason));
                    RecordHistory(id, oldGen, 0, false, reason, transaction.PrepTime.Elapsed);
                }

                ClearTransaction(transaction);
                WriteStatus();
                var failedIds = transaction.CommitOrder
                    .Where(id => !committable.Contains(id, StringComparer.OrdinalIgnoreCase))
                    .Concat(transaction.Rejected.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return new ReloadResult(Array.Empty<string>(), Array.Empty<string>(), failedIds);
            }

            // Pre-publication finalization: the candidate snapshot must publish fully-formed
            // objects. Lifetime, ReloadCount, and record cells are assigned BEFORE the exchange
            // so concurrent snapshot readers never observe partial counts (a Running generation
            // with ReloadCount still 0, or a record still pointing at the predecessor).
            foreach (var id in committable)
            {
                var staged = transaction.Prepared[id].Generation;
                if (staged.Lifetime == LifetimeState.Starting)
                {
                    SetLifetime(staged, LifetimeState.Running);
                }

                var stagedPrevious = transaction.Previous.GetValueOrDefault(id);
                var stagedCount = (stagedPrevious?.ReloadCount ?? 0) + 1;
                staged.ReloadCount = stagedCount;
                var stagedRecord = GetOrCreateRecord(id);
                stagedRecord.ReloadCount = stagedCount;
                stagedRecord.Current = staged;
            }

            // Build the complete candidate snapshot: fully constructed and validated before the exchange.
            var generations = new Dictionary<string, ModGeneration>(preSnap.Generations, StringComparer.OrdinalIgnoreCase);
            var slots = new Dictionary<string, DispatchSlot>(preSnap.DispatchSlots, StringComparer.Ordinal);
            foreach (var id in committable)
            {
                generations[transaction.Prepared[id].Manifest.Id] = transaction.Prepared[id].Generation;
                foreach (var stage in transaction.Prepared[id].SlotStages)
                {
                    slots[GenerationDispatcher.SlotOwnerFor(stage.ModId, stage.SlotKey)] = stage.Slot;
                }
            }

            foreach (var removed in transaction.Removed)
            {
                generations.Remove(removed);
                var prefix = $"nami:gen:{removed}:";
                foreach (var key in slots.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    slots.Remove(key);
                }
            }

            var dependencies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (existingId, existing) in generations)
            {
                var manifest = transaction.Prepared.TryGetValue(existingId, out var prep)
                    ? prep.Manifest : existing.Manifest;
                dependencies[existingId] = manifest.Dependencies.Select(d => d.Id).ToArray();
            }

            var at = DateTimeOffset.UtcNow;
            var candidate = new RuntimeSnapshot(
                Interlocked.Increment(ref _snapshotVersion), generations, slots, dependencies);
            // THE publication: one atomic root replacement. No user code, I/O, hook construction, or
            // other fallible work happens inside this step by construction.
            Interlocked.Exchange(ref _currentSnapshot, candidate);
            transaction.CommitTime = at;


            // Post-publication Wave rebuilds for slots that need them (never roll back on failure).
            foreach (var id in committable)
            {
                foreach (var stage in transaction.Prepared[id].SlotStages)
                {
                    var owner = GenerationDispatcher.SlotOwnerFor(stage.ModId, stage.SlotKey);
                    var reinstall = NeedsSlotReinstall(stage, preSnap.DispatchSlots);
                    if (reinstall && stage.Trampoline is not null && stage.Stable &&
                        preSnap.DispatchSlots.TryGetValue(owner, out var previousSlot) &&
                        previousSlot.Current.GetType() != stage.Slot.Current.GetType())
                    {
                        // Cross-generation delegate-type mismatch on a stable slot: the registered
                        // trampoline baked castclass <v1-callbackType>, so dispatching the new
                        // callback through it would throw InvalidCastException. Escalate honestly
                        // and re-register with the new trampoline at commit (never force the swap).
                        var bindings = transaction.Prepared[id].Generation.HookBindings;
                        var index = bindings.FindIndex(b =>
                            b.SlotKey == stage.SlotKey && b.GenerationId == stage.Slot.CurrentGeneration);
                        if (index >= 0 && bindings[index].Capability != ReloadCapability.RequiresPatchRebuild)
                        {
                            bindings[index] = bindings[index] with { Capability = ReloadCapability.RequiresPatchRebuild };
                        }

                        _hub.Log("chainloader", LogLevel.Info,
                            $"Slot '{owner}' callback type changed " +
                            $"({previousSlot.Current.GetType().Name} -> {stage.Slot.Current.GetType().Name}); " +
                            "RequiresPatchRebuild (re-registering at commit).");
                    }

                    if (!reinstall)
                    {
                        continue;
                    }

                    try
                    {
                        RebuildSlot(stage);
                    }
                    catch (Exception ex) when (IsToleratedNativeBusy(ex))
                    {
                        _hub.Log("chainloader", LogLevel.Warn,
                            $"Slot '{owner}' rebuild deferred: main-thread executor unavailable (-3 tolerated).");
                    }
                    catch (Exception ex)
                    {
                        _hub.Log("chainloader", LogLevel.Error,
                            $"Slot '{owner}' rebuild failed after publication (snapshot stands): {ex.GetBaseException().Message}");
                    }
                }
            }

            var loaded = new List<string>();
            foreach (var id in committable)
            {
                var prepared = transaction.Prepared[id];
                var generation = prepared.Generation;
                var record = GetOrCreateRecord(id);
                var previous = transaction.Previous.GetValueOrDefault(id);
                record.Active = null;
                if (prepared.StagedState is not null)
                {
                    try
                    {
                        _stateStore.Commit(id, prepared.StagedState);
                    }
                    catch (Exception ex)
                    {
                        _hub.Log("chainloader", LogLevel.Warn, $"State commit for '{id}' reported: {ex.GetBaseException().Message}");
                    }
                }

                if (prepared.Context.State is StagingModState staging)
                {
                    staging.Promote();
                }

                loaded.Add(id);
                var from = previous?.Generation ?? 0;
                _hub.Log("chainloader", LogLevel.Info, $"Reloaded '{id}' gen {from} -> gen {generation.Generation}");
                PluginReloaded?.Invoke(new HotReloadEventArgs(id, from, generation.Generation, true, null) { Generation = generation });
                RecordHistory(id, from, generation.Generation, true, null, transaction.PrepTime.Elapsed);
            }

            foreach (var (id, reason) in transaction.Rejected)
            {
                var oldGen = transaction.Previous.GetValueOrDefault(id)?.Generation ?? 0;
                PluginReloaded?.Invoke(new HotReloadEventArgs(id, oldGen, 0, false, reason));
                RecordHistory(id, oldGen, 0, false, reason, transaction.PrepTime.Elapsed);
            }

            // Retire the old graph in reverse dependency order (C->B->A; retirement order only).
            foreach (var id in committable.AsEnumerable().Reverse())
            {
                var old = transaction.Previous.GetValueOrDefault(id);
                if (old is not null)
                {
                    RetireGeneration(old, releaseSlots: false);
                }
            }

            var gone = new List<string>();
            foreach (var removed in transaction.Removed)
            {
                var old = transaction.Previous.GetValueOrDefault(removed);
                if (old is not null)
                {
                    RetireGeneration(old, releaseSlots: true);
                }

                if (GetOrCreateRecord(removed) is { } record)
                {
                    record.Current = null;
                    record.Active = null;
                }

                gone.Add(removed);
            }

            if (gone.Count > 0)
            {
                _hub.Log("chainloader", LogLevel.Info, $"Removed: {string.Join(", ", gone)}");
            }

            var failed = transaction.CommitOrder
                .Where(id => !committable.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Concat(transaction.Rejected.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ClearTransaction(transaction);
            WriteStatus();
            ObserveReclamation();
            return new ReloadResult(loaded, gone, failed);
        }
    }

    private static List<string> FindTransitiveDependents(IReadOnlyList<ModGeneration> all, string pluginId)
    {
        var dependents = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pluginId };

        // Iterate until fixpoint: any plugin whose dependencies intersect the set joins it.
        bool changed;
        do
        {
            changed = false;
            foreach (var plugin in all)
            {
                if (seen.Contains(plugin.Manifest.Id))
                {
                    continue;
                }

                if (plugin.Manifest.Dependencies.Any(d => seen.Contains(d.Id)))
                {
                    seen.Add(plugin.Manifest.Id);
                    dependents.Add(plugin.Manifest.Id);
                    changed = true;
                }
            }
        } while (changed);

        // Dependents in load order = as they appear in the active list (deps-first).
        return all.Where(p => dependents.Contains(p.Manifest.Id)).Select(p => p.Manifest.Id).ToList();
    }

    // ------------------------------------------------------------------
    // Watcher: FileSystemWatcher + Timer, hardened
    // ------------------------------------------------------------------

    /// <summary>Starts the file watcher so mod edits hot-reload automatically. Safe to call once.</summary>
    public void StartHotReload()
    {
        if (_watcher is not null || !Config.HotReload.Enabled || !Config.HotReload.AutoWatch)
        {
            return;
        }

        Directory.CreateDirectory(_modsDirectory);
        _watcher = new FileSystemWatcher(_modsDirectory, "*.dll")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
        };
        _watcher.Changed += OnModsFileEvent;
        _watcher.Created += OnModsFileEvent;
        _watcher.Deleted += OnModsFileEvent;
        _watcher.Renamed += OnModsFileEvent;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;

        // CLI reload requests (files only, no IPC): nami reload writes reload-requests/<id>.request.
        var requestsDir = Path.Combine(_rootDirectory, "reload-requests");
        Directory.CreateDirectory(requestsDir);
        _requestsWatcher = new FileSystemWatcher(requestsDir, "*.request")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };
        _requestsWatcher.Created += OnRequestsFileEvent;
        _requestsWatcher.Changed += OnRequestsFileEvent;
        _requestsWatcher.Renamed += OnRequestsFileEvent;
        _requestsWatcher.Error += OnWatcherError;
        _requestsWatcher.EnableRaisingEvents = true;

        _debounceTimer = new Timer(_ => StableScan(), null, Timeout.Infinite, Timeout.Infinite);
        _hub.Log("chainloader", LogLevel.Info,
            $"Hot reload watching {_modsDirectory} (debounce {Config.HotReload.DebounceMs} ms)");
    }

    public void StopHotReload()
    {
        _watcher?.Dispose();
        _watcher = null;
        _requestsWatcher?.Dispose();
        _requestsWatcher = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        lock (_gate)
        {
            _pendingFiles.Clear();
            _watcherDirty = false;
            _requestsDirty = false;
        }
    }

    private static bool IsIgnoredWatchName(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) ||
        name.StartsWith(".", StringComparison.Ordinal) ||
        name.StartsWith(".~", StringComparison.Ordinal) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    private void OnModsFileEvent(object sender, FileSystemEventArgs e)
    {
        // Ignore our own temporary/backup writes; debounce so a build (many events) reloads once.
        var name = Path.GetFileName(e.Name ?? string.Empty);
        if (IsIgnoredWatchName(name))
        {
            return;
        }

        var path = e.FullPath;
        lock (_gate)
        {
            _pendingFiles[path] = StatPending(path);
            _debounceTimer?.Change(Config.HotReload.DebounceMs, Timeout.Infinite);
        }
    }

    private void OnRequestsFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _requestsDirty = true;
            _debounceTimer?.Change(Math.Min(250, Math.Max(1, Config.HotReload.DebounceMs)), Timeout.Infinite);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow loses changes: mark dirty and run an authoritative directory
        // reconciliation on the next tick instead of trusting the event stream.
        lock (_gate)
        {
            _watcherDirty = true;
            _debounceTimer?.Change(100, Timeout.Infinite);
        }

        _hub.Log("chainloader", LogLevel.Warn,
            $"File watcher error ({e.GetException().GetBaseException().Message}); reconciling authoritatively.");
    }

    private static PendingFile StatPending(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new PendingFile { Missing = true };
            }

            return new PendingFile { Size = info.Length, MtimeUtc = info.LastWriteTimeUtc };
        }
        catch
        {
            return new PendingFile { Missing = true };
        }
    }

    /// <summary>
    /// Timer-thread scan (ThreadPool): applies the stable-file gate (size+mtime unchanged across
    /// two debounce windows), retries locked files with backoff, and hands stable change sets to
    /// background preparation. Never blocks the tick; commits always land via the command drain.
    /// </summary>
    private void StableScan()
    {
        Dictionary<string, PendingFile> take;
        bool dirty;
        bool requests;
        lock (_gate)
        {
            take = new Dictionary<string, PendingFile>(_pendingFiles, StringComparer.OrdinalIgnoreCase);
            dirty = _watcherDirty;
            _watcherDirty = false;
            requests = _requestsDirty;
            _requestsDirty = false;
        }

        if (requests)
        {
            try
            {
                ConsumeReloadRequests();
            }
            catch (Exception ex)
            {
                _hub.Log("chainloader", LogLevel.Error, $"Reload-request scan failed: {ex.GetBaseException().Message}");
            }
        }

        if (dirty)
        {
            EnqueueCommand(AuthoritativeRescan);
            return;
        }

        if (take.Count == 0)
        {
            return;
        }

        var ready = new List<string>();
        var restable = false;
        foreach (var (path, recorded) in take)
        {
            var current = StatPending(path);
            if (current.Missing || recorded.Missing)
            {
                // Deletion (or a file that never settled): reconcile removals on the next window
                // once the absence itself is stable.
                if (current.Missing && recorded.Missing)
                {
                    ready.Add(path);
                }
                else
                {
                    restable = true;
                    lock (_gate)
                    {
                        _pendingFiles[path] = current;
                    }
                }

                continue;
            }

            if (current.Size == recorded.Size && current.MtimeUtc == recorded.MtimeUtc)
            {
                recorded.StableWindows++;
                if (recorded.StableWindows >= 2 && IsFileReadable(path, recorded))
                {
                    ready.Add(path);
                }
                else
                {
                    restable = true;
                }
            }
            else
            {
                restable = true;
                lock (_gate)
                {
                    _pendingFiles[path] = current;
                }
            }
        }

        lock (_gate)
        {
            foreach (var path in ready)
            {
                _pendingFiles.Remove(path);
            }

            if (restable || _pendingFiles.Count > 0)
            {
                _debounceTimer?.Change(Config.HotReload.DebounceMs, Timeout.Infinite);
            }
        }

        if (ready.Count > 0)
        {
            try
            {
                ScanPathsAndPrepare(ready);
            }
            catch (Exception ex)
            {
                _hub.Log("chainloader", LogLevel.Error, $"Watcher scan failed: {ex.GetBaseException().Message}");
            }
        }
    }

    /// <summary>
    /// Locked-file gate with backoff: a build still holding the DLL is retried, not reloaded
    /// half-written. Returns true when the file can be opened for reading.
    /// </summary>
    private bool IsFileReadable(string path, PendingFile recorded)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            recorded.Retries = 0;
            return true;
        }
        catch (IOException)
        {
            recorded.Retries++;
            _hub.Log("chainloader", LogLevel.Info,
                $"Watcher: '{Path.GetFileName(path)}' is locked (build in progress?); retrying with backoff (attempt {recorded.Retries}).");
            lock (_gate)
            {
                _pendingFiles[path] = recorded;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private void ScanPathsAndPrepare(List<string> stablePaths)
    {
        var manifests = DiscoverPlugins();
        var byPath = manifests.ToDictionary(
            m => Path.GetFullPath(m.AssemblyPath), m => m, StringComparer.OrdinalIgnoreCase);
        var snapshot = Volatile.Read(ref _currentSnapshot);
        var loadedIds = new HashSet<string>(snapshot.Generations.Keys, StringComparer.OrdinalIgnoreCase);
        var manifestIds = new HashSet<string>(manifests.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawMissing = false;
        foreach (var path in stablePaths)
        {
            var full = Path.GetFullPath(path);
            if (byPath.TryGetValue(full, out var manifest))
            {
                if (!loadedIds.Contains(manifest.Id))
                {
                    affected.Add(manifest.Id); // brand-new drop
                }
                else
                {
                    var current = snapshot.Generations[manifest.Id];
                    DateTimeOffset write;
                    try
                    {
                        write = File.GetLastWriteTimeUtc(manifest.AssemblyPath);
                    }
                    catch
                    {
                        continue;
                    }

                    if (write > current.LoadedAtUtc + TimeSpan.FromMilliseconds(100))
                    {
                        affected.Add(manifest.Id);
                    }
                }
            }
            else
            {
                sawMissing = true; // stable absence: a tracked file went away
            }
        }

        if (sawMissing)
        {
            foreach (var id in loadedIds.Where(id => !manifestIds.Contains(id)))
            {
                affected.Add(id);
            }
        }

        QueueAsyncPrepare(affected);
    }

    /// <summary>
    /// Authoritative directory reconciliation after a watcher Error (buffer overflow): diffs fresh
    /// discovery against the live snapshot instead of trusting the lossy event stream.
    /// </summary>
    private void AuthoritativeRescan()
    {
        var manifests = DiscoverPlugins();
        var snapshot = Volatile.Read(ref _currentSnapshot);
        var manifestIds = new HashSet<string>(manifests.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in manifests)
        {
            if (!snapshot.Generations.TryGetValue(manifest.Id, out var current))
            {
                affected.Add(manifest.Id);
                continue;
            }

            try
            {
                if (File.Exists(manifest.AssemblyPath) &&
                    File.GetLastWriteTimeUtc(manifest.AssemblyPath) > current.LoadedAtUtc + TimeSpan.FromMilliseconds(100))
                {
                    affected.Add(manifest.Id);
                }
            }
            catch
            {
                // Stat failure: leave the running generation alone.
            }
        }

        foreach (var id in snapshot.Generations.Keys.Where(id => !manifestIds.Contains(id)))
        {
            affected.Add(id);
        }

        QueueAsyncPrepare(affected);
    }

    /// <summary>
    /// Consumes CLI reload requests (<c>reload-requests/*.request</c>, <c>{modId,sourceDll?}</c>).
    /// Unknown ids are logged and skipped (the CLI validates up front); malformed files are logged
    /// and deleted so a poison file cannot loop forever. Staged <c>--source</c> bytes are already
    /// in place (the CLI copies before writing the request), so preparation just reads from disk.
    /// </summary>
    private void ConsumeReloadRequests()
    {
        var requestsDir = Path.Combine(_rootDirectory, "reload-requests");
        string[] files;
        try
        {
            if (!Directory.Exists(requestsDir))
            {
                return;
            }

            files = Directory.GetFiles(requestsDir, "*.request");
        }
        catch (Exception ex)
        {
            _hub.Log("chainloader", LogLevel.Warn, $"Reload-request scan reported: {ex.GetBaseException().Message}");
            return;
        }

        foreach (var file in files)
        {
            string? modId = null;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("modId"))
                    {
                        modId = property.Value.GetString();
                    }
                }

                if (string.IsNullOrWhiteSpace(modId))
                {
                    throw new InvalidOperationException("missing 'modId'");
                }

                var snapshot = Volatile.Read(ref _currentSnapshot);
                var known = snapshot.Generations.ContainsKey(modId) ||
                    DiscoverPlugins().Any(m => m.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
                if (!known)
                {
                    _hub.Log("chainloader", LogLevel.Warn, $"Reload request for unknown mod '{modId}'; ignoring.");
                }
                else
                {
                    QueueAsyncPrepare(new HashSet<string>(new[] { modId! }, StringComparer.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {

                _hub.Log("chainloader", LogLevel.Warn,
                    $"Ignoring malformed reload request '{Path.GetFileName(file)}': {ex.GetBaseException().Message}");
            }
            finally
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best-effort: a leftover request is re-read (idempotent) on the next scan.
                }
            }
        }
    }

    /// <summary>
    /// Background preparation for watcher-driven change sets: bumps sequences, prepares the graph
    /// on a ThreadPool thread, and enqueues the commit for the tick drain (the safe point).
    /// Convergence holds because every transaction commits something strictly newer, and commit
    /// re-checks sequences (a newer request supersedes this one before publication).
    /// </summary>
    private void QueueAsyncPrepare(HashSet<string> ids)
    {
        if (ids.Count == 0 || _disposed)
        {
            return;
        }

        var pending = ids.ToArray();
        Task.Run(() =>
        {
            ReloadTransaction transaction;
            try
            {
                transaction = PrepareGraph(pending, isReload: true);
            }
            catch (Exception ex)
            {
                _hub.Log("chainloader", LogLevel.Error,
                    $"Background prepare for [{string.Join(", ", pending)}] failed: {ex.GetBaseException().Message}");
                return;
            }

            if (transaction.Prepared.Count == 0 && transaction.Removed.Count == 0 && transaction.Rejected.Count == 0)
            {
                return; // Nothing changed under us.
            }

            EnqueueCommand(() =>
            {
                try
                {
                    CommitGraph(transaction);
                }
                catch (Exception ex)
                {
                    _hub.Log("chainloader", LogLevel.Error, $"Async commit failed: {ex.GetBaseException().Message}");
                }
            });
        });
    }

    private int NextGeneration() => Interlocked.Increment(ref _nextGeneration);

    private void EnqueueCommand(Action command)
    {
        lock (_gate)
        {
            _commands.Enqueue(command);
        }
    }

    private void DrainCommands()
    {
        while (true)
        {
            Action? command;
            lock (_gate)
            {
                if (_commands.Count == 0)
                {
                    return;
                }

                command = _commands.Dequeue();
            }

            try
            {
                command();
            }
            catch (Exception ex)
            {
                _hub.Log("chainloader", LogLevel.Error, $"Reload command failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Disposes a Failed generation's tracked resources at shutdown. Failed boot records never
    /// enter the retirement pipeline, so without this their journal (timers, subscriptions,
    /// disposables tracked before the failure) would leak. Bounded and cooperative with
    /// per-item isolation: failures land in the log, never thrown.
    /// </summary>
    private void DisposeFailedResources(ModGeneration generation)
    {
        try
        {
            var report = generation.Resources.DisposeAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (!report.Completed)
            {
                _hub.Log("chainloader", LogLevel.Warn,
                    $"Shutdown survivors for failed '{generation.Manifest.Id}' generation {generation.Generation}: {string.Join(", ", report.Survivors)}");
            }
        }
        catch (Exception ex)
        {
            _hub.Log("chainloader", LogLevel.Warn,
                $"Shutdown disposal for failed '{generation.Manifest.Id}' reported: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Calls OnUnload on every active plugin (best-effort) at shutdown.</summary>
    public void Shutdown()
    {
        StopHotReload();
        DrainCommands();
        // Retirement order only, mirroring CommitGraph's reverse-dependency retire (C→B→A):
        // dependents drain before the dependencies they may still call into during teardown.
        foreach (var plugin in ShutdownOrder(Volatile.Read(ref _currentSnapshot)))
        {
            if (plugin.Lifetime is LifetimeState.Running or LifetimeState.Failed)
            {
                try
                {
                    if (plugin.Lifetime == LifetimeState.Running)
                    {
                        RetireGeneration(plugin, releaseSlots: true);
                    }
                    else
                    {
                        // Failed boot records never ran the retirement pipeline: OnUnload once,
                        // then their tracked resources (bounded, cooperative, isolated — the same
                        // contract as the normal path; idempotent if prepare already disposed).
                        plugin.RunOnUnloadOnce(msg => _hub.Log("chainloader", LogLevel.Warn, msg));
                        DisposeFailedResources(plugin);
                    }
                }
                catch (Exception ex)
                {
                    _hub.Log("chainloader", LogLevel.Warn,
                        $"Shutdown retire for '{plugin.Manifest.Id}' reported: {ex.GetBaseException().Message}");
                }
            }
        }

        WriteStatus();
    }

    /// <summary>
    /// Reverse-dependency shutdown order over a snapshot: dependents before dependencies, so a
    /// teardown-time call into a dependency never lands on a reclaimed generation. Iteratively
    /// peels ids nothing remaining depends on (the current leaves); a dependency cycle (which
    /// the resolver refuses at load, so this is unreachable) falls back to snapshot order
    /// rather than looping forever. Deterministic: ties break by snapshot enumeration order.
    /// </summary>
    private static List<ModGeneration> ShutdownOrder(RuntimeSnapshot snapshot)
    {
        var order = snapshot.Generations.Keys.ToArray();
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Length; i++)
        {
            position[order[i]] = i;
        }

        var remaining = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModGeneration>(remaining.Count);
        while (remaining.Count > 0)
        {
            var leaves = remaining
                .Where(id => !remaining.Any(other =>
                    !other.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                    snapshot.Dependencies.TryGetValue(other, out var deps) &&
                    deps.Contains(id, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(id => position.GetValueOrDefault(id, int.MaxValue))
                .ToArray();
            if (leaves.Length == 0)
            {
                leaves = remaining.OrderBy(id => position.GetValueOrDefault(id, int.MaxValue)).ToArray();
            }

            foreach (var leaf in leaves)
            {
                remaining.Remove(leaf);
                result.Add(snapshot.Generations[leaf]);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Nami.Sdk.TideMetrics.Unregister(this);
        StopHotReload();
    }
}
