using System.Reflection;
using System.Runtime.InteropServices;
using Nami.Wave.Internal;

namespace Nami.Wave;

/// <summary>
/// Wave — Nami's runtime patching engine.
///
/// Two engines share one detour core (<see cref="Internal.Detour"/>):
///
/// M1 — native-stub dispatch (this file):
///   - x64 inline detours on managed methods (safe prologue relocation, exact restore).
///   - Multiple owners per target; callbacks run in chain (LIFO, newest first).
///   - Shapes: a "gate" (<c>Func&lt;bool&gt;</c> — return true to skip the original) and an
///     "observer" (<c>Action</c> — runs after the original or after a skip).
///   - The original method runs through the detour trampoline with the ORIGINAL arguments
///     intact — no marshaling, no allocation on the hot path.
///   - Scope: parameterless void methods (the IL-copy engine below covers the rest).
///
/// M2 — Harmony-style IL-copy patching (Wave.Patch.cs):
///   - Copies the target's IL into a generated method and injects prefix/postfix calls, so
///     ANY signature is patchable: value returns, arguments, instance methods, ref
///     rewriting, skip semantics. See <see cref="Patch(MethodBase, string, Delegate, Delegate)"/>.
///
/// No Harmony/MonoMod/Cecil anywhere.
/// </summary>
public static unsafe partial class Wave
{
    /// <summary>Thrown when a hook cannot be installed or is outside the supported scope.</summary>
    public sealed class HookException : Exception
    {
        public HookException(string message) : base(message) { }
    }

    private static readonly Lock RegistryLock = new();
    private static readonly Dictionary<MethodBase, HookSite> Sites = new();

    private sealed class HookSite(MethodBase method, IntPtr code)
    {
        public readonly MethodBase Method = method;
        public readonly IntPtr Code = code;
        public Detour? Detour;
        public readonly List<HookEntry> Entries = new();
        public IntPtr Dispatcher;      // native per-site stub
        public nuint StubSize;
        public bool DispatcherReady;
        public GCHandle SelfHandle;    // GC-safe handle to this site, passed through the stub
        public IntPtr Trampoline;      // set once the detour exists
        public IntPtr SkipTrampoline;  // set once the detour exists (zero for leaf prologues)
        public bool FrameSafe;         // true when the prologue is a leaf (gate skip allowed)
    }

    private sealed class HookEntry
    {
        public required string Owner;
        public Func<bool>? Gate;
        public Action? Observer;
    }

    // The managed dispatch entry invoked by every site stub: takes the site's GC handle
    // (as an IntPtr — never a raw object ref across native), runs the chain, returns
    // "skip original?". Kept as a delegate so we can resolve its native address.
    private delegate bool DispatchSiteNative(IntPtr siteHandle);
    private static readonly DispatchSiteNative DispatchEntry = DispatchSite;

    // ------------------------------------------------------------ public API

    /// <summary>Hooks a parameterless void method for <paramref name="owner"/>.</summary>
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
            var site = GetOrCreateSite(target);
            if (site.Entries.Any(e => e.Owner == owner))
            {
                throw new InvalidOperationException($"owner '{owner}' already hooked {target}");
            }

            // Newest first (LIFO).
            site.Entries.Insert(0, new HookEntry { Owner = owner, Gate = gate, Observer = observer });

            if (site.Detour is null)
            {
                var detour = Detour.TryCreate(site.Code);
                if (detour is null)
                {
                    site.Entries.RemoveAt(0);
                    if (site.Entries.Count == 0)
                    {
                        Sites.Remove(target);
                    }

                    throw new HookException($"cannot hook {target}: prologue not relocatable at {site.Code.ToInt64():X}");
                }

                site.Detour = detour;
                site.Trampoline = detour.Trampoline;
                site.SkipTrampoline = detour.SkipTrampoline;
                site.FrameSafe = detour.FrameSafe;

                // The dispatcher stub needs the trampoline addresses; build it, then point
                // the detour at the dispatcher and install.
                var dispatcher = BuildDispatcher(site);
                detour.Retarget(dispatcher);
                detour.Install();
            }
        }
    }

    public static void Unhook(MethodBase target, string owner)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (RegistryLock)
        {
            if (!Sites.TryGetValue(target, out var site))
            {
                return;
            }

            var removed = site.Entries.RemoveAll(e => e.Owner == owner);
            if (removed == 0)
            {
                return;
            }

            if (site.Entries.Count == 0)
            {
                site.Detour?.Uninstall();
                site.Detour?.Dispose();
                site.Detour = null;
                if (site.Dispatcher != IntPtr.Zero)
                {
                    RawMemory.FreeExecutable((byte*)site.Dispatcher, site.StubSize);
                }

                if (site.SelfHandle.IsAllocated)
                {
                    site.SelfHandle.Free();
                }

                Sites.Remove(target);
            }
        }
    }

    public static void UnhookAll(string owner)
    {
        List<MethodBase> keys;
        lock (RegistryLock)
        {
            keys = Sites.Where(kv => kv.Value.Entries.Any(e => e.Owner == owner))
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
            return Sites.TryGetValue(target, out var site) && site.Entries.Any(e => e.Owner == owner);
        }
    }

    public static void UnhookEverything()
    {
        List<MethodBase> keys;
        lock (RegistryLock)
        {
            keys = Sites.Keys.ToList();
        }

        foreach (var k in keys)
        {
            // Remove every hook on this target, whatever the owner.
            lock (RegistryLock)
            {
                if (!Sites.TryGetValue(k, out var site))
                {
                    continue;
                }

                site.Entries.Clear();
                site.Detour?.Uninstall();
                site.Detour?.Dispose();
                site.Detour = null;
                if (site.Dispatcher != IntPtr.Zero)
                {
                    RawMemory.FreeExecutable((byte*)site.Dispatcher, site.StubSize);
                }

                if (site.SelfHandle.IsAllocated)
                {
                    site.SelfHandle.Free();
                }

                Sites.Remove(k);
            }
        }
    }

    // ------------------------------------------------------------ internals

    private static HookSite GetOrCreateSite(MethodBase target)
    {
        if (Sites.TryGetValue(target, out var existing))
        {
            return existing;
        }

        // A method can be M1-hooked or M2-patched, never both (each installs its own detour
        // on the same prologue). M1 covers parameterless void; M2 covers everything else.
        if (M2Sites.ContainsKey(target))
        {
            throw new HookException($"cannot hook {target}: an M2 patch is already installed on it");
        }

        // Scope check (M1): parameterless void only.
        if (target is MethodInfo mi && mi.ReturnType != typeof(void))
        {
            throw new HookException($"Wave M1 supports void-returning methods only; {target} returns {mi.ReturnType}");
        }

        if (target.GetParameters().Length != 0)
        {
            throw new HookException($"Wave M1 supports parameterless methods only; {target} has parameters");
        }

        var addr = NativeInterop.GetCodeAddress(target);
        if (addr == IntPtr.Zero)
        {
            throw new HookException($"cannot hook {target}: no native code address (open generic?)");
        }

        var site = new HookSite(target, addr);
        Sites[target] = site;
        return site;
    }

    /// <summary>Builds the per-site native stub (once).</summary>
    private static IntPtr BuildDispatcher(HookSite site)
    {
        if (site.DispatcherReady)
        {
            return site.Dispatcher;
        }

        site.SelfHandle = GCHandle.Alloc(site, GCHandleType.Normal);

        // Managed dispatch entry address.
        var dispatchPtr = (byte*)NativeInterop.GetCodeAddress(DispatchEntry.Method);
        if (dispatchPtr == null)
        {
            throw new HookException("cannot resolve Wave dispatch entry");
        }

        // Stub layout: preserve arg regs, call dispatch, branch on skip.
        var stub = (byte*)RawMemory.AllocExecutable(160);
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
        //    its original arguments on the caller's stack — exceptions unwind naturally).
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
        site.Dispatcher = (IntPtr)stub;
        site.StubSize = (nuint)o;
        site.DispatcherReady = true;
        return site.Dispatcher;
    }

    /// <summary>Managed dispatch: runs the chain; returns true if the original must be skipped.</summary>
    private static bool DispatchSite(IntPtr siteHandle)
    {
        if (siteHandle == IntPtr.Zero)
        {
            return false;
        }

        var site = (HookSite)GCHandle.FromIntPtr(siteHandle).Target!;
        bool skip = false;
        // Chain, LIFO (entries[0] is the most recent hook).
        foreach (var entry in site.Entries)
        {
            try
            {
                if (entry.Gate is not null && entry.Gate())
                {
                    skip = true;
                }

                entry.Observer?.Invoke();
            }
            catch
            {
                // A throwing user callback must not corrupt the dispatch; the exception is
                // swallowed here because we are mid-native-stub on the hook path. The game
                // must not die because a mod callback threw. (Observers run best-effort.)
            }
        }

        return skip;
    }
}
