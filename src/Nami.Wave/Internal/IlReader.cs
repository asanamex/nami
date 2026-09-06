using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Nami.Wave.Internal;

/// <summary>
/// A single IL instruction with its resolved operand, decoded from a method body.
/// </summary>
internal readonly struct IlInstruction
{
    public required int Offset { get; init; }
    public required OpCode OpCode { get; init; }
    public object? Operand { get; init; }

    /// <summary>Original IL offset of the branch target, for instructions whose operand is a label.</summary>
    public int? BranchTargetOffset { get; init; }

    public override string ToString() => $"{Offset:X4}: {OpCode} {Operand ?? ""}".TrimEnd();
}

/// <summary>
/// Decodes a method body's IL into <see cref="IlInstruction"/>s, resolving the raw metadata
/// tokens of branch/label/switch instructions into IL offsets and leaving other operands as
/// their raw tokens (types/methods/fields/strings resolved later via the module).
/// </summary>
internal static unsafe class IlReader
{
    private static readonly OpCode[] SingleByteOpcodes = new OpCode[0x100];
    private static readonly OpCode[] DoubleByteOpcodes = new OpCode[0x100];

    static IlReader()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op)
            {
                var value = op.Value;
                if (value >= 0 && value < 0x100)
                {
                    SingleByteOpcodes[value] = op;
                }
                else if ((value & 0xFF00) == 0xFE00)
                {
                    DoubleByteOpcodes[value & 0xFF] = op;
                }
            }
        }
    }

    public static List<IlInstruction> Read(MethodBase method)
    {
        var body = method.GetMethodBody() ?? throw new InvalidOperationException($"no body for {method}");
        var il = body.GetILAsByteArray() ?? throw new InvalidOperationException($"no IL for {method}");

        var instructions = new List<IlInstruction>(il.Length / 2);
        var offsets = new List<int>(instructions.Capacity);
        int i = 0;
        while (i < il.Length)
        {
            int offset = i;
            offsets.Add(offset);
            byte b = il[i++];
            OpCode op;
            if (b == 0xFE)
            {
                if (i >= il.Length)
                {
                    throw new InvalidOperationException($"truncated two-byte opcode at {offset}");
                }
                op = DoubleByteOpcodes[il[i++]];
            }
            else
            {
                op = SingleByteOpcodes[b];
            }

            if (op.OperandType == OperandType.InlineNone)
            {
                instructions.Add(new IlInstruction { Offset = offset, OpCode = op });
                continue;
            }

            if (i >= il.Length)
            {
                throw new InvalidOperationException($"truncated operand at {offset} ({op})");
            }

            object? operand = null;
            int? branchTarget = null;
            switch (op.OperandType)
            {
                case OperandType.InlineBrTarget:
                {
                    int delta = ReadI32(il, ref i);
                    int target = offset + delta + 5;
                    operand = target;
                    branchTarget = target;
                    break;
                }
                case OperandType.ShortInlineBrTarget:
                {
                    sbyte delta = (sbyte)il[i++];
                    int target = offset + delta + 2;
                    operand = target;
                    branchTarget = target;
                    break;
                }
                case OperandType.InlineSwitch:
                {
                    int n = ReadI32(il, ref i);
                    var targets = new int[n];
                    for (int t = 0; t < n; t++)
                    {
                        targets[t] = offset + ReadI32(il, ref i) + 5 + 4 * n;
                    }
                    operand = targets;
                    break;
                }
                case OperandType.InlineI:
                    operand = ReadI32(il, ref i);
                    break;
                case OperandType.ShortInlineI:
                    operand = (sbyte)il[i++];
                    break;
                case OperandType.InlineI8:
                    operand = ReadI64(il, ref i);
                    break;
                case OperandType.ShortInlineR:
                    operand = BitConverter.ToSingle(il, i);
                    i += 4;
                    break;
                case OperandType.InlineR:
                    operand = BitConverter.ToDouble(il, i);
                    i += 8;
                    break;
                case OperandType.ShortInlineVar:
                    operand = il[i++];
                    break;
                case OperandType.InlineVar:
                    operand = ReadU16(il, ref i);
                    break;
                case OperandType.InlineString:
                    operand = ReadToken(il, ref i);
                    break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    operand = ReadToken(il, ref i);
                    break;
                case OperandType.InlineSig:
                    operand = ReadToken(il, ref i);
                    break;
                default:
                    throw new InvalidOperationException($"unsupported operand type {op.OperandType} at {offset}");
            }

            instructions.Add(new IlInstruction { Offset = offset, OpCode = op, Operand = operand, BranchTargetOffset = branchTarget });
        }

        return instructions;
    }

    private static int ReadI32(byte[] il, ref int i)
    {
        int v = il[i] | (il[i + 1] << 8) | (il[i + 2] << 16) | (il[i + 3] << 24);
        i += 4;
        return v;
    }

    private static long ReadI64(byte[] il, ref int i)
    {
        long v = (long)il[i] | ((long)il[i + 1] << 8) | ((long)il[i + 2] << 16) | ((long)il[i + 3] << 24)
               | ((long)il[i + 4] << 32) | ((long)il[i + 5] << 40) | ((long)il[i + 6] << 48) | ((long)il[i + 7] << 56);
        i += 8;
        return v;
    }

    private static ushort ReadU16(byte[] il, ref int i)
    {
        ushort v = (ushort)(il[i] | (il[i + 1] << 8));
        i += 2;
        return v;
    }

    private static int ReadToken(byte[] il, ref int i)
    {
        int v = il[i] | (il[i + 1] << 8) | (il[i + 2] << 16) | (il[i + 3] << 24);
        i += 4;
        return v;
    }
}
