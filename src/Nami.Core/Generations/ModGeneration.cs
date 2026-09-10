using Nami.Core.Plugins;
using Nami.Core.Profiling;
using Nami.Sdk;

namespace Nami.Core.Generations;

/// <summary>A plugin generation that has been instantiated and attached to its context.</summary>
public sealed class ModGeneration
{
    internal ModGeneration(PluginManifest manifest, NamiPlugin instance, PluginContext context,
        PluginLoadContext loadContext, int generation, ModProfiler profiler)
    {
        Manifest = manifest;
        Instance = instance;
        Context = context;
        LoadContext = loadContext;
        Generation = generation;
        Profiler = profiler;
    }

    public PluginManifest Manifest { get; }
    public NamiPlugin Instance { get; }
    public PluginContext Context { get; }

    private volatile LifetimeState _lifetime = LifetimeState.Discovered;

    /// <summary>
    /// First-class lifetime of this generation. Volatile-backed: the tick drain, background
    /// prepares, and status reads observe it cross-thread, so every read sees the latest
    /// transition (writes still flow through the validated edge check at the call site).
    /// </summary>
    public LifetimeState Lifetime
    {
        get => _lifetime;
        internal set => _lifetime = value;
    }

    /// <summary>Owned resources for this generation.</summary>
    public GenerationResources Resources { get; internal set; } = null!;

    /// <summary>Wave hook bindings owned by this generation.</summary>
    public List<WaveBinding> HookBindings { get; } = new();

    public int ConsecutiveFailures { get; internal set; }
    public DateTimeOffset? QuarantinedAt { get; internal set; }
    public string? LastError { get; internal set; }

    /// <summary>How many times this plugin id has been loaded (1 = first load).</summary>
    public int Generation { get; internal set; }

    /// <summary>UTC timestamp of when this generation was loaded; hot reload compares mod file writes against it.</summary>
    public DateTimeOffset LoadedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Number of successful hot reloads of this plugin id.</summary>
    public int ReloadCount { get; internal set; }

    /// <summary>The unloadable load context owning this generation's assemblies.</summary>
    public PluginLoadContext LoadContext { get; }

    /// <summary>Built-in profiler metrics for this generation.</summary>
    public ModProfiler Profiler { get; }

    // ------------------------------------------------------------ lease / retire gate
    //
    // Single linearizable atomic gate shared by acquisition and retirement: the packed state
    // holds the retired bit (bit 31) plus the active ordinary-lease count (low 31 bits).
    // EnterExecution and RetireGate both CAS-loop on the same word, so acquisition either
    // linearizes before retirement (lease granted, drain will observe it) or fails with
    // GenerationRetiredException — never a separately observed flag followed by an increment.
    //
    // Count-width overflow analysis: the low 31 bits admit ~2.1B concurrent leases. One lease
    // is held per in-flight host→mod call on a host thread; exhausting the range would require
    // two billion simultaneously live entries, which the host thread budget makes impossible.
    // The increment path additionally guards the saturated value explicitly, so even a bug that
    // leaks leases fails loudly instead of flipping the retired bit. Retry on CAS mismatch is
    // lock-free and terminates: only a successful retire (which ends acquisition attempts) or a
    // successful acquire changes the word.
    private int _gate;
    private const int RetiredBit = unchecked((int)0x80000000);
    private const int CountMask = int.MaxValue;

    /// <summary>True once retirement has claimed this generation; new ordinary execution is rejected.</summary>
    internal bool IsRetired => (Volatile.Read(ref _gate) & RetiredBit) != 0;

    /// <summary>Outstanding ordinary-execution leases; the quiescence drain waits for zero.</summary>
    internal int ActiveExecutions => Volatile.Read(ref _gate) & CountMask;

    /// <summary>
    /// Enters ordinary execution on this generation. Rejects retired generations with
    /// <see cref="GenerationRetiredException"/>. Never admits teardown: teardown runs under
    /// <see cref="EnterTeardown"/>, not through this gate.
    /// </summary>
    internal ExecutionLease EnterExecution()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _gate);
            if ((observed & RetiredBit) != 0)
            {
                throw new GenerationRetiredException(Manifest.Id, Generation);
            }

            if ((observed & CountMask) == CountMask)
            {
                throw new InvalidOperationException(
                    $"Mod '{Manifest.Id}' generation {Generation} exhausted its execution-lease count; refusing entry.");
            }

            if (Interlocked.CompareExchange(ref _gate, observed + 1, observed) == observed)
            {
                return new ExecutionLease(this);
            }
        }
    }

    /// <summary>Releases one lease acquired via <see cref="EnterExecution"/>.</summary>
    internal void ReleaseExecution()
    {
        // The releaser always holds a live lease, so the count is nonzero and subtracting one
        // cannot borrow into the retired bit: the bit survives the decrement untouched.
        Interlocked.Decrement(ref _gate);
    }

    /// <summary>
    /// Claims this generation for retirement: sets the retired bit once (idempotent) and returns
    /// the lease count outstanding at the claim, which the quiescence drain must observe reaching zero.
    /// </summary>
    internal int RetireGate()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _gate);
            if ((observed & RetiredBit) != 0)
            {
                return observed & CountMask;
            }

            if (Interlocked.CompareExchange(ref _gate, observed | RetiredBit, observed) == observed)
            {
                return observed & CountMask;
            }
        }
    }

    /// <summary>
    /// Enters the teardown domain for this generation. Enforced by the gate, not by convention:
    /// throws until the generation has reached <see cref="LifetimeState.Quiescing"/> (or the
    /// re-driven <see cref="LifetimeState.RetirementBlocked"/>) with zero ordinary leases held, so
    /// <c>OnUnload</c> can never race a leased ordinary callback and can never begin while one
    /// is active. Scoped to leased entries: direct (non-slot) Wave registrations are not
    /// lease-gated and keep teardown-at-retire semantics (brief unhooked window, documented on
    /// the hook surface) — that carve-out is intentional, not covered by this gate.
    /// </summary>
    internal void EnterTeardown()
    {
        if (Lifetime is not LifetimeState.Quiescing and not LifetimeState.RetirementBlocked)
        {
            throw new InvalidOperationException(
                $"EnterTeardown for '{Manifest.Id}' generation {Generation} requires Quiescing (was {Lifetime}); " +
                "teardown is its own domain and never admits ordinary execution.");
        }

        var active = ActiveExecutions;
        if (active != 0)
        {
            throw new InvalidOperationException(
                $"EnterTeardown for '{Manifest.Id}' generation {Generation} refused: {active} ordinary lease(s) still held; " +
                "OnUnload must not begin while an ordinary callback is active.");
        }
    }

    private int _onUnloadRan;

    /// <summary>
    /// Runs <c>OnUnload</c> exactly once for this generation (first caller wins; later calls are
    /// no-ops). Exceptions are logged, never thrown: teardown is best-effort past this point and
    /// must always reach resource disposal and ALC release.
    /// Deliberately unbounded (no timeout): abandoning a wedged <c>OnUnload</c> mid-call would
    /// race the cleanup that follows it — the exact race this teardown order was built to close.
    /// Known limitation: one wedged <c>OnUnload</c> stalls the tick drain behind it; that is a
    /// liveness stall only (no invariant is at risk — ordinary leases already drained and the
    /// published snapshot stands).
    /// </summary>
    internal void RunOnUnloadOnce(Action<string> logWarn)
    {
        if (Interlocked.Exchange(ref _onUnloadRan, 1) == 1)
        {
            return;
        }

        try
        {
            Instance.OnUnload();
        }
        catch (Exception ex)
        {
            logWarn($"OnUnload threw for '{Manifest.Id}' generation {Generation}: {ex.GetBaseException().Message}");
        }
    }

    internal void Update(Chainloader chainloader)
    {
        // Ordinary execution under the retire gate: a retire racing this tick either lets this
        // call through (and the drain waits for it) or rejects it before entry — never reclaims under it.
        using var lease = EnterExecution();
        try
        {
            Instance.OnUpdate();
            ConsecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            ConsecutiveFailures++;
            LastError = ex.Message;
            Context.Log.Error($"OnUpdate threw ({ConsecutiveFailures} consecutive): {ex}");
            if (chainloader.Config.QuarantineEnabled &&
                ConsecutiveFailures >= chainloader.Config.QuarantineThreshold)
            {
                chainloader.Quarantine(this, ex);
            }
        }
    }
}
