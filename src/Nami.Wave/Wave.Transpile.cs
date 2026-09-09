using System.Reflection;
using System.Reflection.Emit;
using Nami.Wave.Internal;

namespace Nami.Wave;

/// <summary>
/// A Wave transpiler: rewrites the IL of a patch target through a cursor (MonoMod
/// ILCursor-style) rather than a mutable instruction list. Labels and branch targets are
/// first-class objects, so inserting or removing instructions can never silently move a
/// jump somewhere else - the failure mode of list-index transpilers.
///
/// Every instruction the transpiler touches is provenance-tagged with the owner string:
/// when a second transpiler edits IL introduced (or already edited) by another owner, Wave
/// records a <see cref="WaveTranspilerConflict"/> and raises <see cref="Wave.TranspilerConflict"/>
/// instead of silently composing unpredictable edits.
/// </summary>
/// <param name="il">Cursor positioned before the first instruction of the original body.</param>
public delegate void WaveTranspiler(WaveIlCursor il);

/// <summary>
/// Detected overlap between two transpilers on the same method: <see cref="Modifier"/>
/// mutated IL that <see cref="PriorOwner"/> introduced (or already edited). The edit is
/// applied (deterministically - later transpilers win) but the collision is surfaced so
/// behavior is never silently wrong.
/// </summary>
public sealed class WaveTranspilerConflict
{
    public required MethodBase Target { get; init; }
    /// <summary>Owner performing the edit.</summary>
    public required string Modifier { get; init; }
    /// <summary>Owner whose IL is being modified.</summary>
    public required string PriorOwner { get; init; }
    /// <summary>Edit kind: replace / remove / retarget.</summary>
    public required string Edit { get; init; }
    /// <summary>Original IL offset of the edited instruction (null when IL was itself injected by a mod).</summary>
    public required int? OriginalOffset { get; init; }

    public override string ToString() =>
        $"transpiler conflict on {Target.DeclaringType?.FullName}.{Target.Name}: " +
        $"'{Modifier}' {Edit} IL owned by '{PriorOwner}'" +
        (OriginalOffset is { } off ? $" at IL_{off:X4}" : " (injected IL)");
}

/// <summary>
/// One IL instruction as exposed to transpilers. Original instructions carry their
/// <see cref="OriginalOffset"/> and a null <see cref="CreatedBy"/>; instructions injected by
/// a transpiler carry their owner and a null offset. Branch operands are
/// <see cref="WaveIlLabel"/>s (single target) or <c>WaveIlLabel[]</c> (switch) - never raw
/// offsets - so edits elsewhere cannot invalidate them.
/// </summary>
public sealed class WaveIlInstruction
{
    internal WaveIlInstruction? Next;
    internal WaveIlInstruction? Prev;
    internal readonly List<WaveIlLabel> Anchored = new();
    internal readonly List<(string Owner, string Kind)> History = new();
    internal List<Action<ILGenerator>>? Before; // structural EH actions applied before this node
    internal bool SkipEmit;                     // endfinally/endfilter synthesized by the builder
    internal bool IsSentinel { get; private set; }

    private WaveIlInstruction(OpCode op, object? operand, int? originalOffset, string? createdBy, bool isSentinel)
    {
        OpCode = op;
        Operand = operand;
        OriginalOffset = originalOffset;
        CreatedBy = createdBy;
        IsSentinel = isSentinel;
    }

    internal WaveIlInstruction(OpCode op, object? operand, int? originalOffset, string? createdBy)
        : this(op, operand, originalOffset, createdBy, isSentinel: false)
    {
    }

    internal static WaveIlInstruction Sentinel() => new(default, null, null, null, isSentinel: true);

    public OpCode OpCode { get; internal set; }
    public object? Operand { get; internal set; }

    /// <summary>Original IL offset (null for transpiler-injected instructions and sentinels).</summary>
    public int? OriginalOffset { get; }

    /// <summary>Owner that injected this instruction (null for the original body).</summary>
    public string? CreatedBy { get; internal set; }

    /// <summary>False for transpiler-injected instructions and sentinels.</summary>
    public bool IsOriginal => !IsSentinel && OriginalOffset is not null;

    public override string ToString() =>
        OriginalOffset is { } off
            ? $"IL_{off:X4}: {OpCode} {Operand ?? ""}".TrimEnd()
            : $"+ {OpCode} {Operand ?? ""} (by {CreatedBy ?? "?"})".TrimEnd();
}

/// <summary>A first-class branch target. Anchored to an instruction (or the end position), never
/// to an index - inserting instructions elsewhere cannot move it.</summary>
public sealed class WaveIlLabel
{
    internal WaveIlInstruction? Anchor;

    /// <summary>True once anchored (by the pipeline for original branch targets, or via MarkLabel).</summary>
    public bool IsAnchored => Anchor is not null;

    /// <summary>The instruction this label denotes (null when unanchored or anchored at the end position).</summary>
    public WaveIlInstruction? Instruction => Anchor is { IsSentinel: false } a ? a : null;

    public override string ToString() => Anchor is null ? "unanchored label" : $"label@{Anchor}";
}

/// <summary>
/// Cursor over the transpilable IL of a patch target. The cursor is an insertion point
/// between instructions (before <see cref="Current"/>, or at the end):
///
///   - <see cref="Emit"/> inserts AT the point (before <see cref="Current"/>) and the point
///     stays - sequential emits keep their order. <see cref="Next"/> steps over Current.
///   - <see cref="Goto(Func{WaveIlInstruction, bool}"/> positions the point immediately
///     before the first matching instruction (returns false without moving when absent).
///   - <see cref="Remove"/> / <see cref="Replace"/> mutate <see cref="Current"/>; labels
///     anchored to it re-anchor to its successor, so surrounding control flow survives.
///   - <see cref="MarkLabel"/> anchors a label at the point; branches reference labels, so
///     inserting instructions can never redirect a jump.
///
/// All mutations are provenance-stamped with the owner; touching another owner's IL raises
/// a conflict (see <see cref="Wave.TranspilerConflict"/>).
/// </summary>
public sealed class WaveIlCursor
{
    private WaveIlInstruction _pos; // the node the cursor point sits BEFORE (tail sentinel = end)
    private readonly WaveIlInstruction _tail;
    private readonly string _owner;
    private readonly MethodBase _target;
    private readonly List<WaveTranspilerConflict> _conflicts = new();

    private WaveIlCursor(WaveIlInstruction pos, WaveIlInstruction tail, string owner, MethodBase target)
    {
        _pos = pos;
        _tail = tail;
        _owner = owner;
        _target = target;
    }

    internal static WaveIlCursor Create(WaveIlInstruction head, WaveIlInstruction tail, string owner, MethodBase target) =>
        new(head.Next!, tail, owner, target);

    internal List<WaveTranspilerConflict> TakeConflicts()
    {
        var c = new List<WaveTranspilerConflict>(_conflicts);
        _conflicts.Clear();
        return c;
    }

    // ------------------------------------------------------------- navigation

    public MethodBase Target => _target;

    /// <summary>The instruction immediately after the cursor point (null at the end).</summary>
    public WaveIlInstruction? Current => _pos.IsSentinel ? null : _pos;

    public bool AtEnd => _pos.IsSentinel;

    /// <summary>Steps over <see cref="Current"/> (no-op at the end).</summary>
    public void Next()
    {
        if (!_pos.IsSentinel)
        {
            _pos = _pos.Next!;
        }
    }

    /// <summary>Moves the insertion point back by one instruction (no-op before the first).</summary>
    public void Prev()
    {
        var prev = _pos.Prev!;
        if (!prev.IsSentinel)
        {
            _pos = prev;
        }
    }

    /// <summary>
    /// Moves the point before the first instruction (from the current point onward) matching
    /// <paramref name="match"/>. Returns false and does not move when nothing matches.
    /// </summary>
    public bool Goto(Func<WaveIlInstruction, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        for (var n = _pos; !n.IsSentinel; n = n.Next!)
        {
            if (match(n))
            {
                _pos = n;
                return true;
            }
        }
        return false;
    }

    /// <summary>Goto the first instruction from here with the given opcode.</summary>
    public bool Goto(OpCode op) => Goto(ins => ins.OpCode == op);

    /// <summary>Goto the instruction at the given ORIGINAL IL offset (injected instructions never match).</summary>
    public bool Goto(int originalOffset) => Goto(ins => ins.OriginalOffset == originalOffset);

    /// <summary>
    /// Moves the point to where <paramref name="label"/> is anchored (or to the end when the
    /// label marks the end position). Throws when the label is still unanchored.
    /// </summary>
    public void GotoLabel(WaveIlLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);
        _pos = label.Anchor ?? throw new ArgumentException("label is not anchored yet", nameof(label));
    }

    /// <summary>Defines an unanchored label; anchor it with <see cref="MarkLabel"/>.</summary>
    public WaveIlLabel DefineLabel() => new();

    /// <summary>Anchors <paramref name="label"/> at the cursor point (branching to it lands at Current).</summary>
    public void MarkLabel(WaveIlLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.IsAnchored)
        {
            throw new InvalidOperationException($"label already anchored at {label.Anchor}");
        }
        label.Anchor = _pos;
        _pos.Anchored.Add(label);
    }

    /// <summary>Defines and anchors a label at the cursor point.</summary>
    public WaveIlLabel AnchorHere()
    {
        var label = DefineLabel();
        MarkLabel(label);
        return label;
    }

    // ------------------------------------------------------------- emitting

    /// <summary>Inserts an instruction at the cursor point (before Current); the point stays put,
    /// so sequential emits keep their order.</summary>
    public void Emit(OpCode op) => EmitCore(op, null);
    public void Emit(OpCode op, int value) => EmitCore(op, value);
    public void Emit(OpCode op, long value) => EmitCore(op, value);
    public void Emit(OpCode op, float value) => EmitCore(op, value);
    public void Emit(OpCode op, double value) => EmitCore(op, value);
    public void Emit(OpCode op, sbyte value) => EmitCore(op, value);
    public void Emit(OpCode op, byte value) => EmitCore(op, value);
    public void Emit(OpCode op, short value) => EmitCore(op, value);
    public void Emit(OpCode op, ushort value) => EmitCore(op, value);
    public void Emit(OpCode op, string value) => EmitCore(op, value ?? throw new ArgumentNullException(nameof(value)));
    public void Emit(OpCode op, Type value) => EmitCore(op, value ?? throw new ArgumentNullException(nameof(value)));
    public void Emit(OpCode op, MethodInfo value) => EmitCore(op, value ?? throw new ArgumentNullException(nameof(value)));
    public void Emit(OpCode op, ConstructorInfo value) => EmitCore(op, value ?? throw new ArgumentNullException(nameof(value)));
    public void Emit(OpCode op, FieldInfo value) => EmitCore(op, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Inserts a branch to <paramref name="label"/> (the label may be anchored later).</summary>
    public void Emit(OpCode op, WaveIlLabel label) => EmitCore(op, label ?? throw new ArgumentNullException(nameof(label)));

    /// <summary>Inserts a switch over the given targets (in case order).</summary>
    public void Emit(OpCode op, params WaveIlLabel[] labels) =>
        EmitCore(op, labels is { Length: > 0 } ? labels : throw new ArgumentException("switch needs at least one target", nameof(labels)));

    private void EmitCore(OpCode op, object? operand)
    {
        ValidateOperand(op, operand);
        var node = new WaveIlInstruction(op, operand, null, _owner);
        InsertBefore(_pos, node);
    }

    // ------------------------------------------------------------- editing

    /// <summary>Removes <see cref="Current"/>. Labels and EH boundaries anchored to it re-anchor
    /// to its successor, so surrounding control flow (branches into the region, exception
    /// blocks) stays coherent. The point stays put - the successor becomes the new
    /// <see cref="Current"/> - so sequential removes drop a contiguous range.</summary>
    public void Remove()
    {
        var node = Current ?? throw new InvalidOperationException("cursor is at the end — nothing to remove");
        RecordMutation(node, "remove");

        // Labels jump to "the instruction that used to be here" → its successor.
        foreach (var l in node.Anchored)
        {
            l.Anchor = node.Next!;
            node.Next!.Anchored.Add(l);
        }
        node.Anchored.Clear();

        // Structural EH boundaries ride along to the same position.
        if (node.Before is { Count: > 0 } before)
        {
            var successor = node.Next!;
            successor.Before ??= new List<Action<ILGenerator>>();
            successor.Before.InsertRange(0, before);
            node.Before = null;
        }

        node.Prev!.Next = node.Next;
        node.Next!.Prev = node.Prev;
        _pos = node.Next;
        node.Next = node.Prev = null;
    }

    /// <summary>Replaces the opcode of <see cref="Current"/>, keeping its operand.</summary>
    public void Replace(OpCode op) => ReplaceCore(op, keepOperand: true, null);

    /// <summary>
    /// Replaces <see cref="Current"/> with a new opcode/operand. A null operand keeps the
    /// existing one. Branch operands must be <see cref="WaveIlLabel"/>s (e.g. retarget a
    /// jump: <c>cursor.Replace(OpCodes.Br, label)</c>).
    /// </summary>
    public void Replace(OpCode op, object? operand) => ReplaceCore(op, operand is null, operand);

    private void ReplaceCore(OpCode op, bool keepOperand, object? operand)
    {
        var node = Current ?? throw new InvalidOperationException("cursor is at the end — nothing to replace");
        var kind = IsBranchOp(op) && operand is WaveIlLabel or WaveIlLabel[] ? "retarget" : "replace";
        if (!keepOperand)
        {
            ValidateOperand(op, operand);
        }
        RecordMutation(node, kind);
        node.OpCode = op;
        if (!keepOperand)
        {
            node.Operand = operand;
        }
    }

    // ------------------------------------------------------------- plumbing

    private static void InsertBefore(WaveIlInstruction pos, WaveIlInstruction node)
    {
        var prev = pos.Prev!;
        node.Prev = prev;
        node.Next = pos;
        prev.Next = node;
        pos.Prev = node;
    }

    private static bool IsBranchOp(OpCode op) =>
        op.OperandType is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget or OperandType.InlineSwitch;

    private static void ValidateOperand(OpCode op, object? operand)
    {
        switch (op.OperandType)
        {
            case OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget:
                if (operand is not WaveIlLabel)
                {
                    throw new ArgumentException(
                        $"{op} requires a WaveIlLabel branch target (got {Describe(operand)}) — branch targets are labels, never offsets", nameof(operand));
                }
                break;
            case OperandType.InlineSwitch:
                if (operand is not WaveIlLabel[] { Length: > 0 })
                {
                    throw new ArgumentException(
                        $"{op} requires a non-empty WaveIlLabel[] of switch targets (got {Describe(operand)})", nameof(operand));
                }
                break;
        }
    }

    private static string Describe(object? o) =>
        o is null ? "null" : $"{o.GetType().Name} '{o}'";

    /// <summary>Provenance: stamp the edit, and flag every prior owner this edit collides with.</summary>
    private void RecordMutation(WaveIlInstruction node, string kind)
    {
        if (node.CreatedBy is { } creator && creator != _owner)
        {
            AddConflict(node, kind, creator);
        }
        foreach (var (histOwner, _) in node.History)
        {
            if (histOwner != _owner && histOwner != node.CreatedBy)
            {
                AddConflict(node, kind, histOwner);
            }
        }
        node.History.Add((_owner, kind));
    }

    private void AddConflict(WaveIlInstruction node, string kind, string priorOwner)
    {
        _conflicts.Add(new WaveTranspilerConflict
        {
            Target = _target,
            Modifier = _owner,
            PriorOwner = priorOwner,
            Edit = kind,
            OriginalOffset = node.OriginalOffset,
        });
    }
}

public static unsafe partial class Wave
{
    private static readonly Lock ConflictLock = new();
    private static readonly List<WaveTranspilerConflict> RecentConflicts = new();
    private const int MaxRecentConflicts = 256;

    /// <summary>
    /// Raised whenever a transpiler edits IL introduced (or already edited) by another
    /// transpiler owner on the same method. The edit is applied - later transpilers win
    /// deterministically - but the collision is never silent. Wire this to the mod log.
    /// </summary>
    public static event Action<WaveTranspilerConflict>? TranspilerConflict;

    /// <summary>Recent conflicts (bounded ring, newest last) - for diagnostics and tests.</summary>
    public static IReadOnlyList<WaveTranspilerConflict> RecentTranspilerConflicts
    {
        get
        {
            lock (ConflictLock)
            {
                return RecentConflicts.ToArray();
            }
        }
    }

    internal static void ReportTranspilerConflicts(MethodBase target, List<WaveTranspilerConflict> conflicts)
    {
        if (conflicts.Count == 0)
        {
            return;
        }

        lock (ConflictLock)
        {
            RecentConflicts.AddRange(conflicts);
            if (RecentConflicts.Count > MaxRecentConflicts)
            {
                RecentConflicts.RemoveRange(0, RecentConflicts.Count - MaxRecentConflicts);
            }
        }

        var handlers = TranspilerConflict;
        if (handlers is null)
        {
            return;
        }

        foreach (var c in conflicts)
        {
            try
            {
                handlers(c);
            }
            catch
            {
                // A failing log handler must never break patching.
            }
        }
    }

    /// <summary>
    /// Adds a transpiler for <paramref name="owner"/> on <paramref name="target"/>: the
    /// target's IL is rewritten through a cursor BEFORE any prefix/postfix wrapping, so a
    /// transpiler shapes the "original" that prefixes and postfixes observe. Transpilers run
    /// in registration order, each seeing the previous one's output - with provenance
    /// tracking (see <see cref="TranspilerConflict"/>) when they overlap.
    /// </summary>
    public static void Transpile(MethodBase target, string owner, WaveTranspiler transpiler)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(transpiler);

        lock (RegistryLock)
        {
            var site = GetOrCreateUnifiedSite(target, "patch");
            if (site.Entries.Any(e => e.Owner == owner && e.Source == ApiSource.Patch))
            {
                throw new InvalidOperationException($"owner '{owner}' already hooked {target}");
            }

            var entry = new WaveChainEntry { Owner = owner, Source = ApiSource.Patch, Seq = site.NextSeq++ };
            entry.Transpilers.Add(transpiler);
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
}
