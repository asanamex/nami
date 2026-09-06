using System.Reflection;
using Nami.Wave.Internal;

namespace Nami.Wave;

/// <summary>
/// M2 — Harmony-style method patching on top of the M1 detour core.
///
/// Rather than calling a trampoline "as a function" (which is unsafe on CoreCLR x64 for
/// managed-convention bodies — see docs/wave.md), Wave copies the target's IL into a
/// generated method in a dynamic assembly and detours the target to that copy:
///
///   - the patched body = the target's own IL with prefix/postfix calls injected, which the
///     detour jumps to. All calls on the hot path are ordinary managed calls — no unmanaged
///     stubs, no marshaling, no ABI risk. Recursion into the detour is impossible because
///     the patched body INLINES the original instructions rather than calling the target.
///
/// Prefix/postfix conventions (Harmony-compatible, resolved by parameter name):
///   - a parameter whose name matches a target parameter receives that argument (by value)
///   - <c>__instance</c> — the receiver (instance targets)
///   - <c>__result</c> — the return value; declare <c>ref</c> to rewrite it (postfix only)
///   - <c>__state</c> — <c>out object</c> on prefix, <c>ref object</c> on postfix: threads
///     per-call state between the two
///   - <c>__args</c> — <c>object[]</c> of all arguments (including <c>this</c>)
/// Prefixes may return <c>void</c> or <c>bool</c>; returning <c>false</c> skips the original
/// body. Postfixes must return void and always run (also when the original was skipped).
/// </summary>
public static unsafe partial class Wave
{
    // ---------------------------------------------------------------- M2 API

    /// <summary>Adds a prefix/postfix pair to <paramref name="target"/> for <paramref name="owner"/>.</summary>
    public static void Patch(MethodBase target, string owner, Delegate? prefix = null, Delegate? postfix = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        if (prefix is null && postfix is null)
        {
            throw new ArgumentException("provide at least a prefix or a postfix");
        }

        lock (RegistryLock)
        {
            var site = GetOrCreateM2Site(target);
            if (site.Entries.Any(e => e.Owner == owner))
            {
                throw new InvalidOperationException($"owner '{owner}' already hooked {target}");
            }

            site.Entries.Add(new PatchEntry
            {
                Owner = owner,
                Prefix = prefix,
                Postfix = postfix,
            });
            RebuildAndApply(site);
        }
    }

    /// <summary>Removes an owner's patch from <paramref name="target"/>.</summary>
    public static void Unpatch(MethodBase target, string owner)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);

        lock (RegistryLock)
        {
            if (!M2Sites.TryGetValue(target, out var site))
            {
                return;
            }

            if (site.Entries.RemoveAll(e => e.Owner == owner) == 0)
            {
                return;
            }

            if (site.Entries.Count == 0)
            {
                Teardown(site);
                M2Sites.Remove(target);
            }
            else
            {
                RebuildAndApply(site);
            }
        }
    }

    /// <summary>True when <paramref name="owner"/> has a patch on <paramref name="target"/>.</summary>
    public static bool IsPatched(MethodBase target, string owner)
    {
        lock (RegistryLock)
        {
            return M2Sites.TryGetValue(target, out var site) && site.Entries.Any(e => e.Owner == owner);
        }
    }

    /// <summary>Removes every M2 patch owned by <paramref name="owner"/>.</summary>
    public static void UnpatchAll(string owner)
    {
        List<MethodBase> keys;
        lock (RegistryLock)
        {
            keys = M2Sites.Where(kv => kv.Value.Entries.Any(e => e.Owner == owner))
                .Select(kv => kv.Key).ToList();
        }

        foreach (var k in keys)
        {
            Unpatch(k, owner);
        }
    }

    /// <summary>Tears down every M2 patch (used by tests and shutdown).</summary>
    public static void UnpatchEverything()
    {
        List<M2Site> sites;
        lock (RegistryLock)
        {
            sites = M2Sites.Values.ToList();
        }

        foreach (var site in sites)
        {
            lock (RegistryLock)
            {
                if (!M2Sites.ContainsValue(site))
                {
                    continue;
                }
                site.Entries.Clear();
                Teardown(site);
                M2Sites.Remove(site.Method);
            }
        }
    }

    // ---------------------------------------------------------------- internals

    private sealed class M2Site(MethodBase method, IlBody body)
    {
        public readonly MethodBase Method = method;
        public readonly IlBody Body = body;
        public readonly List<PatchEntry> Entries = new();
        public Detour? Detour;
        public MethodInfo? Patched; // body with injected chain (the detour target)

        /// <summary>The native address of the patched body, resolved once per build.</summary>
        public IntPtr PatchedEntry;
    }

    private static readonly Dictionary<MethodBase, M2Site> M2Sites = new();

    private static M2Site GetOrCreateM2Site(MethodBase target)
    {
        if (M2Sites.TryGetValue(target, out var existing))
        {
            return existing;
        }

        // A method can be M1-hooked or M2-patched, never both: each installs its own detour
        // on the same prologue and they would corrupt each other.
        if (Sites.ContainsKey(target))
        {
            throw new HookException($"cannot patch {target}: an M1 hook is already installed on it");
        }

        var addr = NativeInterop.GetCodeAddress(target);
        if (addr == IntPtr.Zero)
        {
            throw new HookException($"cannot patch {target}: no native code address (open generic?)");
        }

        var detour = Detour.TryCreate(addr)
            ?? throw new HookException($"cannot patch {target}: prologue not relocatable at {addr.ToInt64():X}");

        var body = IlRewriter.Analyze(target);

        var site = new M2Site(target, body)
        {
            Detour = detour,
        };

        M2Sites[target] = site;
        return site;
    }

    private static void RebuildAndApply(M2Site site)
    {
        lock (RegistryLock)
        {
            var patched = PatchedBodyBuilder.Build(site.Method, site.Entries, site.Body);
            site.Patched = patched;
            site.PatchedEntry = NativeInterop.GetCodeAddress(patched);
            if (site.PatchedEntry == IntPtr.Zero)
            {
                throw new HookException($"cannot resolve patched body for {site.Method}");
            }

            var detour = site.Detour!;
            detour.Uninstall();
            detour.Retarget(site.PatchedEntry);
            detour.Install();
        }
    }

    private static void Teardown(M2Site site)
    {
        lock (RegistryLock)
        {
            site.Detour?.Uninstall();
            site.Detour?.Dispose();
            site.Detour = null;
            site.Patched = null;
        }
    }
}
