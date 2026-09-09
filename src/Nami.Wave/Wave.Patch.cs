using System.Reflection;
using Nami.Wave.Internal;

namespace Nami.Wave;

/// <summary>
/// Unified patching: <see cref="Wave.Patch"/> picks the cheapest strategy that satisfies
/// what was requested - the fast native stub for prefix-only hooks on GC-tracking-free
/// shapes, the IL-copy body otherwise - and moves the site between strategies
/// transparently as owners come and go. See <see cref="Wave"/> for the model.
/// </summary>
public static unsafe partial class Wave
{
    // ---------------------------------------------------------------- API

    /// <summary>
    /// Patches <paramref name="target"/> for <paramref name="owner"/>: an optional
    /// prefix (observe args, return false to skip), an optional postfix, and an optional
    /// transpiler. Prefix-only hooks on fast-eligible shapes take the native stub;
    /// anything needing a postfix, a transpiler, or a richer signature takes the IL-copy
    /// body. Query the choice with <see cref="GetPatchEngine"/>.
    /// </summary>
    public static void Patch(MethodBase target, string owner, Delegate? prefix = null, Delegate? postfix = null, WaveTranspiler? transpiler = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        if (prefix is null && postfix is null && transpiler is null)
        {
            throw new ArgumentException("provide at least a prefix, a postfix, or a transpiler");
        }

        lock (RegistryLock)
        {
            var site = GetOrCreateUnifiedSite(target, "patch");
            if (site.Entries.Any(e => e.Owner == owner && e.Source == ApiSource.Patch))
            {
                throw new InvalidOperationException($"owner '{owner}' already hooked {target}");
            }

            var entry = new WaveChainEntry { Owner = owner, Source = ApiSource.Patch, Seq = site.NextSeq++ };
            var targetParams = target.GetParameters();
            var targetParamTypes = targetParams.Select(p => p.ParameterType).ToArray();
            if (prefix is not null)
            {
                entry.Prefix = prefix;
                var binding = site.Shape is not null
                    ? WaveFast.BindHook(prefix, targetParams, targetParamTypes, isPrefix: true)
                    : null;
                if (binding is not null)
                {
                    entry.PrefixInvoker = WaveFast.MakeInvoker(prefix, site.Shape!, binding);
                    entry.PrefixBlind = binding.Slots.Length == 0;
                    // Pre-JIT outside the stub window (see WarmInvoker): first-call
                    // compilation under a GC-info-less stub frame is fatal.
                    System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(prefix);
                    WaveFast.WarmInvoker(entry.PrefixInvoker, prefix);
                }
            }
            if (postfix is not null)
            {
                entry.Postfix = postfix;
            }
            if (transpiler is not null)
            {
                entry.Transpilers.Add(transpiler);
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

    /// <summary>
    /// Patches a closed instantiation of an open generic method definition. Definitions
    /// have no machine code and cannot be patched directly - this closes over
    /// <paramref name="typeArguments"/> first, then patches. Returns the closed method
    /// (hand it to <see cref="Unpatch(MethodBase, string)"/> to remove the patch).
    /// </summary>
    public static MethodInfo Patch(MethodInfo genericDefinition, Type[] typeArguments, string owner, Delegate? prefix = null, Delegate? postfix = null)
    {
        ArgumentNullException.ThrowIfNull(genericDefinition);
        ArgumentNullException.ThrowIfNull(typeArguments);
        if (!genericDefinition.IsGenericMethodDefinition)
        {
            throw new ArgumentException($"not a generic method definition: {genericDefinition}", nameof(genericDefinition));
        }
        MethodInfo closed;
        try
        {
            closed = genericDefinition.MakeGenericMethod(typeArguments);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"cannot close {genericDefinition} over [{string.Join(", ", typeArguments.Select(t => t.Name))}]: {ex.Message}", ex);
        }
        Patch(closed, owner, prefix, postfix);
        return closed;
    }

    /// <summary>Removes an owner's patch from <paramref name="target"/>.</summary>
    public static void Unpatch(MethodBase target, string owner)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        Unhook(target, owner); // owner-scoped: one chain per target, either entry point
    }

    /// <summary>True when <paramref name="owner"/> has a patch on <paramref name="target"/>.</summary>
    public static bool IsPatched(MethodBase target, string owner)
    {
        lock (RegistryLock)
        {
            return UnifiedSites.TryGetValue(target, out var site) && site.Entries.Any(e => e.Owner == owner);
        }
    }

    /// <summary>Removes every patch owned by <paramref name="owner"/>.</summary>
    public static void UnpatchAll(string owner)
    {
        List<MethodBase> keys;
        lock (RegistryLock)
        {
            keys = UnifiedSites.Where(kv => kv.Value.Entries.Any(e => e.Owner == owner))
                .Select(kv => kv.Key).ToList();
        }

        foreach (var k in keys)
        {
            Unpatch(k, owner);
        }
    }

    /// <summary>Tears down every patch (used by tests and shutdown).</summary>
    public static void UnpatchEverything()
    {
        UnhookEverything(); // unified registry: one teardown covers both entry points
    }

    /// <summary>Which engine currently serves <paramref name="target"/> (None if unpatched).</summary>
    public static WavePatchEngine GetPatchEngine(MethodBase target)
    {
        lock (RegistryLock)
        {
            if (!UnifiedSites.TryGetValue(target, out var site))
            {
                return WavePatchEngine.None;
            }
            return site.IsFast ? WavePatchEngine.Fast : WavePatchEngine.ILCopy;
        }
    }

    // ---------------------------------------------------------------- ILCopy strategy

    /// <summary>
    /// Builds the IL-copy body for a site whose entries need it: prefixes in unified
    /// pre-order (Hook gates/observers map to bool/void prefixes), postfixes oldest-first
    /// (the ret-tail loop runs them newest-first, as before), transpilers in entry order.
    /// </summary>
    private static void RebuildIlCopy(WaveSite site)
    {
        var ordered = site.Entries.OrderBy(e => e.Source == ApiSource.Hook ? -(e.Seq + 1) : 0)
            .ThenBy(e => e.Seq).ToList();
        var synth = new List<PatchEntry>();
        foreach (var e in ordered)
        {
            if (e.Source == ApiSource.Hook)
            {
                if (e.Gate is not null)
                {
                    // Hook gates skip on true; M2 prefixes skip on false - normalize.
                    var gate = e.Gate;
                    synth.Add(new PatchEntry { Owner = e.Owner, Prefix = (Func<bool>)(() => !gate()) });
                }
                if (e.Observer is not null)
                {
                    synth.Add(new PatchEntry { Owner = e.Owner, Prefix = e.Observer });
                }
            }
            else if (e.Prefix is not null)
            {
                var pe = new PatchEntry { Owner = e.Owner, Prefix = e.Prefix };
                foreach (var t in e.Transpilers)
                {
                    pe.Transpilers.Add(t);
                }
                synth.Add(pe);
            }
            else
            {
                foreach (var t in e.Transpilers)
                {
                    var pe = new PatchEntry { Owner = e.Owner };
                    pe.Transpilers.Add(t);
                    synth.Add(pe);
                }
            }
        }
        foreach (var e in site.Entries.OrderBy(e => e.Seq))
        {
            if (e.Postfix is not null)
            {
                synth.Add(new PatchEntry { Owner = e.Owner, Postfix = e.Postfix });
            }
        }

        site.Body ??= IlRewriter.Analyze(site.Method);
        var patched = PatchedBodyBuilder.Build(site.Method, synth, site.Body);
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

    /// <summary>
    /// Throws the precise, actionable error for an unaddressable target. Open generics
    /// have no machine code by definition - there is nothing to detour - so the error
    /// names the exact closing step instead of a bare address complaint.
    /// </summary>
    private static void ThrowForOpenGeneric(MethodBase target, string verb)
    {
        if (target is MethodInfo { IsGenericMethodDefinition: true })
        {
            throw new HookException(
                $"cannot {verb} {target}: it is an open generic method definition and has no machine code — " +
                "close it first (definition.MakeGenericMethod(typeof(...)), or Wave.Patch(definition, typeArguments, owner, ...)), " +
                $"then {verb} the closed method");
        }
        if (target.DeclaringType?.IsGenericTypeDefinition == true)
        {
            throw new HookException(
                $"cannot {verb} {target}: its declaring type is an open generic definition and has no machine code — " +
                $"close the type first (type.MakeGenericType(...).GetMethod(...)), then {verb}");
        }
        throw new HookException($"cannot {verb} {target}: no native code address");
    }
}
