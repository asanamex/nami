using System.Runtime.InteropServices;

namespace Nami.Wave.Internal;

/// <summary>
/// A single inline detour on an x64 code address.
///
/// Installation:
///   1. Decode the prologue until it is long enough to hold a 14-byte absolute jump
///      (mov rax, imm64; jmp rax) and contains no instruction we refuse to relocate
///      (relative branches, end-of-block, indirect control flow).
///   2. Allocate a trampoline: relocated prologue bytes, RIP-relative operands fixed up,
///      then a jump back to the original function past the patch site.
///   3. Overwrite the prologue with the absolute jump to the detour target.
///
/// Uninstallation restores the original bytes exactly.
/// </summary>
internal sealed unsafe class Detour : IDisposable
{
    private const int MinJumpSize = 14; // mov rax, imm64 (10) + jmp rax (2) — see EmitAbsoluteJump
    private const int MaxPrologueBytes = 64;

    private readonly byte* _target;
    private byte* _detour;
    private readonly int _patchLength;
    private readonly byte[] _originalBytes;
    private readonly byte* _trampoline;
    private readonly nuint _trampolineSize;
    private readonly List<IntPtr> _allocations = new();
    private bool _installed;
    private bool _disposed;

    /// <summary>True if the relocated prologue is a leaf (no stack push/reserve), so a gate
    /// may skip the original and return cleanly to the caller.</summary>
    public bool FrameSafe { get; private set; }

    /// <summary>Registers pushed by the relocated prologue, in order.</summary>
    public int[] PushedRegisters { get; private set; } = Array.Empty<int>();

    /// <summary>Total bytes reserved by `sub rsp, imm` in the relocated prologue.</summary>
    public int TotalReserveBytes { get; private set; }

    /// <summary>True if the target prologue could be safely decoded.</summary>
    public bool CanInstall => _patchLength > 0;

    private Detour(byte* target, int patchLength, byte[] original, byte* trampoline, nuint trampolineSize)
    {
        _target = target;
        _patchLength = patchLength;
        _originalBytes = original;
        _trampoline = trampoline;
        _trampolineSize = trampolineSize;
    }

    public static Detour? TryCreate(IntPtr target)
    {
        if (target == IntPtr.Zero)
        {
            return null;
        }

        var t = (byte*)target;

        // Decode the prologue until we have >= MinJumpSize relocatable bytes.
        var decoded = new List<X64Decoder.Instruction>();
        int offset = 0;
        bool ok = true;
        while (offset < MinJumpSize)
        {
            var ins = X64Decoder.Decode(t + offset, MaxPrologueBytes - offset);
            if (ins is null)
            {
                ok = false;
                break;
            }

            decoded.Add(ins);
            offset += ins.Length;

            // If the prologue ends (ret/jmp/...) before covering the jump, we cannot
            // install a detour at this address safely.
            if (ins.EndsBasicBlock && offset < MinJumpSize)
            {
                ok = false;
                break;
            }

            // If the block ends exactly at/after MinJumpSize with a relative/indirect
            // control-flow instruction as the last decoded one, we refuse: copying a
            // relative branch into the trampoline would point at the wrong target.
            if (ins.IsRelativeControlFlow || ins.IsIndirectControlFlow)
            {
                ok = false;
                break;
            }

            if (offset > MaxPrologueBytes)
            {
                ok = false;
                break;
            }
        }

        if (!ok || offset < MinJumpSize)
        {
            return null;
        }

        // Copy original bytes.
        var original = new byte[offset];
        for (int i = 0; i < offset; i++)
        {
            original[i] = t[i];
        }

        // Record prologue stack effects so a "skip the original" path can unwind the frame
        // (run the relocated prologue, then reverse pushes and the stack reserve, then ret).
        int totalReserve = 0;
        var pushedRegs = new List<int>();
        foreach (var ins in decoded)
        {
            if (ins.IsStackPush && ins.PushedRegister >= 0)
            {
                pushedRegs.Add(ins.PushedRegister);
            }

            if (ins.IsStackReserve)
            {
                totalReserve += ins.ReserveBytes;
            }
        }

        // Allocate the run-original trampoline and (if needed) a skip trampoline.
        var trampoline = (byte*)RawMemory.AllocExecutable((nuint)(offset + MinJumpSize + 32));
        var detourObj = new Detour(t, offset, original, trampoline, (nuint)(offset + MinJumpSize + 32))
        {
            FrameSafe = pushedRegs.Count == 0 && totalReserve == 0,
            PushedRegisters = pushedRegs.ToArray(),
            TotalReserveBytes = totalReserve
        };
        detourObj._allocations.Add((IntPtr)trampoline);

        // Emits the relocated prologue into `dst`, fixing RIP-relative displacements for
        // that buffer's own location. Returns the byte offset just past the prologue.
        int EmitRelocated(byte* dst)
        {
            int dp = 0;
            int srcOff = 0;
            foreach (var ins in decoded)
            {
                for (int i = 0; i < ins.Length; i++)
                {
                    dst[dp + i] = t[srcOff + i];
                }

                if (ins.IsRipRelative)
                {
                    long origDisp = ReadI32(t + srcOff + ins.Length - 4);
                    byte* origDispField = t + srcOff + ins.Length - 4;
                    byte* origRipAfter = origDispField + 4;
                    byte* origTarget = origRipAfter + origDisp;

                    byte* newDispField = dst + dp + ins.Length - 4;
                    byte* newRipAfter = newDispField + 4;
                    long newDisp = origTarget - newRipAfter;
                    WriteI32(newDispField, (int)newDisp);
                }

                dp += ins.Length;
                srcOff += ins.Length;
            }

            return dp;
        }

        // Trampoline A: relocated prologue + jump back into the original past the patch site.
        int tp = EmitRelocated(trampoline);
        EmitAbsoluteJump(trampoline + tp, t + offset);
        tp += MinJumpSize;
        RawMemory.FlushCode(trampoline, (nuint)tp);
        RawMemory.MakeExecutable(trampoline, (nuint)tp);

        // Trampoline B (skip): relocated prologue + add rsp,reserve + pop pushed regs (reverse)
        // + ret. Only allocated when the prologue has stack effects; otherwise the dispatcher
        // stub returns directly.
        if (!detourObj.FrameSafe)
        {
            var skip = (byte*)RawMemory.AllocExecutable((nuint)(offset + 64));
            detourObj._allocations.Add((IntPtr)skip);
            int sp = EmitRelocated(skip);
            if (detourObj.TotalReserveBytes != 0)
            {
                sp += EmitAddRspImm(skip + sp, detourObj.TotalReserveBytes);
            }

            // Pop in reverse order.
            for (int i = detourObj.PushedRegisters.Length - 1; i >= 0; i--)
            {
                sp += EmitPopReg(skip + sp, detourObj.PushedRegisters[i]);
            }

            skip[sp++] = 0xC3; // ret
            RawMemory.FlushCode(skip, (nuint)sp);
            RawMemory.MakeExecutable(skip, (nuint)sp);
            detourObj._skipTrampoline = skip;
        }

        return detourObj;
    }

    private byte* _skipTrampoline;

    /// <summary>Trampoline that runs the relocated prologue, unwinds the frame and returns
    /// (used when a gate skips the original). Null when the prologue is frame-safe.</summary>
    public IntPtr SkipTrampoline => (IntPtr)_skipTrampoline;

    /// <summary>Emits `add rsp, imm8/imm32` at p; returns bytes written.</summary>
    private static int EmitAddRspImm(byte* p, int imm)
    {
        if (imm is >= -128 and <= 127)
        {
            p[0] = 0x48; p[1] = 0x83; p[2] = 0xC4; p[3] = (byte)imm;
            return 4;
        }

        p[0] = 0x48; p[1] = 0x81; p[2] = 0xC4;
        *(int*)(p + 3) = imm;
        return 7;
    }

    /// <summary>Emits `pop r64` for register 0-15 at p; returns bytes written.</summary>
    private static int EmitPopReg(byte* p, int reg)
    {
        if (reg < 8)
        {
            p[0] = (byte)(0x58 + reg);
            return 1;
        }

        p[0] = 0x41; // REX.B
        p[1] = (byte)(0x58 + (reg - 8));
        return 2;
    }

    public IntPtr Trampoline => (IntPtr)_trampoline;

    public int PatchLength => _patchLength;

    /// <summary>Points the detour at its final target (the per-site dispatcher stub).</summary>
    public void Retarget(IntPtr detour) => _detour = (byte*)detour;

    public void Install()
    {
        if (_installed || _disposed || _detour == null)
        {
            return;
        }

        // Write the 14-byte absolute jump to the detour over the prologue.
        RawMemory.MakeWritable(_target, (nuint)MinJumpSize);
        try
        {
            EmitAbsoluteJump(_target, _detour);
        }
        finally
        {
            RawMemory.RestoreProtection(_target, (nuint)MinJumpSize, RawMemory.PageExecuteRead);
        }
        _installed = true;
    }

    public void Uninstall()
    {
        if (!_installed || _disposed)
        {
            return;
        }

        RawMemory.MakeWritable(_target, (nuint)_patchLength);
        try
        {
            for (int i = 0; i < _patchLength; i++)
            {
                _target[i] = _originalBytes[i];
            }
        }
        finally
        {
            RawMemory.RestoreProtection(_target, (nuint)_patchLength, RawMemory.PageExecuteRead);
        }
        _installed = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Uninstall();
        }
        finally
        {
            foreach (var alloc in _allocations)
            {
                RawMemory.FreeExecutable((byte*)alloc, 0);
            }

            _disposed = true;
        }
    }

    /// <summary>Emits `mov rax, imm64; jmp rax` at <paramref name="p"/> (14 bytes).</summary>
    public static void EmitAbsoluteJump(byte* p, byte* destination)
    {
        p[0] = 0x48; p[1] = 0xB8; // mov rax, imm64
        WriteU64(p + 2, (ulong)destination);
        p[10] = 0xFF; p[11] = 0xE0; // jmp rax
        p[12] = 0x90; p[13] = 0x90; // nop padding (never executed)
    }

    private static long ReadI32(byte* p) => *(int*)p;

    private static void WriteI32(byte* p, int v) => *(int*)p = v;

    private static void WriteU64(byte* p, ulong v) => *(ulong*)p = v;
}
