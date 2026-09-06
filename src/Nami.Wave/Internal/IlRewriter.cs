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
/// stable native entry (unlike DynamicMethod, whose body address is not resolvable) — which
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
    /// declaring type as parameter 0 — arg0 = this, matching the target's entry).
    /// </summary>
    public static (TypeBuilder Type, MethodBuilder Method, ILGenerator Il) BeginGeneratedMethod(IlBody b, string name)
    {
        if (b.IsInstance && b.DeclaringType is { IsValueType: true })
        {
            throw new InvalidOperationException($"Wave cannot patch struct instance method {b.Method}");
        }

        var fullParams = b.ParameterTypes;
        if (b.IsInstance && b.DeclaringType is { } dt)
        {
            fullParams = new[] { dt }.Concat(fullParams).ToArray();
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
    /// runtime recognizes it by full name even when defined dynamically — the standard
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
    /// hooks. The caller must have created the DynamicMethod and declared NO locals yet —
    /// locals are declared here (needed for correct init flags) and exposed via the context.
    /// </summary>
    public static void EmitInto(ILGenerator il, IlBody b, IlEmitHooks? hooks)
    {
        // Locals: re-declare with the original types, preserving indices.
        var locals = new LocalBuilder[b.Locals.Count];
        for (int i = 0; i < b.Locals.Count; i++)
        {
            var lv = b.Locals[i];
            locals[i] = il.DeclareLocal(lv.LocalType, lv.IsPinned);
        }

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
                throw new InvalidOperationException(
                    $"Wave cannot copy filter exception clauses ({b.Method})");
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
        // synthesized by EndExceptionBlock, so the copied one must be dropped.
        var handlerRegionEnds = new HashSet<int>();
        foreach (var c in clauses)
        {
            handlerRegionEnds.Add(c.HandlerOffset + c.HandlerLength);
        }

        // Whether an offset lies inside a try region (ret rewriting must respect EH: a ret
        // inside a try cannot simply branch out; but valid C# never emits ret in a try, and
        // a ret in a handler cannot branch to outside code — so rets inside handler regions
        // are rewritten in place by hooks that know this).
        var handlerStarts = new HashSet<int>();
        foreach (var c in clauses)
        {
            handlerStarts.Add(c.HandlerOffset);
        }

        var module = b.Method.Module;
        var declaringType = b.DeclaringType;
        var genericArgs = declaringType?.IsGenericType == true ? declaringType.GetGenericArguments() : Type.EmptyTypes;
        var genericMethodArgs = b.Method.IsGenericMethod ? b.Method.GetGenericArguments() : Type.EmptyTypes;

        // Optional hook prelude (prefix chain) — outside any EH region.
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

    private static void Emit(ILGenerator il, IlInstruction ins, Module module, Type? declaringType,
        Type[] genericArgs, Type[] genericMethodArgs, LocalBuilder[] locals, Func<int, Label> labelFor)
    {
        var op = ins.OpCode;
        var operand = ins.Operand;

        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                il.Emit(op);
                return;
            case OperandType.ShortInlineBrTarget:
            case OperandType.InlineBrTarget:
                il.Emit(op, labelFor((int)operand!));
                return;
            case OperandType.InlineSwitch:
            {
                var targets = (int[])operand!;
                var labels = new Label[targets.Length];
                for (int i = 0; i < targets.Length; i++)
                {
                    labels[i] = labelFor(targets[i]);
                }
                il.Emit(op, labels);
                return;
            }
            case OperandType.ShortInlineVar:
                il.Emit(op, locals[ToInt(operand!)]);
                return;
            case OperandType.InlineVar:
                il.Emit(op, locals[ToInt(operand!)]);
                return;
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
                il.Emit(op, (string)module.ResolveString((int)operand!)!);
                return;
            case OperandType.InlineField:
            {
                var field = module.ResolveField((int)operand!, genericArgs, genericMethodArgs)
                    ?? throw new InvalidOperationException($"cannot resolve field token {(int)operand!:X8}");
                il.Emit(op, field);
                return;
            }
            case OperandType.InlineMethod:
            {
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
                var type = module.ResolveType((int)operand!, genericArgs, genericMethodArgs)
                    ?? throw new InvalidOperationException($"cannot resolve type token {(int)operand!:X8}");
                il.Emit(op, type);
                return;
            }
            case OperandType.InlineTok:
            {
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
                throw new InvalidOperationException($"Wave cannot copy calli at {ins.Offset:X4}");
            default:
                throw new InvalidOperationException($"cannot emit {op}");
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
