using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nami.Wave.Internal;

/// <summary>
/// Decoded shape of a method body: instructions, locals, exception clauses, signature.
/// </summary>
internal sealed class IlBody
{
    public required List<IlInstruction> Instructions;
    public required IList<LocalVariableInfo> Locals;
    public required IList<ExceptionHandlingClause> Clauses;
    public required MethodBase Method;
    public required Type ReturnType;
    public required Type[] ParameterTypes; // target parameter types (this excluded)
    public required bool IsInstance;
    public required Type? DeclaringType;
}

/// <summary>
/// Hooks for re-emitting a method body into a DynamicMethod.
/// </summary>
internal interface IlEmitHooks
{
    /// <summary>Emitted before the first instruction (outside any EH region).</summary>
    void EmitPrologue(ILGenerator il, IlEmitContext ctx);

    /// <summary>
    /// Called when a `ret` instruction is reached. Return true if the instruction was
    /// consumed (the hook emitted replacement IL); false to emit the ret normally.
    /// </summary>
    bool RewriteRet(ILGenerator il, IlInstruction ret, IlEmitContext ctx);
}

/// <summary>State shared between the re-emitter and hooks.</summary>
internal sealed class IlEmitContext
{
    public required IlBody Body;
    public required LocalBuilder[] Locals;         // indexed by original local number
    public required Dictionary<int, Label> Labels; // original offset -> label
    public required Func<int, Label> LabelFor;
}

/// <summary>
/// Re-emits a method's IL into a method on a generated type in a dynamic assembly,
/// preserving semantics: same locals, same exception-handler structure, same branch graph,
/// tokens resolved from the original method's module. The generated type is a public static
/// class in its own dynamic assembly, so the emitted method has a REAL MethodHandle and a
/// stable native entry (unlike DynamicMethod, whose body address is not resolvable) - which
/// makes it a safe detour target.
/// </summary>
internal static class IlRewriter
{
    /// <summary>Keeps generated assemblies alive (a dynamic assembly is collected when unreferenced).</summary>
    private static readonly Lock AssembliesLock = new();
    private static readonly List<Assembly> Assemblies = new();
    private static int s_assemblySeq;

    public static IlBody Analyze(MethodBase original)
    {
        var body = original.GetMethodBody()
            ?? throw new InvalidOperationException($"no body for {original}");
        var instructions = IlReader.Read(original);
        if (instructions.Count == 0)
        {
            throw new InvalidOperationException($"no IL body to copy for {original}");
        }

        var parameters = original.GetParameters();
        var paramTypes = parameters.Select(p => p.ParameterType).ToArray();
        Type returnType = original is MethodInfo mi ? mi.ReturnType : typeof(void);

        return new IlBody
        {
            Instructions = instructions,
            Locals = body.LocalVariables,
            Clauses = body.ExceptionHandlingClauses,
            Method = original,
            ReturnType = returnType,
            ParameterTypes = paramTypes,
            IsInstance = !original.IsStatic,
            DeclaringType = original.DeclaringType,
        };
    }

    /// <summary>
    /// Starts a generated method: a public static method on a fresh type in a fresh dynamic
    /// assembly, whose parameters mirror the body's native layout (instance targets take the
    /// declaring type as parameter 0 - arg0 = this, matching the target's entry; struct
    /// instance targets take it by reference, matching the managed-pointer this of value
    /// types - mutations through it behave exactly like the original).
    /// </summary>
    public static (TypeBuilder Type, MethodBuilder Method, ILGenerator Il) BeginGeneratedMethod(IlBody b, string name)
    {
        var fullParams = b.ParameterTypes;
        if (b.IsInstance && b.DeclaringType is { } dt)
        {
            // Struct this is already a managed pointer in the original IL (ldarg.0), so a
            // byref parameter carries the identical value - no copy, no writeback gap.
            var thisParam = dt.IsValueType ? dt.MakeByRefType() : dt;
            fullParams = new[] { thisParam }.Concat(fullParams).ToArray();
        }

        int seq = Interlocked.Increment(ref s_assemblySeq);
        var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Wave.Gen_{seq}"), AssemblyBuilderAccess.Run);
        var mb = ab.DefineDynamicModule("main");

        // Allow the generated assembly to access non-public members of every assembly its
        // IL references (the target's assembly, Nami.Wave internals, the host's assembly).
        // This mirrors how Harmony emits patch assemblies. The attribute type is not present
        // in the runtime, so we define it in the assembly's single dynamic module first.
        var ignoresType = DefineIgnoresAccessChecksToAttribute(ab, mb);
        ApplyIgnoresAccessChecksTo(ab, ignoresType, typeof(IlRewriter).Assembly);
        var targetAssembly = b.Method.Module.Assembly;
        if (targetAssembly != typeof(IlRewriter).Assembly)
        {
            ApplyIgnoresAccessChecksTo(ab, ignoresType, targetAssembly);
        }

        var tb = mb.DefineType($"Wave.Gen_{seq}", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var method = tb.DefineMethod(name,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            CallingConventions.Standard,
            b.ReturnType,
            fullParams);

        lock (AssembliesLock)
        {
            Assemblies.Add(ab);
        }

        return (tb, method, method.GetILGenerator());
    }

    /// <summary>
    /// Defines the runtime's IgnoresAccessChecksToAttribute in the dynamic module (the
    /// runtime recognizes it by full name even when defined dynamically - the standard
    /// technique used by Harmony and MonoMod).
    /// </summary>
    private static Type DefineIgnoresAccessChecksToAttribute(AssemblyBuilder ab, ModuleBuilder mb)
    {
        const string attrName = "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute";
        var tb = mb.DefineType(attrName,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));
        var ctor = tb.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard, new[] { typeof(string) });
        var ilg = ctor.GetILGenerator();
        ilg.Emit(OpCodes.Ldarg_0);
        ilg.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)!);
        ilg.Emit(OpCodes.Ret);
        _ = ab;
        return tb.CreateType();
    }

    /// <summary>Applies an IgnoresAccessChecksTo attribute instance for the given assembly.</summary>
    private static void ApplyIgnoresAccessChecksTo(AssemblyBuilder ab, Type attrType, Assembly target)
    {
        ab.SetCustomAttribute(new CustomAttributeBuilder(
            attrType.GetConstructor(new[] { typeof(string) })!,
            new object[] { target.GetName().Name! }));
    }

    /// <summary>Creates a clean copy of the original body (no injections).</summary>
    public static MethodInfo CopyBody(MethodBase original, string name)
    {
        var b = Analyze(original);
        var (tb, method, il) = BeginGeneratedMethod(b, name);
        EmitInto(il, b, null);
        var t = tb.CreateType();
        return t.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
    }

    /// <summary>
    /// Emits <paramref name="b"/>'s body into <paramref name="il"/> applying the optional
    /// hooks. The caller must have created the DynamicMethod and declared NO locals yet -
    /// locals are declared here (needed for correct init flags) and exposed via the context.
    /// </summary>
    public static void EmitInto(ILGenerator il, IlBody b, IlEmitHooks? hooks)
    {
        var declaringType = b.DeclaringType;
        var genericArgs = declaringType?.IsGenericType == true ? declaringType.GetGenericArguments() : Type.EmptyTypes;
        var genericMethodArgs = b.Method.IsGenericMethod ? b.Method.GetGenericArguments() : Type.EmptyTypes;

        // Locals: re-declare with the original types, preserving indices.
        var locals = DeclareLocals(il, b);

        // Labels for every instruction; branches resolve to these.
        var labels = new Dictionary<int, Label>();
        Label LabelFor(int offset)
        {
            if (!labels.TryGetValue(offset, out var lbl))
            {
                lbl = il.DefineLabel();
                labels[offset] = lbl;
            }
            return lbl;
        }

        var ctx = new IlEmitContext { Body = b, Locals = locals, Labels = labels, LabelFor = LabelFor };

        // Structural EH actions keyed by IL offset (see AddAction).
        var clauses = b.Clauses;
        var actionsAt = new SortedDictionary<int, List<Action<ILGenerator>>>();

        void AddAction(int offset, Action<ILGenerator> a)
        {
            if (!actionsAt.TryGetValue(offset, out var list))
            {
                list = actionsAt[offset] = new List<Action<ILGenerator>>();
            }
            list.Add(a);
        }

        foreach (var c in clauses)
        {
            int tryStart = c.TryOffset;
            int handlerStart = c.HandlerOffset;
            int handlerEnd = c.HandlerOffset + c.HandlerLength;
            var flags = c.Flags;

            if (flags == ExceptionHandlingClauseOptions.Filter)
            {
                // Filter block runs from FilterOffset to the handler; the filter body
                // itself is copied verbatim (endfilter dropped like endfinally below).
                // Note: CatchType throws on filter clauses, so catch object - the
                // filter itself gates entry.
                AddAction(tryStart, ilg => ilg.BeginExceptionBlock());
                AddAction(c.FilterOffset, ilg => ilg.BeginExceptFilterBlock());
                // No exception type: the filter decides entry (passing one throws).
                AddAction(handlerStart, ilg => ilg.BeginCatchBlock(null));
                AddAction(handlerEnd, ilg => ilg.EndExceptionBlock());
                continue;
            }

            AddAction(tryStart, ilg => ilg.BeginExceptionBlock());
            AddAction(handlerStart, ilg =>
            {
                switch (flags)
                {
                    case ExceptionHandlingClauseOptions.Clause:
                        ilg.BeginCatchBlock(c.CatchType ?? typeof(object));
                        break;
                    case ExceptionHandlingClauseOptions.Finally:
                        ilg.BeginFinallyBlock();
                        break;
                    case ExceptionHandlingClauseOptions.Fault:
                        ilg.BeginFaultBlock();
                        break;
                }
            });
            AddAction(handlerEnd, ilg => ilg.EndExceptionBlock());
        }

        // Handler terminator filtering: `endfinally` that closes a finally/fault handler is
        // synthesized by EndExceptionBlock, so the copied one must be dropped. Same for
        // `endfilter`, synthesized by BeginExceptFilterBlock.
        var handlerRegionEnds = new HashSet<int>();
        var filterHandlerStarts = new HashSet<int>();
        foreach (var c in clauses)
        {
            handlerRegionEnds.Add(c.HandlerOffset + c.HandlerLength);
            if (c.Flags == ExceptionHandlingClauseOptions.Filter)
            {
                filterHandlerStarts.Add(c.HandlerOffset);
            }
        }

        // Whether an offset lies inside a try region (ret rewriting must respect EH: a ret
        // inside a try cannot simply branch out; but valid C# never emits ret in a try, and
        // a ret in a handler cannot branch to outside code - so rets inside handler regions
        // are rewritten in place by hooks that know this).
        var handlerStarts = new HashSet<int>();
        foreach (var c in clauses)
        {
            handlerStarts.Add(c.HandlerOffset);
        }

        var module = b.Method.Module;

        // Optional hook prelude (prefix chain) - outside any EH region.
        hooks?.EmitPrologue(il, ctx);

        // Walk instructions in offset order, applying structural actions when their offset is
        // reached.
        var actionOffsets = actionsAt.Keys.ToList();
        int actionIdx = 0;

        foreach (var ins in b.Instructions)
        {
            while (actionIdx < actionOffsets.Count && actionOffsets[actionIdx] <= ins.Offset)
            {
                foreach (var a in actionsAt[actionOffsets[actionIdx]])
                {
                    a(il);
                }
                actionIdx++;
            }

            il.MarkLabel(LabelFor(ins.Offset));

            if (ins.OpCode == OpCodes.Endfinally)
            {
                int nextOffset = ins.Offset + ins.OpCode.Size;
                if (handlerRegionEnds.Contains(nextOffset))
                {
                    continue; // synthesized by EndExceptionBlock
                }
            }

            if (ins.OpCode == OpCodes.Endfilter)
            {
                int nextOffset = ins.Offset + ins.OpCode.Size;
                if (filterHandlerStarts.Contains(nextOffset))
                {
                    continue; // synthesized by BeginExceptFilterBlock
                }
            }

            if (ins.OpCode == OpCodes.Ret && hooks is not null)
            {
                if (hooks.RewriteRet(il, ins, ctx))
                {
                    continue;
                }
            }

            Emit(il, ins, module, declaringType, genericArgs, genericMethodArgs, locals, LabelFor);
        }

        // Remaining actions (handler ending at end of IL).
        while (actionIdx < actionOffsets.Count)
        {
            foreach (var a in actionsAt[actionOffsets[actionIdx]])
            {
                a(il);
            }
            actionIdx++;
        }
    }

    /// <summary>Re-declares the body's original locals on <paramref name="il"/>, preserving indices
    /// (shared by the offset-driven and transpiler-node emitters).</summary>
    internal static LocalBuilder[] DeclareLocals(ILGenerator il, IlBody b)
    {
        var declaringType = b.DeclaringType;
        var genericArgs = declaringType?.IsGenericType == true ? declaringType.GetGenericArguments() : Type.EmptyTypes;
        var genericMethodArgs = b.Method.IsGenericMethod ? b.Method.GetGenericArguments() : Type.EmptyTypes;
        var locals = new LocalBuilder[b.Locals.Count];
        for (int i = 0; i < b.Locals.Count; i++)
        {
            var lv = b.Locals[i];
            locals[i] = il.DeclareLocal(MapLocalType(lv.LocalType, b, genericArgs, genericMethodArgs), lv.IsPinned);
        }
        return locals;
    }

    /// <summary>
    /// Emits one instruction. <paramref name="labelFor"/> resolves raw branch-target offsets
    /// (the offset-driven path); on the transpiler path branch operands are labels handled
    /// by the caller, and operands may already be RESOLVED (string/Type/MemberInfo injected
    /// by a transpiler) - those are emitted directly without token resolution.
    /// </summary>
    internal static void Emit(ILGenerator il, IlInstruction ins, Module module, Type? declaringType,
        Type[] genericArgs, Type[] genericMethodArgs, LocalBuilder[] locals, Func<int, Label>? labelFor = null)
    {
        var op = ins.OpCode;
        var operand = ins.Operand;

        // Short-form local loads/stores (ldloc.0-3/stloc.0-3) are InlineNone with the
        // slot baked into the opcode - remap through the real builders (same verbatim
        // trap as the .s forms below). Short ldarg.0-3 need nothing: the argument
        // layout is preserved (this stays arg 0).
        int v = op.Value;
        if (v >= 0x06 && v <= 0x09)
        {
            EmitLocalVar(il, op, locals[v - 0x06]);
            return;
        }
        if (v >= 0x0A && v <= 0x0D)
        {
            EmitLocalVar(il, op, locals[v - 0x0A]);
            return;
        }

        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                il.Emit(op);
                return;
            case OperandType.ShortInlineBrTarget:
            case OperandType.InlineBrTarget:
                if (operand is Label resolved)
                {
                    il.Emit(op, resolved);
                }
                else if (labelFor is not null)
                {
                    il.Emit(op, labelFor((int)operand!));
                }
                else
                {
                    throw new InvalidOperationException($"raw branch target on {op} without a label resolver");
                }
                return;
            case OperandType.InlineSwitch:
            {
                if (operand is Label[] resolvedSwitch)
                {
                    il.Emit(op, resolvedSwitch);
                    return;
                }
                var targets = (int[])operand!;
                var labels = new Label[targets.Length];
                for (int i = 0; i < targets.Length; i++)
                {
                    labels[i] = labelFor!(targets[i]);
                }
                il.Emit(op, labels);
                return;
            }
            case OperandType.ShortInlineVar:
            case OperandType.InlineVar:
            {
                // ponytail: ILGenerator.Emit bakes short-form local opcodes VERBATIM,
                // ignoring the LocalBuilder's real index - and Wave's prologue locals
                // shift every original index. Select every var form explicitly.
                int slot = ToInt(operand!);
                if (IsArgVarOp(op))
                {
                    EmitArgVar(il, op, slot);
                }
                else
                {
                    EmitLocalVar(il, op, locals[slot]);
                }
                return;
            }
            case OperandType.InlineI:
                il.Emit(op, ToInt(operand!));
                return;
            case OperandType.ShortInlineI:
                il.Emit(op, (sbyte)ToInt(operand!));
                return;
            case OperandType.InlineI8:
                il.Emit(op, (long)operand!);
                return;
            case OperandType.ShortInlineR:
                il.Emit(op, (float)operand!);
                return;
            case OperandType.InlineR:
                il.Emit(op, (double)operand!);
                return;
            case OperandType.InlineString:
                if (operand is string s)
                {
                    il.Emit(op, s); // transpiler-injected literal
                }
                else
                {
                    il.Emit(op, (string)module.ResolveString((int)operand!)!);
                }
                return;
            case OperandType.InlineField:
            {
                if (operand is FieldInfo knownField)
                {
                    il.Emit(op, knownField); // transpiler-injected reference
                    return;
                }
                var field = module.ResolveField((int)operand!, genericArgs, genericMethodArgs)
                    ?? throw new InvalidOperationException($"cannot resolve field token {(int)operand!:X8}");
                il.Emit(op, field);
                return;
            }
            case OperandType.InlineMethod:
            {
                if (operand is MethodInfo knownMethod)
                {
                    il.Emit(op, knownMethod); // transpiler-injected reference
                    return;
                }
                if (operand is ConstructorInfo knownCtor)
                {
                    il.Emit(op, knownCtor);
                    return;
                }
                var method = module.ResolveMethod((int)operand!, genericArgs, genericMethodArgs)
                    ?? throw new InvalidOperationException($"cannot resolve method token {(int)operand!:X8}");
                switch (method)
                {
                    case MethodInfo mi2:
                        il.Emit(op, mi2);
                        break;
                    case ConstructorInfo ci:
                        il.Emit(op, ci);
                        break;
                    default:
                        throw new InvalidOperationException($"cannot emit method {method}");
                }
                return;
            }
            case OperandType.InlineType:
            {
                if (operand is Type knownType)
                {
                    il.Emit(op, knownType); // transpiler-injected reference
                    return;
                }
                var type = module.ResolveType((int)operand!, genericArgs, genericMethodArgs)
                    ?? throw new InvalidOperationException($"cannot resolve type token {(int)operand!:X8}");
                il.Emit(op, type);
                return;
            }
            case OperandType.InlineTok:
            {
                if (operand is Type or FieldInfo or MethodInfo or ConstructorInfo)
                {
                    // Transpiler-injected member reference: same emission as the resolved path.
                    switch (operand)
                    {
                        case Type t:
                            il.Emit(op, t);
                            break;
                        case FieldInfo f:
                            il.Emit(op, f);
                            break;
                        case MethodInfo m:
                            il.Emit(op, m);
                            break;
                        case ConstructorInfo c:
                            il.Emit(op, c);
                            break;
                    }
                    return;
                }
                var member = module.ResolveMember((int)operand!, genericArgs, genericMethodArgs)
                    ?? throw new InvalidOperationException($"cannot resolve member token {(int)operand!:X8}");
                switch (member)
                {
                    case Type t:
                        il.Emit(op, t);
                        break;
                    case FieldInfo f:
                        il.Emit(op, f);
                        break;
                    case MethodInfo m:
                        il.Emit(op, m);
                        break;
                    case ConstructorInfo c:
                        il.Emit(op, c);
                        break;
                    default:
                        throw new InvalidOperationException($"cannot emit token for {member}");
                }
                return;
            }
            case OperandType.InlineSig:
            {
                // calli: resolve the call-site signature and re-emit it. Only calli
                // carries InlineSig; anything else here is a reader bug, not a body.
                if (op != OpCodes.Calli)
                {
                    throw new InvalidOperationException($"unexpected signatures operand on {op} at {ins.Offset:X4}");
                }
                var sig = CalliSignatureParser.Parse(module, (int)operand!, genericArgs, genericMethodArgs);
                if (sig.Unmanaged)
                {
                    il.EmitCalli(op, sig.UnmanagedConvention, sig.ReturnType, sig.ParameterTypes);
                }
                else
                {
                    // Managed calli has no EmitCalli overload; a SignatureHelper
                    // carries the same calling convention + shape instead.
                    var helper = SignatureHelper.GetMethodSigHelper(sig.ManagedConvention, sig.ReturnType);
                    foreach (var p in sig.ParameterTypes)
                    {
                        helper.AddArgument(p);
                    }
                    il.Emit(op, helper);
                }
                return;
            }
            default:
                throw new InvalidOperationException($"cannot emit {op}");
        }
    }

    /// <summary>
    /// Maps an original local type to a declarable one. Two shapes have no nameable
    /// runtime Type: function pointers (redeclared as IntPtr - identical native-int
    /// size and stack representation, all the copied IL observes) and generic
    /// parameters (substituted from the closed context - the patched method is always
    /// closed by the time its body is copied).
    /// </summary>
    private static Type MapLocalType(Type? t, IlBody b, Type[] genericArgs, Type[] genericMethodArgs)
    {
        if (t is null || t.IsFunctionPointer)
        {
            return typeof(IntPtr);
        }
        if (t.IsGenericParameter)
        {
            var args = t.DeclaringMethod is not null ? genericMethodArgs : genericArgs;
            if ((uint)t.GenericParameterPosition < (uint)args.Length)
            {
                return args[t.GenericParameterPosition];
            }
            throw new InvalidOperationException($"cannot map generic local type {t} in {b.Method}");
        }
        return t;
    }

    private static bool IsArgVarOp(OpCode op) =>
        op.Equals(OpCodes.Ldarg) || op.Equals(OpCodes.Ldarg_S) ||
        op.Equals(OpCodes.Ldarga) || op.Equals(OpCodes.Ldarga_S) ||
        op.Equals(OpCodes.Starg) || op.Equals(OpCodes.Starg_S);

    /// <summary>
    /// Re-emits an argument load/store/address with the SAME slot: the generated
    /// method preserves the argument layout (`this` stays arg 0), so no remapping.
    /// </summary>
    private static void EmitArgVar(ILGenerator il, OpCode op, int slot)
    {
        bool store = op.Equals(OpCodes.Starg) || op.Equals(OpCodes.Starg_S);
        bool addr = op.Equals(OpCodes.Ldarga) || op.Equals(OpCodes.Ldarga_S);
        if (addr)
        {
            if (slot <= 255)
            {
                il.Emit(OpCodes.Ldarga_S, (byte)slot);
            }
            else
            {
                il.Emit(OpCodes.Ldarga, (short)slot);
            }
        }
        else if (store)
        {
            if (slot <= 255)
            {
                il.Emit(OpCodes.Starg_S, (byte)slot);
            }
            else
            {
                il.Emit(OpCodes.Starg, (short)slot);
            }
        }
        else if (slot <= 3)
        {
            switch (slot)
            {
                case 0: il.Emit(OpCodes.Ldarg_0); break;
                case 1: il.Emit(OpCodes.Ldarg_1); break;
                case 2: il.Emit(OpCodes.Ldarg_2); break;
                default: il.Emit(OpCodes.Ldarg_3); break;
            }
        }
        else if (slot <= 255)
        {
            il.Emit(OpCodes.Ldarg_S, (byte)slot);
        }
        else
        {
            il.Emit(OpCodes.Ldarg, (short)slot);
        }
    }

    /// <summary>
    /// Re-emits a local load/store/address against the builder's REAL index (the
    /// family - load/store/address - comes from the original opcode).
    /// </summary>
    private static void EmitLocalVar(ILGenerator il, OpCode op, LocalBuilder lb)
    {
        int index = lb.LocalIndex;
        bool store = op.Equals(OpCodes.Stloc) || op.Equals(OpCodes.Stloc_S) ||
            op.Equals(OpCodes.Stloc_0) || op.Equals(OpCodes.Stloc_1) ||
            op.Equals(OpCodes.Stloc_2) || op.Equals(OpCodes.Stloc_3);
        bool addr = op.Equals(OpCodes.Ldloca) || op.Equals(OpCodes.Ldloca_S);
        if (addr)
        {
            if (index <= 255)
            {
                il.Emit(OpCodes.Ldloca_S, (byte)index);
            }
            else
            {
                il.Emit(OpCodes.Ldloca, (short)index);
            }
        }
        else if (store)
        {
            if (index <= 3)
            {
                switch (index)
                {
                    case 0: il.Emit(OpCodes.Stloc_0); break;
                    case 1: il.Emit(OpCodes.Stloc_1); break;
                    case 2: il.Emit(OpCodes.Stloc_2); break;
                    default: il.Emit(OpCodes.Stloc_3); break;
                }
            }
            else if (index <= 255)
            {
                il.Emit(OpCodes.Stloc_S, (byte)index);
            }
            else
            {
                il.Emit(OpCodes.Stloc, (short)index);
            }
        }
        else if (index <= 3)
        {
            switch (index)
            {
                case 0: il.Emit(OpCodes.Ldloc_0); break;
                case 1: il.Emit(OpCodes.Ldloc_1); break;
                case 2: il.Emit(OpCodes.Ldloc_2); break;
                default: il.Emit(OpCodes.Ldloc_3); break;
            }
        }
        else if (index <= 255)
        {
            il.Emit(OpCodes.Ldloc_S, (byte)index);
        }
        else
        {
            il.Emit(OpCodes.Ldloc, (short)index);
        }
    }

    /// <summary>Unboxes a small-int operand (byte/sbyte/ushort/int) to int.</summary>
    private static int ToInt(object operand) => operand switch
    {
        int i => i,
        byte b => b,
        sbyte sb => sb,
        ushort us => us,
        short s => s,
        _ => throw new InvalidOperationException($"operand {operand} ({operand.GetType()}) is not a small int"),
    };
}
