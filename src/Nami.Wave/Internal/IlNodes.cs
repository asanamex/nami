using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Nami.Wave.Internal;

/// <summary>
/// The transpiler pipeline: converts an <see cref="IlBody"/> into a linked list of
/// <see cref="WaveIlInstruction"/> nodes that a <see cref="WaveIlCursor"/> edits, then emits
/// the edited list back into IL. Two invariants carry the design:
///
///   1. Branch operands become <see cref="WaveIlLabel"/>s anchored to NODES during
///      conversion — never raw offsets — so inserting or removing instructions anywhere
///      cannot redirect a jump (the list-index failure mode of Harmony-style transpilers).
///   2. Exception-handling boundaries are attached to nodes as structural actions at the
///      same relative position <see cref="IlRewriter.EmitInto"/> derives them from offsets,
///      so insertions before/inside try and handler regions keep the EH shape coherent
///      without recomputing offsets.
/// </summary>
internal static class IlNodes
{
    /// <summary>
    /// Converts a decoded body into a sentinel-bounded node list (Head/Tail sentinels;
    /// real nodes between). Branch and switch operands become labels anchored at their
    /// target node; EH clause offsets become per-node structural actions; handler-terminator
    /// endfinally/endfilter instructions are marked SkipEmit (synthesized by the builder).
    /// </summary>
    public static (WaveIlInstruction Head, WaveIlInstruction Tail) From(IlBody b)
    {
        var head = WaveIlInstruction.Sentinel();
        var tail = WaveIlInstruction.Sentinel();
        head.Next = tail;
        tail.Prev = head;

        // Pass 1: nodes in IL order.
        var byOffset = new Dictionary<int, WaveIlInstruction>();
        var last = head;
        foreach (var ins in b.Instructions)
        {
            var node = new WaveIlInstruction(ins.OpCode, ins.Operand, ins.Offset, null);
            Link(last, node);
            last = node;
            byOffset[ins.Offset] = node;
        }

        WaveIlInstruction NodeAt(int offset) =>
            byOffset.TryGetValue(offset, out var n) ? n : tail; // body-end targets anchor at the tail

        // Anchors a fresh label at the target node — the label MUST join the node's Anchored
        // list, that list is what the emitter marks.
        WaveIlLabel AnchorAt(int targetOffset)
        {
            var label = new WaveIlLabel();
            var anchor = NodeAt(targetOffset);
            label.Anchor = anchor;
            anchor.Anchored.Add(label);
            return label;
        }

        // Pass 2: branch operands → labels anchored at their target node.
        for (var n = head.Next!; !n.IsSentinel; n = n.Next!)
        {
            switch (n.OpCode.OperandType)
            {
                case OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget:
                    n.Operand = AnchorAt((int)n.Operand!);
                    break;
                case OperandType.InlineSwitch:
                {
                    var targets = (int[])n.Operand!;
                    var labels = new WaveIlLabel[targets.Length];
                    for (var i = 0; i < targets.Length; i++)
                    {
                        labels[i] = AnchorAt(targets[i]);
                    }
                    n.Operand = labels;
                    break;
                }
            }
        }

        // Pass 3: EH boundaries as structural actions, handler terminators marked SkipEmit —
        // mirroring IlRewriter.EmitInto's offset rules exactly.
        var clauses = b.Clauses;
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

        foreach (var c in clauses)
        {
            AddAction(NodeAt(c.TryOffset), ilg => ilg.BeginExceptionBlock());
            if (c.Flags == ExceptionHandlingClauseOptions.Filter)
            {
                // Filter block runs from FilterOffset to the handler; the filter body is
                // copied verbatim (endfilter is synthesized by BeginExceptFilterBlock).
                // No catch type: the filter itself gates entry (passing one throws).
                AddAction(NodeAt(c.FilterOffset), ilg => ilg.BeginExceptFilterBlock());
                AddAction(NodeAt(c.HandlerOffset), ilg => ilg.BeginCatchBlock(null));
            }
            else
            {
                AddAction(NodeAt(c.HandlerOffset), ilg =>
                {
                    switch (c.Flags)
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
            }
            AddAction(NodeAt(c.HandlerOffset + c.HandlerLength), ilg => ilg.EndExceptionBlock());
        }

        for (var n = head.Next!; !n.IsSentinel; n = n.Next!)
        {
            if (n.OpCode == OpCodes.Endfinally)
            {
                int next = n.OriginalOffset!.Value + OpCodes.Endfinally.Size;
                if (handlerRegionEnds.Contains(next))
                {
                    n.SkipEmit = true; // synthesized by EndExceptionBlock
                }
            }
            else if (n.OpCode == OpCodes.Endfilter)
            {
                int next = n.OriginalOffset!.Value + OpCodes.Endfilter.Size;
                if (filterHandlerStarts.Contains(next))
                {
                    n.SkipEmit = true; // synthesized by BeginExceptFilterBlock
                }
            }
        }

        return (head, tail);
    }

    private static void Link(WaveIlInstruction prev, WaveIlInstruction node)
    {
        node.Prev = prev;
        node.Next = prev.Next;
        prev.Next!.Prev = node;
        prev.Next = node;
    }

    private static void AddAction(WaveIlInstruction node, Action<ILGenerator> action)
    {
        node.Before ??= new List<Action<ILGenerator>>();
        node.Before.Add(action);
    }

    /// <summary>
    /// Emits the (transpiled) node list into <paramref name="il"/>: original locals are
    /// re-declared first (same contract as <see cref="IlRewriter.EmitInto"/>), labels are
    /// marked as their anchor node is reached, structural EH actions fire before their node,
    /// handler terminators are skipped, and every <c>ret</c> goes through the hooks (original
    /// AND injected — a transpiler-injected early return still runs the postfix chain).
    /// </summary>
    public static void Emit(ILGenerator il, IlBody b, WaveIlInstruction head, WaveIlInstruction tail, IlEmitHooks? hooks)
    {
        Validate(head);

        var declaringType = b.DeclaringType;
        var genericArgs = declaringType?.IsGenericType == true ? declaringType.GetGenericArguments() : Type.EmptyTypes;
        var genericMethodArgs = b.Method.IsGenericMethod ? b.Method.GetGenericArguments() : Type.EmptyTypes;
        var locals = IlRewriter.DeclareLocals(il, b);

        var ctx = new IlEmitContext
        {
            Body = b,
            Locals = locals,
            Labels = new Dictionary<int, Label>(),
            LabelFor = _ => throw new InvalidOperationException("offset labels are not used on the transpiler path"),
        };
        var labelMap = new Dictionary<WaveIlLabel, Label>();
        Label LabelOf(WaveIlLabel l)
        {
            if (!labelMap.TryGetValue(l, out var lbl))
            {
                lbl = il.DefineLabel();
                labelMap[l] = lbl;
            }
            return lbl;
        }

        for (var n = head.Next!; !n.IsSentinel; n = n.Next!)
        {
            if (n.Before is { Count: > 0 } before)
            {
                foreach (var a in before)
                {
                    a(il);
                }
            }

            foreach (var l in n.Anchored)
            {
                il.MarkLabel(LabelOf(l));
            }

            if (n.SkipEmit)
            {
                continue;
            }

            if (n.OpCode == OpCodes.Ret && hooks is not null)
            {
                var ins = new IlInstruction { Offset = n.OriginalOffset ?? 0, OpCode = n.OpCode, Operand = n.Operand };
                if (hooks.RewriteRet(il, ins, ctx))
                {
                    continue;
                }
            }

            EmitNode(il, n, b, declaringType, genericArgs, genericMethodArgs, locals, LabelOf);
        }

        // Structural actions anchored at the tail (handler ending at end of body).
        if (tail.Before is { Count: > 0 } trailing)
        {
            foreach (var a in trailing)
            {
                a(il);
            }
        }
        // Labels anchored at the tail are deliberately not defined: nothing may branch to
        // the end position (Validate rejects that), and no IL needs to be marked there.
    }

    /// <summary>Rejects, before any IL is emitted, the states that would silently corrupt a body:
    /// a branch to an unanchored label, or to the end position (no instruction there to land on).</summary>
    private static void Validate(WaveIlInstruction head)
    {
        for (var n = head.Next!; !n.IsSentinel; n = n.Next!)
        {
            switch (n.OpCode.OperandType)
            {
                case OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget:
                    Check((WaveIlLabel)n.Operand!, n);
                    break;
                case OperandType.InlineSwitch:
                    foreach (var l in (WaveIlLabel[])n.Operand!)
                    {
                        Check(l, n);
                    }
                    break;
            }
        }

        static void Check(WaveIlLabel l, WaveIlInstruction at)
        {
            if (!l.IsAnchored)
            {
                throw new Wave.HookException(
                    $"transpiled IL for a method branches to an unanchored label from {at} — anchor it with cursor.MarkLabel before the patch is built");
            }
            if (l.Anchor!.IsSentinel)
            {
                throw new Wave.HookException(
                    $"transpiled IL branches to the end of the body from {at} — there is no instruction there to land on; branch to a labeled instruction instead");
            }
        }
    }

    private static void EmitNode(
        ILGenerator il, WaveIlInstruction n, IlBody b, Type? declaringType,
        Type[] genericArgs, Type[] genericMethodArgs, LocalBuilder[] locals,
        Func<WaveIlLabel, Label> labelOf)
    {
        // Branch operands are labels on this path — emit them directly; everything else
        // (raw metadata tokens from the original body, resolved operands from transpilers)
        // goes through the shared operand emitter.
        switch (n.OpCode.OperandType)
        {
            case OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget:
                il.Emit(n.OpCode, labelOf((WaveIlLabel)n.Operand!));
                return;
            case OperandType.InlineSwitch:
            {
                var labels = ((WaveIlLabel[])n.Operand!).Select(labelOf).ToArray();
                il.Emit(n.OpCode, labels);
                return;
            }
        }

        IlRewriter.Emit(il, new IlInstruction
        {
            Offset = n.OriginalOffset ?? 0,
            OpCode = n.OpCode,
            Operand = n.Operand,
        }, b.Method.Module, declaringType, genericArgs, genericMethodArgs, locals);
    }
}
