using System.Reflection;
using System.Reflection.Emit;

namespace Nami.Core.Generations;

/// <summary>
/// Reflection-only seam to the optional Wave subsystem. Nami.Core must not reference Nami.Wave
/// (Wave is optional; see <c>Chainloader.cs:343</c> rationale): every call below resolves
/// <c>Type.GetType("Nami.Wave.Wave, Nami.Wave")</c> / <c>"Nami.Wave.WaveIl2Cpp, Nami.Wave"</c> at
/// runtime and invokes through <see cref="MethodInfo"/>. A missing assembly or method throws
/// <see cref="InvalidOperationException"/> with a clear message; preparation treats that as a
/// candidate rejection (old generation kept), commit-time failures are logged and never roll back.
/// </summary>
internal static class WaveBridge
{
    private const string WaveAssembly = "Nami.Wave";
    private const string WaveTypeName = "Nami.Wave.Wave, Nami.Wave";
    private const string Il2CppTypeName = "Nami.Wave.WaveIl2Cpp, Nami.Wave";

    internal static Type RequireWave()
    {
        var type = Type.GetType(WaveTypeName, throwOnError: false);
        if (type is null)
        {
            throw new InvalidOperationException(
                "Wave is not present in this process (Nami.Wave assembly missing); hook registration refused.");
        }

        return type;
    }

    internal static Type RequireIl2Cpp()
    {
        var type = Type.GetType(Il2CppTypeName, throwOnError: false);
        if (type is null)
        {
            throw new InvalidOperationException(
                "Wave IL2CPP support is not present in this process (Nami.Wave assembly missing); hook refused.");
        }

        return type;
    }

    /// <summary>
    /// Type-shaped method lookup: overloads are resolved by leading parameter types, never by
    /// bare arity. Both <c>Wave.Patch</c> overloads take 5 parameters (the <c>MethodBase</c> target
    /// overload and the generic-definition overload), and <c>HookFull</c>/<c>HookTyped</c> both take
    /// 9, so arity alone binds the wrong method (or none) depending on reflection order. The
    /// leading types (<c>MethodBase</c> vs <c>MethodInfo</c>, <c>string</c> vs <c>Type[]</c>,
    /// <c>int</c> vs <c>IReadOnlyList&lt;&gt;</c>) are stable and unambiguous.
    /// </summary>
    private static MethodInfo RequireMethod(Type type, string name, Func<ParameterInfo[], bool> shape, string expected)
    {
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == name && shape(m.GetParameters()));
        if (method is null)
        {
            throw new InvalidOperationException(
                $"Wave API drift: {type.FullName}.{name} {expected} not found; hook refused.");
        }

        return method;
    }

    /// <summary>
    /// Pure overload selector for the <c>MethodBase</c>-target <c>Wave.Patch</c> overload, over
    /// caller-supplied candidates so the seam stays unit-testable without loading Wave
    /// (Nami.Tests exercises this against a Wave-double mirroring the real overload pair).
    /// </summary>
    internal static MethodInfo? SelectPatchMethod(IEnumerable<MethodInfo> candidates) =>
        candidates.FirstOrDefault(IsMethodBasePatch);

    private static bool IsMethodBasePatch(MethodInfo method)
    {
        if (!string.Equals(method.Name, "Patch", StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length == 5 &&
            parameters[0].ParameterType == typeof(MethodBase) &&
            parameters[1].ParameterType == typeof(string);
    }

    /// <summary>Pure selector for <c>Wave.Hook</c> (gate/observer entry point).</summary>
    internal static MethodInfo? SelectHookMethod(IEnumerable<MethodInfo> candidates) =>
        candidates.FirstOrDefault(m =>
            m.Name == "Hook" &&
            m.GetParameters() is { Length: 4 } parameters &&
            parameters[0].ParameterType == typeof(MethodBase) &&
            parameters[1].ParameterType == typeof(string));

    /// <summary>Pure selector for <c>WaveIl2Cpp.HookFull</c> (disambiguated from <c>HookTyped</c> by the 5th parameter).</summary>
    internal static MethodInfo? SelectHookFullMethod(IEnumerable<MethodInfo> candidates) =>
        candidates.FirstOrDefault(m =>
            m.Name == "HookFull" &&
            m.GetParameters() is { Length: 9 } parameters &&
            parameters[4].ParameterType == typeof(int));

    /// <summary>Pure selector for <c>WaveIl2Cpp.HookTyped</c> (disambiguated from <c>HookFull</c> by the 5th parameter).</summary>
    internal static MethodInfo? SelectHookTypedMethod(IEnumerable<MethodInfo> candidates) =>
        candidates.FirstOrDefault(m =>
            m.Name == "HookTyped" &&
            m.GetParameters() is { Length: 9 } parameters &&
            parameters[4].ParameterType.IsGenericType &&
            parameters[4].ParameterType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));

    private static MethodInfo RequireHookMethod(Type wave) =>
        RequireMethod(wave, "Hook",
            parameters => parameters.Length == 4 &&
                parameters[0].ParameterType == typeof(MethodBase) &&
                parameters[1].ParameterType == typeof(string),
            "(MethodBase, string, gate, observer)");

    private static MethodInfo RequirePatchMethod(Type wave) =>
        SelectPatchMethod(wave.GetMethods(BindingFlags.Public | BindingFlags.Static))
            ?? throw new InvalidOperationException(
                $"Wave API drift: {wave.FullName}.Patch (MethodBase, string, prefix, postfix, transpiler) not found; hook refused.");

    private static MethodInfo RequireHookFullMethod(Type il2cpp) =>
        SelectHookFullMethod(il2cpp.GetMethods(BindingFlags.Public | BindingFlags.Static))
            ?? throw new InvalidOperationException(
                $"Wave API drift: {il2cpp.FullName}.HookFull (assembly, ns, klass, method, argCount, returnKind, prefix, postfix, owner) not found; hook refused.");

    private static MethodInfo RequireHookTypedMethod(Type il2cpp) =>
        SelectHookTypedMethod(il2cpp.GetMethods(BindingFlags.Public | BindingFlags.Static))
            ?? throw new InvalidOperationException(
                $"Wave API drift: {il2cpp.FullName}.HookTyped (assembly, ns, klass, method, parameterTypes, returnType, prefix, postfix, owner) not found; hook refused.");

    // ------------------------------------------------------------ CoreCLR

    internal static void HookGate(MethodBase target, string owner, Func<bool> trampoline) =>
        RequireHookMethod(RequireWave())
            .Invoke(null, new object?[] { target, owner, trampoline, null });

    internal static void HookObserver(MethodBase target, string owner, Action trampoline) =>
        RequireHookMethod(RequireWave())
            .Invoke(null, new object?[] { target, owner, null, trampoline });

    internal static void PatchPrefix(MethodBase target, string owner, Delegate trampoline) =>
        RequirePatchMethod(RequireWave())
            .Invoke(null, new object?[] { target, owner, trampoline, null, null });

    internal static void PatchPostfix(MethodBase target, string owner, Delegate trampoline) =>
        RequirePatchMethod(RequireWave())
            .Invoke(null, new object?[] { target, owner, null, trampoline, null });

    internal static void PatchTranspiler(MethodBase target, string owner, Delegate transpiler)
    {
        if (!string.Equals(transpiler.GetType().Assembly.GetName().Name, WaveAssembly, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Transpiler for {target} must be a Nami.Wave.WaveTranspiler (got {transpiler.GetType().FullName}); refused.");
        }

        RequirePatchMethod(RequireWave())
            .Invoke(null, new object?[] { target, owner, null, null, transpiler });
    }

    /// <summary>
    /// Teardown for one owner: CoreCLR unpatch plus IL2CPP unhook. <c>WaveIl2Cpp.UnhookAll</c>
    /// exists (Wave.Il2Cpp.cs:993, verified) but is still resolved defensively: a missing method
    /// keeps CoreCLR-only teardown and is noted by the caller instead of throwing. Never throws.
    /// Direct (non-slot) registrations keep teardown-at-retire semantics (brief unhooked window);
    /// slot trampolines live under <c>nami:gen:*</c> owners and are managed per-slot, not here.
    /// </summary>
    internal static void TeardownOwner(string owner, Action<string> log)
    {
        try
        {
            var wave = Type.GetType(WaveTypeName, throwOnError: false);
            wave?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "UnpatchAll" && m.GetParameters().Length == 1)
                ?.Invoke(null, new object?[] { owner });
        }
        catch (Exception ex)
        {
            log($"Wave teardown for '{owner}' reported: {ex.GetBaseException().Message}");
        }

        try
        {
            var il2cpp = Type.GetType(Il2CppTypeName, throwOnError: false);
            var unhook = il2cpp?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "UnhookAll" && m.GetParameters().Length == 1);
            if (unhook is null)
            {
                // Gap, not an error: CoreCLR-only teardown (recorded by the caller).
                log($"WaveIl2Cpp.UnhookAll missing for '{owner}'; CoreCLR-only teardown.");
                return;
            }

            unhook.Invoke(null, new object?[] { owner });
        }
        catch (Exception ex)
        {
            log($"Wave IL2CPP teardown for '{owner}' reported: {ex.GetBaseException().Message}");
        }
    }

    // ------------------------------------------------------------ IL2CPP

    internal static void Il2CppHook(string assembly, string ns, string klass, string method,
        int argCount, Delegate callback, string owner) =>
        RequireMethod(RequireIl2Cpp(), "Hook",
            parameters => parameters.Length == 7 &&
                parameters[0].ParameterType == typeof(string) &&
                parameters[4].ParameterType == typeof(int),
            "(assembly, ns, klass, method, argCount, callback, owner)")
            .Invoke(null, new object?[] { assembly, ns, klass, method, argCount, callback, owner });

    internal static void Il2CppHookFull(string assembly, string ns, string klass, string method,
        int argCount, int returnKind, string owner, Delegate? prefix, Delegate? postfix)
    {
        var hook = RequireHookFullMethod(RequireIl2Cpp());
    }

    internal static void Il2CppHookTyped(string assembly, string ns, string klass, string method,
        IReadOnlyList<int> parameterTypes, int? returnType, string owner, Delegate? prefix, Delegate? postfix)
    {
        var hook = RequireHookTypedMethod(RequireIl2Cpp());
        var parameters = hook.GetParameters();
        // Element type (TideType) is read off the method itself so Core never names the home assembly.
        var listType = parameters[4].ParameterType;
        var elementType = listType.IsArray
            ? listType.GetElementType()!
            : listType.IsGenericType ? listType.GetGenericArguments()[0] : throw new InvalidOperationException(
                $"Wave API drift: HookTyped parameter shape changed ({listType}); hook refused.");
        var array = Array.CreateInstance(elementType, parameterTypes.Count);
        for (var i = 0; i < parameterTypes.Count; i++)
        {
            array.SetValue(Enum.ToObject(elementType, parameterTypes[i]), i);
        }

        var returnTypeType = parameters[5].ParameterType;
        var underlying = Nullable.GetUnderlyingType(returnTypeType);
        object? returnValue = null;
        if (returnType.HasValue)
        {
            returnValue = Enum.ToObject(underlying ?? returnTypeType, returnType.Value);
        }

        hook.Invoke(null, new object?[] { assembly, ns, klass, method, array, returnValue, prefix, postfix, owner });
    }

    // ------------------------------------------------------------ separability

    /// <summary>
    /// Proves a callback separable from any single generation: true when the delegate's type
    /// lives outside every collectible ALC (<see cref="Assembly.IsCollectible"/> false — BCL and
    /// shared host assemblies). A stable-typed callback may be served by a register-once host
    /// trampoline (zero Wave rebuild on swap). A generation-local delegate type would root its
    /// ALC through any long-lived trampoline that references it (an emitted <c>castclass</c>, a
    /// closed-over <see cref="Type"/>, or the instance itself all keep the loader alive), so such
    /// slots take the per-generation path: fresh trampoline plus <c>Unpatch</c>/<c>Patch</c> at
    /// commit, with the old trampoline released only after quiescence. Reported, never forced.
    /// </summary>
    internal static bool IsStableCallback(Delegate callback) =>
        !callback.GetType().Assembly.IsCollectible;

    /// <summary>
    /// Per-slot host state bound as the target of the emitted trampoline. Holds only host
    /// references (the acquire closure captures the chainloader plus strings): never a generation
    /// callback, its <see cref="Type"/>, or any ALC reference. Dropping the trampoline delegate
    /// releases this state.
    /// </summary>
    private sealed class TrampolineState
    {
        public Func<(ExecutionLease Lease, Delegate Callback)> Acquire = null!;
    }

    /// <summary>
    /// Builds a host-owned trampoline with exactly the delegate type of <paramref name="callbackType"/>.
    /// The emitted body runs the fused <paramref name="acquire"/> (snapshot read once + lease through
    /// the retire gate), invokes the current callback, and releases the lease in <c>finally</c>.
    /// A retired/missing slot fails safe to <c>default</c> (gate <c>false</c> skips the original;
    /// observers/postfixes no-op); mod exceptions propagate exactly as with a direct registration.
    /// The signature is read from <paramref name="callbackType"/> but no instance, <see cref="Type"/>,
    /// or ALC reference is captured: the only bound target is the host-owned
    /// <see cref="TrampolineState"/>. Callers must still honor <see cref="IsStableCallback"/> — a
    /// trampoline emitted for a generation-local delegate type references that type and must be
    /// released (unpatched) at the next commit, never held across generations.
    /// </summary>
    /// <param name="callbackType">Delegate type to match (only the Invoke signature is read).</param>
    /// <param name="acquire">Fused snapshot-read + lease acquisition; throws <see cref="GenerationRetiredException"/> when retired.</param>
    internal static Delegate CreateTrampoline(Type callbackType, Func<(ExecutionLease Lease, Delegate Callback)> acquire)
    {
        ArgumentNullException.ThrowIfNull(callbackType);
        ArgumentNullException.ThrowIfNull(acquire);
        if (!typeof(Delegate).IsAssignableFrom(callbackType))
        {
            throw new ArgumentException($"not a delegate type: {callbackType}", nameof(callbackType));
        }

        var invoke = callbackType.GetMethod("Invoke")
            ?? throw new ArgumentException($"delegate {callbackType} has no Invoke method", nameof(callbackType));
        var parameters = invoke.GetParameters();
        if (parameters.Any(p => p.ParameterType == typeof(TypedReference)))
        {
            throw new InvalidOperationException($"callback shape {callbackType} uses TypedReference; trampoline refused.");
        }

        var parameterTypes = parameters.Select(p => p.ParameterType).ToArray();
        var returnType = invoke.ReturnType;
        var hasResult = returnType != typeof(void);

        // Bound-instance pattern: the state object becomes arg0 and is bound away by CreateDelegate,
        // so the resulting delegate has exactly the callback signature. No global token map needed.
        var state = new TrampolineState { Acquire = acquire };
        var dense = new[] { typeof(TrampolineState) }.Concat(parameterTypes).ToArray();
        var method = new DynamicMethod(
            $"nami_trampoline_{callbackType.Name}",
            returnType,
            dense,
            typeof(WaveBridge).Module,
            skipVisibility: true);

        var il = method.GetILGenerator();
        var lease = il.DeclareLocal(typeof(ExecutionLease));
        var current = il.DeclareLocal(callbackType);
        var result = hasResult ? il.DeclareLocal(returnType) : null;
        var done = il.DefineLabel();
        var skipDispose = il.DefineLabel();
        var leaseField = typeof(ValueTuple<ExecutionLease, Delegate>).GetField("Item1")!;
        var callbackField = typeof(ValueTuple<ExecutionLease, Delegate>).GetField("Item2")!;
        var funcInvoke = typeof(Func<(ExecutionLease Lease, Delegate Callback)>).GetMethod("Invoke")!;
        var dispose = typeof(ExecutionLease).GetMethod(nameof(ExecutionLease.Dispose))!;

        il.BeginExceptionBlock();

        // var (lease, callback) = state.Acquire(); current = (T)callback;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, typeof(TrampolineState).GetField(nameof(TrampolineState.Acquire))!);
        il.Emit(OpCodes.Callvirt, funcInvoke);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldfld, leaseField);
        il.Emit(OpCodes.Stloc, lease);
        il.Emit(OpCodes.Ldfld, callbackField);
        il.Emit(OpCodes.Castclass, callbackType);
        il.Emit(OpCodes.Stloc, current);

        // current(args...).Invoke()
        il.Emit(OpCodes.Ldloc, current);
        for (var i = 0; i < parameterTypes.Length; i++)
        {
            il.Emit(OpCodes.Ldarg, i + 1);
        }

        il.Emit(OpCodes.Callvirt, invoke);
        if (hasResult)
        {
            il.Emit(OpCodes.Stloc, result!);
        }

        il.Emit(OpCodes.Leave, done);

        il.BeginCatchBlock(typeof(GenerationRetiredException));
        il.Emit(OpCodes.Pop); // retired: fail safe to default(TResult)
        if (hasResult)
        {
            il.Emit(OpCodes.Ldloca, result!);
            il.Emit(OpCodes.Initobj, returnType);
        }

        il.Emit(OpCodes.Leave, done);

        il.BeginFinallyBlock();
        il.Emit(OpCodes.Ldloc, lease);
        il.Emit(OpCodes.Brfalse_S, skipDispose);
        il.Emit(OpCodes.Ldloc, lease);
        il.Emit(OpCodes.Callvirt, dispose);
        il.MarkLabel(skipDispose);
        il.Emit(OpCodes.Endfinally);

        il.EndExceptionBlock();
        il.MarkLabel(done);
        if (hasResult)
        {
            il.Emit(OpCodes.Ldloc, result!);
        }

        il.Emit(OpCodes.Ret);

        return method.CreateDelegate(callbackType, state);
    }
}
