using System.Reflection;
using System.Reflection.Emit;

namespace Nami.Wave.Internal;

/// <summary>A registered prefix/postfix on a patch site.</summary>
public sealed class PatchEntry
{
    public required string Owner;
    public Delegate? Prefix;   // void/bool prefix
    public Delegate? Postfix;  // void postfix
}

/// <summary>
/// Builds the patched body for an IL-copy patch site: the target's own IL with prefix calls
/// at the entry and every <c>ret</c> rewritten to run postfixes first. All calls are plain
/// managed calls — no unmanaged stubs anywhere on the path.
///
/// Injection model (single combined prefix and postfix chain):
///   [prologue: run prefixes; skip if any returned false]
///   [original instructions with ret -> postfix chain + ret]
///
/// Prefix/postfix delegates follow Harmony-style conventions resolved by parameter NAME:
///   - a parameter named like a target parameter receives that argument
///   - `__instance` receives `this` (instance targets)
///   - `__result` receives the return value (postfix only, must match return type or be ref)
///   - `__state` (prefix: out object / postfix: ref object) threads state prefix -> postfix
///   - `__args` receives the full argument array (object[])
/// Prefix may return void or bool (returning false skips the original body).
/// </summary>
internal static class PatchedBodyBuilder
{
    // Well-known parameter names (Harmony-compatible).
    private const string InstanceName = "__instance";
    private const string ResultName = "__result";
    private const string StateName = "__state";
    private const string ArgsName = "__args";

    /// <summary>
    /// Holds the live hook delegates for every site. Emitted code loads a site's entries via
    /// <c>ldsfld All</c> + index, then reads the prefix/postfix delegate and invokes it —
    /// supporting closures, lambdas and static methods uniformly.
    /// </summary>
    public sealed class HookBridge
    {
        public static PatchEntry[][] All = Array.Empty<PatchEntry[]>();
    }

    private static readonly Lock BridgeLock = new();

    /// <summary>Registers the entries array and returns the (index, field) pair emitted code uses.</summary>
    public static (int Index, FieldInfo Field) RegisterBridge(PatchEntry[] entries)
    {
        lock (BridgeLock)
        {
            var all = HookBridge.All;
            var next = new PatchEntry[all.Length + 1][];
            Array.Copy(all, next, all.Length);
            next[all.Length] = entries;
            HookBridge.All = next;
            return (all.Length, typeof(HookBridge).GetField(nameof(HookBridge.All))!);
        }
    }

    public static MethodInfo Build(MethodBase target, IReadOnlyList<PatchEntry> entries, IlBody body)
    {
        var (tb, method, il) = IlRewriter.BeginGeneratedMethod(body, "Wave_Patched");
        var (bridgeIndex, bridgeField) = RegisterBridge(entries.ToArray());

        // State locals, alive across the whole method:
        //   0: object __state
        //   1: return-value local (only when the target returns a value)
        //   2: bool skipOriginal (set by prefixes)
        var lState = il.DeclareLocal(typeof(object));
        var lResult = body.ReturnType == typeof(void) ? null : il.DeclareLocal(body.ReturnType);
        var lSkip = il.DeclareLocal(typeof(bool));

        var lblSkipped = il.DefineLabel();

        // Default-initialize result local (a skipped original still needs a value).
        if (lResult is not null)
        {
            EmitDefault(il, body.ReturnType);
            il.Emit(OpCodes.Stloc, lResult);
        }
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, lSkip);

        // ----- prefixes -----
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Prefix is not { } prefix)
            {
                continue;
            }
            EmitDelegateCall(il, bridgeField, bridgeIndex, i, prefix, target, body, isPrefix: true, lState, lResult, lSkip);
        }

        // If any prefix said skip, jump to the first ret's postfix tail (bound by the hooks).
        il.Emit(OpCodes.Ldloc, lSkip);
        il.Emit(OpCodes.Brtrue, lblSkipped);

        // Run the original body inline; the hooks rewrite every ret into the postfix tail.
        var hooks = new PatchHooks(bridgeField, bridgeIndex, entries, target, body, lState, lResult, lSkip, lblSkipped);
        IlRewriter.EmitInto(il, body, hooks);

        var t = tb.CreateType();
        return t.GetMethod(method.Name, BindingFlags.Public | BindingFlags.Static)!;
    }

    /// <summary>Emits the default value for a type (zero/null).</summary>
    private static void EmitDefault(ILGenerator il, Type t)
    {
        if (t.IsValueType)
        {
            var tmp = il.DeclareLocal(t);
            il.Emit(OpCodes.Ldloca, tmp);
            il.Emit(OpCodes.Initobj, t);
            il.Emit(OpCodes.Ldloc, tmp);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
    }

    /// <summary>Emits a call to a prefix/postfix delegate following name conventions.</summary>
    private static void EmitDelegateCall(
        ILGenerator il, FieldInfo bridgeField, int bridgeIndex, int entryIndex, Delegate hook, MethodBase target, IlBody body,
        bool isPrefix, LocalBuilder lState, LocalBuilder? lResult, LocalBuilder lSkip)
    {
        var hookMethod = hook.Method; // the invoked method (may be a closure instance method)
        var invoke = hook.GetType().GetMethod("Invoke")!;
        var hookParams = hookMethod.GetParameters();
        if (hookParams.Length == 0)
        {
            hookParams = invoke.GetParameters(); // closure methods report params on Invoke
        }
        var returnType = invoke.ReturnType;

        // Load the delegate from the bridge FIRST (callvirt stack order: delegate, args...).
        il.Emit(OpCodes.Ldsfld, bridgeField);        // PatchEntry[][]
        il.Emit(OpCodes.Ldc_I4, bridgeIndex);
        il.Emit(OpCodes.Ldelem_Ref);                 // PatchEntry[]
        il.Emit(OpCodes.Ldc_I4, entryIndex);
        il.Emit(OpCodes.Ldelem_Ref);                 // PatchEntry
        il.Emit(OpCodes.Ldfld, typeof(PatchEntry).GetField(isPrefix ? nameof(PatchEntry.Prefix) : nameof(PatchEntry.Postfix))!); // Delegate
        il.Emit(OpCodes.Castclass, hook.GetType());  // concrete delegate type

        // Then the arguments.
        foreach (var hp in hookParams)
        {
            EmitHookArgument(il, hp, hook, target, body, isPrefix, lState, lResult);
        }

        il.Emit(OpCodes.Callvirt, invoke);

        // Consume return: a prefix returning bool(false) skips the original.
        if (returnType == typeof(bool))
        {
            if (!isPrefix)
            {
                throw new InvalidOperationException($"postfix must return void: {hook}");
            }
            // skip |= !result
            var lblKeepGoing = il.DefineLabel();
            il.Emit(OpCodes.Brtrue, lblKeepGoing);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stloc, lSkip);
            il.MarkLabel(lblKeepGoing);
        }
        else if (returnType != typeof(void))
        {
            throw new InvalidOperationException($"hook must return void or bool: {hook}");
        }
    }

    /// <summary>Emits the load for one hook parameter following name conventions.</summary>
    private static void EmitHookArgument(
        ILGenerator il, ParameterInfo hp, Delegate hook, MethodBase target, IlBody body,
        bool isPrefix, LocalBuilder lState, LocalBuilder? lResult)
    {
        var name = hp.Name ?? "";
        var ptype = hp.ParameterType;
        var parameterTypes = body.ParameterTypes;
        var targetParams = target.GetParameters();

        if (name == StateName)
        {
            // __state: prefix declares out/ref object; postfix declares ref object.
            EmitLdargForState(il, ptype, lState, isPrefix, hook);
            return;
        }
        if (name == ResultName)
        {
            if (isPrefix || lResult is null)
            {
                throw new InvalidOperationException($"__result is only valid on postfix of a value-returning method: {hook}");
            }
            EmitLdargForResult(il, body.ReturnType, lResult, ptype, hook);
            return;
        }
        if (name == InstanceName)
        {
            if (!body.IsInstance)
            {
                throw new InvalidOperationException($"__instance on a static target: {hook}");
            }
            il.Emit(OpCodes.Ldarg_0); // this (managed pointer for struct targets)
            if (body.DeclaringType is { IsValueType: true } dt)
            {
                // Struct this arrives as a pointer; hooks observe a boxed copy.
                // (Mutations to __instance are not written back.)
                il.Emit(OpCodes.Ldobj, dt);
                if (ptype == typeof(object))
                {
                    il.Emit(OpCodes.Box, dt);
                    return;
                }
            }
            if (ptype != typeof(object) && ptype != body.DeclaringType)
            {
                throw new InvalidOperationException($"__instance type mismatch on {hook}: expected {body.DeclaringType} or object");
            }
            return;
        }
        if (name == ArgsName)
        {
            EmitLdargForArgs(il, body, ptype, hook);
            return;
        }

        // Otherwise: match by parameter name against the target's parameters.
        int idx = FindParamIndex(targetParams, name);
        if (idx < 0)
        {
            throw new InvalidOperationException($"hook parameter '{name}' on {hook} does not match any target parameter or convention name");
        }

        var targetType = parameterTypes[idx];
        bool hookIsByRef = ptype.IsByRef;
        var hookElem = hookIsByRef ? ptype.GetElementType()! : ptype;
        int argIdx = body.IsInstance ? idx + 1 : idx;

        if (hookIsByRef)
        {
            throw new NotSupportedException(
                $"ref hook parameter '{name}' is not yet supported (by-value only): {hook}");
        }

        EmitLdargN(il, argIdx);
        if (hookElem != targetType && !(hookElem == typeof(object) && !targetType.IsValueType))
        {
            throw new InvalidOperationException($"hook parameter '{name}' type {ptype} is not compatible with target parameter {targetType}: {hook}");
        }
    }

    private static void EmitLdargForState(ILGenerator il, Type ptype, LocalBuilder lState, bool isPrefix, Delegate hook)
    {
        // __state on prefix: out object (or ref object) — pass address of the state local.
        // On postfix: ref object — same address.
        if (ptype != typeof(object).MakeByRefType())
        {
            throw new InvalidOperationException($"__state must be 'out object' (prefix) or 'ref object' (postfix): {hook}");
        }
        il.Emit(OpCodes.Ldloca, lState);
    }

    private static void EmitLdargForResult(ILGenerator il, Type returnType, LocalBuilder lResult, Type ptype, Delegate hook)
    {
        bool byRef = ptype.IsByRef;
        var elem = byRef ? ptype.GetElementType()! : ptype;
        if (byRef)
        {
            if (elem != returnType && !(elem == typeof(object) && returnType.IsValueType))
            {
                throw new InvalidOperationException($"__result ref type {elem} does not match return type {returnType}: {hook}");
            }
            il.Emit(OpCodes.Ldloca, lResult);
        }
        else
        {
            if (elem != returnType)
            {
                throw new InvalidOperationException($"__result type {elem} does not match return type {returnType}: {hook}");
            }
            il.Emit(OpCodes.Ldloc, lResult);
        }
    }

    private static void EmitLdargForArgs(ILGenerator il, IlBody body, Type ptype, Delegate hook)
    {
        if (ptype != typeof(object[]))
        {
            throw new InvalidOperationException($"__args must be object[]: {hook}");
        }
        int total = body.ParameterTypes.Length + (body.IsInstance ? 1 : 0);
        il.Emit(OpCodes.Ldc_I4, total);
        il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < total; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            EmitLdargN(il, i);
            var t = body.IsInstance && i == 0
                ? body.DeclaringType!
                : body.ParameterTypes[i - (body.IsInstance ? 1 : 0)];
            if (body.IsInstance && i == 0 && t.IsValueType)
            {
                il.Emit(OpCodes.Ldobj, t); // struct this is a pointer; dereference first
            }
            if (t.IsValueType)
            {
                il.Emit(OpCodes.Box, t);
            }
            il.Emit(OpCodes.Stelem_Ref);
        }
    }

    private static int FindParamIndex(ParameterInfo[] targetParams, string name)
    {
        for (int i = 0; i < targetParams.Length; i++)
        {
            if (targetParams[i].Name == name)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Emits ldarg for the given index (supports up to 255 args via ldarg).</summary>
    internal static void EmitLdargN(ILGenerator il, int index)
    {
        switch (index)
        {
            case 0: il.Emit(OpCodes.Ldarg_0); break;
            case 1: il.Emit(OpCodes.Ldarg_1); break;
            case 2: il.Emit(OpCodes.Ldarg_2); break;
            case 3: il.Emit(OpCodes.Ldarg_3); break;
            default:
                if (index <= 255)
                {
                    il.Emit(OpCodes.Ldarg_S, (byte)index);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg, (short)index);
                }
                break;
        }
    }

    /// <summary>Hooks that inject prefix/postfix into the re-emitted body.</summary>
    private sealed class PatchHooks : IlEmitHooks
    {
        private readonly FieldInfo _bridgeField;
        private readonly int _bridgeIndex;
        private readonly IReadOnlyList<PatchEntry> _entries;
        private readonly MethodBase _target;
        private readonly IlBody _body;
        private readonly LocalBuilder _lState;
        private readonly LocalBuilder? _lResult;
        private readonly LocalBuilder _lSkip;
        private readonly Label _lblSkipped;
        private bool _boundSkipped;

        public PatchHooks(
            FieldInfo bridgeField, int bridgeIndex, IReadOnlyList<PatchEntry> entries, MethodBase target, IlBody body,
            LocalBuilder lState, LocalBuilder? lResult, LocalBuilder lSkip,
            Label lblSkipped)
        {
            _bridgeField = bridgeField;
            _bridgeIndex = bridgeIndex;
            _entries = entries;
            _target = target;
            _body = body;
            _lState = lState;
            _lResult = lResult;
            _lSkip = lSkip;
            _lblSkipped = lblSkipped;
        }

        public void EmitPrologue(ILGenerator il, IlEmitContext ctx)
        {
            // Prefix chain is emitted by Build() before EmitInto; nothing to add here.
        }

        public bool RewriteRet(ILGenerator il, IlInstruction ret, IlEmitContext ctx)
        {
            // This ret is an original `ret`. Replace it with:
            //   [store the value the ret would have returned (value-returning methods)]
            //   [postfix chain]
            //   ret (or load result + ret)
            //
            // The skipped branch (prefix returned false) lands at lblSkipped, placed AFTER
            // the store so the empty stack is valid (lResult already holds the default).

            // The value on the stack at the ret is the method result — capture it first.
            if (_lResult is not null)
            {
                il.Emit(OpCodes.Stloc, _lResult);
            }

            if (!_boundSkipped)
            {
                il.MarkLabel(_lblSkipped);
                _boundSkipped = true;
            }

            // Postfixes (in reverse entry order so the chain unwinds like Harmony).
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var postfix = _entries[i].Postfix;
                if (postfix is null)
                {
                    continue;
                }
                EmitDelegateCall(il, _bridgeField, _bridgeIndex, i, postfix, _target, _body, isPrefix: false, _lState, _lResult, _lSkip);
            }

            if (_lResult is not null)
            {
                il.Emit(OpCodes.Ldloc, _lResult);
            }
            il.Emit(OpCodes.Ret);
            return true;
        }
    }
}
