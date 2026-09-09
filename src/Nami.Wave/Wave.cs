using System.Reflection;
using System.Runtime.InteropServices;
using Nami.Wave.Internal;

namespace Nami.Wave;

/// <summary>
/// Wave - Nami's runtime patching engine.
///
/// One chain per target method, two serving strategies sharing one detour core
/// (<see cref="Internal.Detour"/>):
///
/// Fast - native stub (blueprints A/B/D, see <see cref="WaveFast"/>): prefix-only
///   hooks run in one managed dispatch, then the original runs via tail-jump (or a
///   type-default returns on skip). No IL copy, no extra assembly, the original code
///   keeps its JIT state. Serves prefix-only hooks on GC-tracking-free shapes.
/// ILCopy - Harmony-style IL-copy patching (Wave.Patch.cs): the target's IL is copied
///   into a generated method with prefix/postfix calls injected, so ANY signature and
///   any hook shape (postfix, ref result, transpilers, instance) works.
///
/// <see cref="Patch"/> picks the cheapest strategy that satisfies what was requested
/// and upgrades/downgrades the site transparently as owners come and go.
/// <see cref="Hook"/> is the legacy entry point: parameterless void targets only, kept
/// bit-identical (LIFO chain order) and deprecated in favor of Patch.
///
/// No Harmony/MonoMod/Cecil anywhere.
/// </summary>
public static unsafe partial class Wave
{
    /// <summary>Thrown when a hook cannot be installed or is outside the supported scope.</summary>
    public sealed class HookException : Exception
    {
        public HookException(string message) : base(message) { }
        public HookException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Which entry point registered a chain entry (drives default ordering).</summary>
    internal enum ApiSource : byte { Hook, Patch }

    /// <summary>One prefix-chain item on the fast path: hook + prebuilt invoker.</summary>
    internal sealed class FastPreItem
    {
        public required object Hook;
        public required WaveFast.FastPreInvoker Invoker;
        /// <summary>True when the invoker never reads the spill block.</summary>
        public required bool Blind;
    }

    internal sealed class WaveChainEntry
    {
        public required string Owner;
        public required ApiSource Source;
        public required int Seq;
        public Func<bool>? Gate;          // Hook-source
        public Action? Observer;          // Hook-source
        public Delegate? Prefix;          // Patch-source
        public Delegate? Postfix;         // Patch-source (forces ILCopy)
        public bool PrefixBlind;          // Prefix observes no arguments
        public WaveFast.FastPreInvoker? GateInvoker;
        public WaveFast.FastPreInvoker? ObserverInvoker;
        public WaveFast.FastPreInvoker? PrefixInvoker; // null when not fast-bindable
        public List<WaveTranspiler> Transpilers { get; } = new(); // forces ILCopy

        /// <summary>True when this entry can be served by the fast stub.</summary>
        public bool FastOk =>
            Transpilers.Count == 0 && Postfix is null &&
            (Source == ApiSource.Hook || Prefix is null || PrefixInvoker is not null);
    }

    internal sealed class WaveSite
    {
        public required MethodBase Method;
        public required IntPtr Code;
        public required WaveFast.FastShape? Shape; // null = never fast-eligible
        public Detour? Detour;
        public readonly List<WaveChainEntry> Entries = new();
        public int NextSeq;
        public bool IsFast; // current strategy
        public FastPreItem[] Chain = [];
        public IntPtr Stub;      // fast stub (A/B/D), built once
        public nuint StubSize;
        public bool StubBuilt;
        public GCHandle SelfHandle;
        public IlBody? Body;     // analyzed lazily for ILCopy builds
        public MethodInfo? Patched;
        public IntPtr PatchedEntry;
        // A-blueprint fields (today's M1 stub shape, kept byte-identical).
        public IntPtr Trampoline;
        public IntPtr SkipTrampoline;
        public bool FrameSafe;
        public bool DispatcherReady;
    }

    private static readonly Lock RegistryLock = new();
    private static readonly Dictionary<MethodBase, WaveSite> UnifiedSites = new();

    // The managed dispatch entry invoked by every fast stub: takes the site's GC handle
    // (as an IntPtr - never a raw object ref across native) plus the spill block (null
    // for blueprint A, whose items are blind by construction), runs the sorted prefix
    // chain, returns "skip original?". Resolved once for its native address.
    private delegate int FastPreNative(IntPtr siteHandle, IntPtr block);
    private static readonly FastPreNative FastPreEntry = FastPre;
    private static IntPtr s_fastPrePtr;
    private static bool s_fastPreReady;

    // ------------------------------------------------------------ public API

    /// <summary>
    /// Hooks a parameterless void method for <paramref name="owner"/>.
    /// Legacy entry point (deprecated in favor of <see cref="Patch"/>): scope and LIFO
    /// chain order are frozen. Shares the unified chain, so Hook and Patch entries on
    /// the same method compose instead of refusing each other.
    /// </summary>
    public static void Hook(MethodBase target, string owner, Func<bool>? gate = null, Action? observer = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        if (gate is null && observer is null)
        {
            throw new ArgumentException("provide at least a gate or an observer");
        }

        lock (RegistryLock)
        {
            // Frozen scope: parameterless void only (anything else is Patch's job).
            if (target is MethodInfo mi && mi.ReturnType != typeof(void))
            {
                throw new HookException($"Wave M1 supports void-returning methods only; {target} returns {mi.ReturnType}");
            }

            if (target.GetParameters().Length != 0)
            {
                throw new HookException($"Wave M1 supports parameterless methods only; {target} has parameters");
            }

            var site = GetOrCreateUnifiedSite(target);
            if (site.Entries.Any(e => e.Owner == owner && e.Source == ApiSource.Hook))
            {
                throw new InvalidOperationException($"owner '{owner}' already hooked {target}");
            }

            var entry = new WaveChainEntry { Owner = owner, Source = ApiSource.Hook, Seq = site.NextSeq++ };
            if (gate is not null)
            {
                entry.Gate = gate;
                entry.GateInvoker = MakeBlindInvoker(gate, site);
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(gate);
                WaveFast.WarmInvoker(entry.GateInvoker, gate);
            }
            if (observer is not null)
            {
                entry.Observer = observer;
                entry.ObserverInvoker = MakeBlindInvoker(observer, site);
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(observer);
                WaveFast.WarmInvoker(entry.ObserverInvoker, observer);
            }
            site.Entries.Add(entry);
            try
            {
                RebuildSite(site);
            }
            catch
            {
                RollbackSite(site, entry);
                throw;
            }
        }
    }

    /// <summary>Builds a fast invoker for a parameterless hook (gate or observer).</summary>
    private static WaveFast.FastPreInvoker MakeBlindInvoker(Delegate hook, WaveSite site)
    {
        // Hook scope guarantees the A shape (void(), no params): the binding is empty.
        var shape = site.Shape ?? throw new HookException($"cannot hook {site.Method}: unsupported shape");
        bool vote = hook is Func<bool>;
        return WaveFast.MakeInvoker(hook, shape,
            new WaveFast.FastHookBinding { Slots = [], ReturnsVote = vote, VoteIfTrue = vote });
    }

    public static void Unhook(MethodBase target, string owner)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (RegistryLock)
        {
            if (!UnifiedSites.TryGetValue(target, out var site))
            {
                return;
            }

            if (site.Entries.RemoveAll(e => e.Owner == owner) == 0)
            {
                return;
            }

            if (site.Entries.Count == 0)
            {
                TeardownSite(site);
            }
            else
            {
                RebuildSite(site);
            }
        }
    }

    public static void UnhookAll(string owner)
    {
        List<MethodBase> keys;
        lock (RegistryLock)
        {
            keys = UnifiedSites.Where(kv => kv.Value.Entries.Any(e => e.Owner == owner))
                .Select(kv => kv.Key).ToList();
        }

        foreach (var k in keys)
        {
            Unhook(k, owner);
        }
    }

    public static bool IsHooked(MethodBase target, string owner)
    {
        lock (RegistryLock)
        {
            return UnifiedSites.TryGetValue(target, out var site) && site.Entries.Any(e => e.Owner == owner);
        }
    }

    public static void UnhookEverything()
    {
        List<WaveSite> sites;
        lock (RegistryLock)
        {
            sites = UnifiedSites.Values.ToList();
        }

        foreach (var site in sites)
        {
            lock (RegistryLock)
            {
                if (!UnifiedSites.ContainsValue(site))
                {
                    continue;
                }
                TeardownSite(site);
            }
        }
    }

    // ------------------------------------------------------------ unified sites

    private static WaveSite GetOrCreateUnifiedSite(MethodBase target, string verb = "hook")
    {
        if (UnifiedSites.TryGetValue(target, out var existing))
        {
            return existing;
        }

        var addr = NativeInterop.GetCodeAddress(target);
        if (addr == IntPtr.Zero)
        {
            ThrowForOpenGeneric(target, verb);
        }

        var site = new WaveSite
        {
            Method = target,
            Code = addr,
            Shape = WaveFast.AnalyzeShape(target),
        };
        UnifiedSites[target] = site;
        return site;
    }

    /// <summary>
    /// Prefix-chain order: Hook entries sort by decreasing sequence (LIFO, bit-identical
    /// to the legacy M1 chain); Patch entries sort oldest-first (M2 order). Hook's
    /// negative priorities always sort before Patch's zero, so mixed chains are
    /// deterministic too.
    /// </summary>
    private static List<FastPreItem> BuildPreChain(WaveSite site)
    {
        var items = new List<(int Priority, int Seq, int Bias, FastPreItem Item)>();
        foreach (var e in site.Entries)
        {
            int priority = e.Source == ApiSource.Hook ? -(e.Seq + 1) : 0;
            if (e.GateInvoker is not null)
            {
                items.Add((priority, e.Seq, 0, new FastPreItem { Hook = e.Gate!, Invoker = e.GateInvoker, Blind = true }));
            }
            if (e.ObserverInvoker is not null)
            {
                items.Add((priority, e.Seq, 1, new FastPreItem { Hook = e.Observer!, Invoker = e.ObserverInvoker, Blind = true }));
            }
            if (e.PrefixInvoker is not null)
            {
                items.Add((priority, e.Seq, 0, new FastPreItem { Hook = e.Prefix!, Invoker = e.PrefixInvoker, Blind = e.PrefixBlind }));
            }
        }
        // Stable sort: same-key items keep materialization order (gate before observer).
        return items.OrderBy(t => t.Priority).ThenBy(t => t.Seq).ThenBy(t => t.Bias)
            .Select(t => t.Item).ToList();
    }

    private static void RebuildSite(WaveSite site)
    {
        lock (RegistryLock)
        {
            site.Detour ??= Detour.TryCreate(site.Code)
                ?? throw new HookException($"cannot hook {site.Method}: prologue not relocatable at {site.Code.ToInt64():X}");
            site.Trampoline = site.Detour.Trampoline;
            site.SkipTrampoline = site.Detour.SkipTrampoline;
            site.FrameSafe = site.Detour.FrameSafe;

            bool fastOk = site.Shape is not null
                && site.Detour.TrampolinesSound
                && site.Entries.All(e => e.FastOk);
            if (fastOk)
            {
                site.Chain = BuildPreChain(site).ToArray();
                EnsureFastStub(site);
                site.IsFast = true;
                site.Detour.Uninstall();
                site.Detour.Retarget(site.Stub);
                site.Detour.Install();
            }
            else
            {
                RebuildIlCopy(site);
                site.IsFast = false;
            }
        }
    }

    /// <summary>Rolls back a failed rebuild so a site never claims a patch its body doesn't carry.</summary>
    private static void RollbackSite(WaveSite site, WaveChainEntry entry)
    {
        site.Entries.Remove(entry);
        if (site.Entries.Count == 0)
        {
            TeardownSite(site);
        }
    }

    private static void TeardownSite(WaveSite site)
    {
        lock (RegistryLock)
        {
            site.Detour?.Uninstall();
            site.Detour?.Dispose();
            site.Detour = null;
            if (site.Stub != IntPtr.Zero)
            {
                RawMemory.FreeExecutable((byte*)site.Stub, site.StubSize);
                site.Stub = IntPtr.Zero;
                site.StubBuilt = false;
            }
            site.DispatcherReady = false;

            if (site.SelfHandle.IsAllocated)
            {
                site.SelfHandle.Free();
            }

            UnifiedSites.Remove(site.Method);
        }
    }

    private static void EnsureFastStub(WaveSite site)
    {
        if (site.StubBuilt)
        {
            return;
        }
        if (!site.SelfHandle.IsAllocated)
        {
            site.SelfHandle = GCHandle.Alloc(site, GCHandleType.Normal);
        }
        if (site.Shape!.Blueprint == 'A' || UseBlueprintA(site))
        {
            site.Stub = BuildDispatcher(site);
        }
        else
        {
            EnsureFastPreAddress();
            // Near-jump sites can only reach ±2GB: the stub must be allocated near.
            var near = site.Detour!.IsNearJump ? site.Code : IntPtr.Zero;
            var (stub, size) = WaveFast.BuildStub(
                site.Shape, GCHandle.ToIntPtr(site.SelfHandle), s_fastPrePtr, site.Detour!.Trampoline, near);
            site.Stub = stub;
            site.StubSize = size;
        }
        site.StubBuilt = true;
    }

    /// <summary>
    /// True when the legacy A-form stub can serve the site: every chain item is
    /// slotless (blind hooks, so no spill block is needed), every param is int-kind
    /// (the managed dispatch clobbers xmm0-3), and the return is void (A's skip path
    /// runs the relocated prologue and returns whatever it leaves - only sound when
    /// nothing is returned). Anything else takes the B/D stub with typed skip defaults.
    /// </summary>
    private static bool UseBlueprintA(WaveSite site)
    {
        if (site.Shape!.Return != WaveFast.ReturnKind.Void)
        {
            return false;
        }
        if (site.Shape!.ParamKinds.Any(k => k != WaveFast.SlotKind.Int))
        {
            return false;
        }
        return site.Chain.All(i => i.Blind);
    }

    private static void EnsureFastPreAddress()
    {
        if (s_fastPreReady)
        {
            return;
        }
        var ptr = (byte*)NativeInterop.GetCodeAddress(FastPreEntry.Method);
        if (ptr == null)
        {
            throw new HookException("cannot resolve Wave fast dispatch entry");
        }
        s_fastPrePtr = (IntPtr)ptr;
        s_fastPreReady = true;
    }

    /// <summary>Builds the per-site native stub (once).</summary>
    private static IntPtr BuildDispatcher(WaveSite site)
    {
        if (site.DispatcherReady)
        {
            return site.Stub;
        }

        if (!site.SelfHandle.IsAllocated)
        {
            site.SelfHandle = GCHandle.Alloc(site, GCHandleType.Normal);
        }

        // Managed dispatch entry address.
        EnsureFastPreAddress();
        var dispatchPtr = (byte*)s_fastPrePtr;

        // Stub layout: preserve arg regs, call dispatch, branch on skip.
        // Near-jump sites can only reach ±2GB (same rule as the B/D emitter).
        void* mem = site.Detour!.IsNearJump
            ? RawMemory.TryAllocExecutableNear((void*)site.Code, 160)
            : RawMemory.AllocExecutable(160);
        if (mem == null)
        {
            throw new HookException(
                $"cannot allocate dispatch stub within reach of {site.Code.ToInt64():X} (near-jump site)");
        }
        var stub = (byte*)mem;
        int o = 0;

        // push rcx; push rdx; push r8; push r9  (preserve original argument registers)
        stub[o++] = 0x51;
        stub[o++] = 0x52;
        stub[o++] = 0x41; stub[o++] = 0x50;
        stub[o++] = 0x41; stub[o++] = 0x51;
        // sub rsp, 40  (shadow space for the call)
        stub[o++] = 0x48; stub[o++] = 0x83; stub[o++] = 0xEC; stub[o++] = 40;
        // mov rcx, siteHandle (GCHandle.ToIntPtr)
        stub[o++] = 0x48; stub[o++] = 0xB9;
        *(ulong*)(stub + o) = (ulong)GCHandle.ToIntPtr(site.SelfHandle); o += 8;
        // mov rax, dispatchPtr
        stub[o++] = 0x48; stub[o++] = 0xB8;
        *(ulong*)(stub + o) = (ulong)dispatchPtr; o += 8;
        // call rax
        stub[o++] = 0xFF; stub[o++] = 0xD0;
        // test al, al
        stub[o++] = 0x84; stub[o++] = 0xC0;
        // jz runOriginal  (displacement patched below)
        int jzOff = o;
        stub[o++] = 0x74; stub[o++] = 0x00;

        // -- skipOriginal: restore the original argument registers, then either return
        //    directly (leaf prologue) or jump into the skip trampoline which runs the
        //    relocated prologue (setting up the frame) and then unwinds it before returning.
        // add rsp, 40
        stub[o++] = 0x48; stub[o++] = 0x83; stub[o++] = 0xC4; stub[o++] = 40;
        // pop r9; pop r8; pop rdx; pop rcx
        stub[o++] = 0x41; stub[o++] = 0x59;
        stub[o++] = 0x41; stub[o++] = 0x58;
        stub[o++] = 0x5A;
        stub[o++] = 0x59;
        if (site.SkipTrampoline != IntPtr.Zero)
        {
            // mov rax, skipTrampoline; jmp rax
            stub[o++] = 0x48; stub[o++] = 0xB8;
            *(ulong*)(stub + o) = (ulong)site.SkipTrampoline; o += 8;
            stub[o++] = 0xFF; stub[o++] = 0xE0;
        }
        else
        {
            stub[o++] = 0xC3; // ret
        }

        // -- runOriginal: restore registers, tail-jump to the trampoline (original runs with
        //    its original arguments on the caller's stack - exceptions unwind naturally).
        int runOff = o;
        // add rsp, 40
        stub[o++] = 0x48; stub[o++] = 0x83; stub[o++] = 0xC4; stub[o++] = 40;
        // pop r9; pop r8; pop rdx; pop rcx
        stub[o++] = 0x41; stub[o++] = 0x59;
        stub[o++] = 0x41; stub[o++] = 0x58;
        stub[o++] = 0x5A;
        stub[o++] = 0x59;
        // mov rax, trampoline
        stub[o++] = 0x48; stub[o++] = 0xB8;
        *(ulong*)(stub + o) = (ulong)site.Trampoline; o += 8;
        // jmp rax
        stub[o++] = 0xFF; stub[o++] = 0xE0;

        // Patch jz displacement: from after jz (jzOff+2) to runOff.
        int jzDisp = runOff - (jzOff + 2);
        stub[jzOff + 1] = (byte)jzDisp;

        RawMemory.FlushCode(stub, (nuint)o);
        RawMemory.MakeExecutable(stub, (nuint)o);
        site.Stub = (IntPtr)stub;
        site.StubSize = (nuint)o;
        site.DispatcherReady = true;
        return site.Stub;
    }

    /// <summary>
    /// Managed dispatch for every fast stub: runs the sorted prefix chain; returns 1 if
    /// the original must be skipped. A throwing callback is swallowed (best-effort): we
    /// are mid-native-stub on the hook path, and an exception crossing that boundary
    /// would corrupt the process. The game must not die because a mod callback threw.
    ///
    /// Hot-path contract: everything reachable from here must already be JIT-compiled
    /// (hooks and invokers are pre-JITted with PrepareDelegate at registration) and must
    /// not allocate on first call - compilation or GC stack activity while a GC-info-less
    /// stub frame is live is fatal. Keep this body allocation-free.
    /// </summary>
    private static int FastPre(IntPtr siteHandle, IntPtr block)
    {
        if (siteHandle == IntPtr.Zero)
        {
            return 0;
        }

        var site = (WaveSite)GCHandle.FromIntPtr(siteHandle).Target!;
        int skip = 0;
        var chain = site.Chain;
        for (int i = 0; i < chain.Length; i++)
        {
            try
            {
                skip |= chain[i].Invoker(chain[i].Hook, block);
            }
            catch
            {
            }
        }
        return skip;
    }
}
