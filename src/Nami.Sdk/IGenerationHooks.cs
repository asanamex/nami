using System.Reflection;

namespace Nami.Sdk;

/// <summary>
/// Host-owned Wave hook surface for one mod generation, surfaced as <c>IPluginContext.Hooks</c>
/// (additive, consumer-compatible: mods consume, never implement, this interface).
/// Each method mirrors a Wave registration signature with the Wave owner replaced by a
/// mod-scoped <c>slotKey</c>: the host registers Wave ONCE per slot under the owner
/// <c>$"nami:gen:{modId}:{slotKey}"</c> a stable trampoline (snapshot read, lease, invoke
/// current callback), so a generation swap publishes a new snapshot with zero Wave rebuild
/// and zero native rewrite for dispatchable slots. Slots needing a rebuild re-register at
/// commit (<c>Unpatch</c> old plus <c>Patch</c> new; the native rewrite is accepted and logged).
/// Retire order per slot: publish the new snapshot first, then remove old bindings from
/// eligibility, then drop old callback references only after quiescence. Direct (non-slot)
/// Wave registrations keep today's teardown-at-retire semantics and are unaffected by this surface.
/// A duplicate <c>slotKey</c> for the same mod throws <see cref="InvalidOperationException"/>
/// (same rule as Wave's duplicate owner registration).
/// </summary>
public interface IGenerationHooks
{
    /// <summary>
    /// Mirrors the gate half of <c>Wave.Hook(target, owner, gate, observer)</c> for the same frozen
    /// scope (parameterless void targets only). Registers Wave ONCE per slot under the host owner
    /// <c>$"nami:gen:{modId}:{slotKey}"</c> a stable trampoline that reads the current snapshot,
    /// acquires the generation lease, and invokes the current generation's gate.
    /// Capability is FullyLiveReloadable via trampoline dispatch (RequiresPatchRebuild when the site
    /// runs the IL-copy engine; the mechanism is snapshotted at registration): dispatchable swaps
    /// publish a new snapshot with zero Wave rebuild, rebuild slots re-register at commit.
    /// Retire order: publish the new snapshot first, then remove old bindings from eligibility,
    /// then drop old callback references only after quiescence.
    /// </summary>
    /// <param name="target">Wave hook target (parameterless void method, as <c>Wave.Hook</c> requires).</param>
    /// <param name="slotKey">Mod-scoped slot name; unique per mod, never empty.</param>
    /// <param name="gate">Current generation's gate vote; return false to skip the original.</param>
    void HookGate(MethodBase target, string slotKey, Func<bool> gate);

    /// <summary>
    /// Mirrors the observer half of <c>Wave.Hook(target, owner, gate, observer)</c> for the same frozen
    /// scope (parameterless void targets only). Registers Wave ONCE per slot under the host owner
    /// <c>$"nami:gen:{modId}:{slotKey}"</c> a stable trampoline that reads the current snapshot,
    /// acquires the generation lease, and invokes the current generation's observer.
    /// Capability is FullyLiveReloadable via trampoline dispatch (RequiresPatchRebuild when the site
    /// runs the IL-copy engine; the mechanism is snapshotted at registration): dispatchable swaps
    /// publish a new snapshot with zero Wave rebuild, rebuild slots re-register at commit.
    /// Retire order: publish the new snapshot first, then remove old bindings from eligibility,
    /// then drop old callback references only after quiescence.
    /// </summary>
    /// <param name="target">Wave hook target (parameterless void method, as <c>Wave.Hook</c> requires).</param>
    /// <param name="slotKey">Mod-scoped slot name; unique per mod, never empty.</param>
    /// <param name="observer">Current generation's observer, run on every call.</param>
    void HookObserver(MethodBase target, string slotKey, Action observer);

    /// <summary>
    /// Mirrors the <c>prefix</c> argument of <c>Wave.Patch(target, owner, prefix, postfix, transpiler)</c>.
    /// Registers Wave ONCE per slot under the host owner <c>$"nami:gen:{modId}:{slotKey}"</c> a stable
    /// trampoline that reads the current snapshot, acquires the generation lease, and invokes the
    /// current generation's prefix.
    /// Capability is FullyLiveReloadable via trampoline dispatch (RequiresPatchRebuild when the site
    /// runs the IL-copy engine; the mechanism is snapshotted at registration): dispatchable swaps
    /// publish a new snapshot with zero Wave rebuild, rebuild slots re-register at commit
    /// (<c>Unpatch</c> old plus <c>Patch</c> new).
    /// Retire order: publish the new snapshot first, then remove old bindings from eligibility,
    /// then drop old callback references only after quiescence.
    /// </summary>
    /// <param name="target">Wave patch target.</param>
    /// <param name="slotKey">Mod-scoped slot name; unique per mod, never empty.</param>
    /// <param name="prefix">Current generation's prefix delegate (same shape <c>Wave.Patch</c> requires).</param>
    void PatchPrefix(MethodBase target, string slotKey, Delegate prefix);

    /// <summary>
    /// Mirrors the <c>postfix</c> argument of <c>Wave.Patch(target, owner, prefix, postfix, transpiler)</c>.
    /// Registers Wave ONCE per slot under the host owner <c>$"nami:gen:{modId}:{slotKey}"</c> a stable
    /// trampoline that reads the current snapshot, acquires the generation lease, and invokes the
    /// current generation's postfix.
    /// Capability is FullyLiveReloadable via trampoline dispatch (RequiresPatchRebuild when the site
    /// runs the IL-copy engine; the mechanism is snapshotted at registration): dispatchable swaps
    /// publish a new snapshot with zero Wave rebuild, rebuild slots re-register at commit
    /// (<c>Unpatch</c> old plus <c>Patch</c> new).
    /// Retire order: publish the new snapshot first, then remove old bindings from eligibility,
    /// then drop old callback references only after quiescence.
    /// </summary>
    /// <param name="target">Wave patch target.</param>
    /// <param name="slotKey">Mod-scoped slot name; unique per mod, never empty.</param>
    /// <param name="postfix">Current generation's postfix delegate (same shape <c>Wave.Patch</c> requires).</param>
    void PatchPostfix(MethodBase target, string slotKey, Delegate postfix);

    /// <summary>
    /// Mirrors the <c>transpiler</c> argument of <c>Wave.Patch(target, owner, prefix, postfix, transpiler)</c>.
    /// The parameter is <see cref="Delegate"/> (not the Wave transpiler type) so this SDK stays free of a
    /// Wave reference; callers referencing Nami.Wave pass a <c>WaveTranspiler</c> directly.
    /// Registers Wave ONCE per slot under the host owner <c>$"nami:gen:{modId}:{slotKey}"</c>.
    /// Capability is always RequiresPatchRebuild, never a callback swap: the generated body is the
    /// artifact, so the replacement registers at commit on the tick drain (the CoreCLR safe point;
    /// best-effort — in-flight calls observe Wave's rebuild semantics during the rewrite, and a
    /// failed rebuild is reported with the published snapshot left standing). Overlap between
    /// owners is recorded as a Wave transpiler conflict, never silently composed.
    /// Retire order: publish the new snapshot first, then remove old bindings from eligibility,
    /// then drop old callback references only after quiescence.
    /// </summary>

    /// <summary>
    /// Mirrors <c>WaveIl2Cpp.Hook(assembly, ns, klass, method, argCount, callback, owner)</c>
    /// (fast path, up to 4 register arguments). The callback is <see cref="Delegate"/> (not the Wave
    /// callback type) so this SDK stays free of a Wave reference and unsafe blocks; callers
    /// referencing Nami.Wave pass an <c>Il2CppHookCallback</c> directly. Registers Wave ONCE per slot
    /// under the host owner <c>$"nami:gen:{modId}:{slotKey}"</c> the generation's callback directly
    /// (no stable trampoline: there is no MethodBase signature to emit one from). Native dispatch
    /// stays untouched wherever the Wave implementation keeps it stable across re-hooks; swaps
    /// re-register at commit.
    /// Capability is at minimum SafePointRequired; IL2CPP separability is mechanically present but
    /// operationally unproven, so hosts report RestartRequired for these slots until a
    /// proven-separable entry exists (reported, never forced).
    /// Retire order: publish the new snapshot first, then remove old bindings from eligibility,
    /// then drop old callback references only after quiescence.
    /// </summary>
    /// <param name="assembly">IL2CPP assembly name.</param>
    /// <param name="ns">Namespace of the declaring type.</param>
    /// <param name="klass">Declaring type name.</param>
    /// <param name="method">Method name.</param>
    /// <param name="argCount">Register argument count (0 to 4).</param>
    /// <param name="slotKey">Mod-scoped slot name; unique per mod, never empty.</param>
    /// <param name="callback">Current generation's hook callback (an <c>Il2CppHookCallback</c>).</param>
    void HookIl2Cpp(string assembly, string ns, string klass, string method, int argCount, string slotKey, Delegate callback);

    /// <summary>
    /// Mirrors <c>WaveIl2Cpp.HookFull(assembly, ns, klass, method, argCount, returnKind, prefix, postfix, owner)</c>
    /// (full path: results plus up to 12 arguments). Callbacks are <see cref="Delegate"/> so this SDK stays
    /// free of a Wave reference and unsafe blocks; callers referencing Nami.Wave pass
    /// <c>Il2CppHookPrefixCallback</c> / <c>Il2CppHookPostfixCallback</c> directly. Registers Wave ONCE per
    /// slot under the host owner <c>$"nami:gen:{modId}:{slotKey}"</c> the generation's callbacks directly
    /// (no stable trampoline); native dispatch stays untouched wherever the Wave implementation
    /// keeps it stable across re-hooks. Swaps re-register at commit.
    /// Capability is at minimum SafePointRequired; IL2CPP separability is mechanically present but
    /// operationally unproven, so hosts report RestartRequired for these slots until a
    /// proven-separable entry exists (reported, never forced).
    /// Retire order: publish the new snapshot first, then remove old bindings from eligibility,
    /// then drop old callback references only after quiescence.
    /// </summary>
    /// <param name="assembly">IL2CPP assembly name.</param>
    /// <param name="ns">Namespace of the declaring type.</param>
    /// <param name="klass">Declaring type name.</param>
    /// <param name="method">Method name.</param>
    /// <param name="argCount">Exposed argument count (0 to 12).</param>
    /// <param name="returnKind">Result-slot interpretation as an <c>Il2CppReturnKind</c> tag (Void 0, I32 1, I64 2, F32 3, F64 4).</param>
    /// <param name="slotKey">Mod-scoped slot name; unique per mod, never empty.</param>
    /// <param name="prefix">Current generation's full-path prefix (an <c>Il2CppHookPrefixCallback</c>), or null.</param>
    /// <param name="postfix">Current generation's full-path postfix (an <c>Il2CppHookPostfixCallback</c>), or null.</param>
    void HookIl2CppFull(string assembly, string ns, string klass, string method, int argCount, int returnKind, string slotKey, Delegate? prefix, Delegate? postfix);

    /// <summary>
    /// Mirrors <c>WaveIl2Cpp.HookTyped(assembly, ns, klass, method, parameterTypes, returnType, prefix, postfix, owner)</c>
    /// (typed full path, up to 12 user arguments). Type tags are plain integers so this SDK stays free
    /// of Wave/Tide references: <paramref name="parameterTypes"/> entries are <c>TideType</c> tags (Void 0,
    /// I32 1, I64 2, R4 3, R8 4, Bool 5, String 6, Object 7) and <paramref name="returnType"/> is a
    /// <c>TideType</c> tag or null (return shape unmatched). Callbacks are <see cref="Delegate"/>; callers
    /// referencing Nami.Wave pass <c>Il2CppTypedHookPrefixCallback</c> / <c>Il2CppTypedHookPostfixCallback</c>
    /// directly. Registers Wave ONCE per slot under the host owner <c>$"nami:gen:{modId}:{slotKey}"</c> the
    /// generation's callbacks directly (no stable trampoline); native dispatch stays untouched wherever
    /// the Wave implementation keeps it stable across re-hooks. Swaps re-register at commit.
    /// Capability is at minimum SafePointRequired; IL2CPP separability is mechanically present but
    /// operationally unproven, so hosts report RestartRequired for these slots until a
    /// proven-separable entry exists (reported, never forced).
    /// Retire order: publish the new snapshot first, then remove old bindings from eligibility,
    /// then drop old callback references only after quiescence.
    /// </summary>
    /// <param name="assembly">IL2CPP assembly name.</param>
    /// <param name="ns">Namespace of the declaring type.</param>
    /// <param name="klass">Declaring type name.</param>
    /// <param name="method">Method name.</param>
    /// <param name="parameterTypes">Exact user-parameter type tags (empty selects a zero-parameter method).</param>
    /// <param name="returnType">Return type tag, or null to leave the return shape unmatched.</param>
    /// <param name="slotKey">Mod-scoped slot name; unique per mod, never empty.</param>
    /// <param name="prefix">Current generation's typed prefix, or null.</param>
    /// <param name="postfix">Current generation's typed postfix, or null.</param>
    void HookIl2CppTyped(string assembly, string ns, string klass, string method, IReadOnlyList<int> parameterTypes, int? returnType, string slotKey, Delegate? prefix, Delegate? postfix);
}


