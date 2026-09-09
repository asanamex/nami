using System.Runtime.InteropServices;

namespace Nami.Wave.Internal;

/// <summary>
/// A single inline detour on an x64 code address.
///
/// Installation:
///   1. Decode the prologue until it is long enough to hold a jump and contains no
///      instruction we refuse to relocate (relative branches, end-of-block, indirect
///      control flow). Preferred form is the 14-byte absolute jump (mov rax, imm64;
///      jmp rax); prologues with only 5+ clean bytes use a 5-byte relative jump
///      (E9 rel32) with a near (±2GB) trampoline instead.
///   2. Allocate a trampoline: relocated prologue bytes, RIP-relative operands fixed up,
///      then a jump back to the original function past the patch site.
///   3. Overwrite the prologue with the jump to the detour target.
///
/// Uninstallation restores the original bytes exactly.
/// </summary>
internal sealed unsafe class Detour : IDisposable
{
    private const int MinJumpSize = 14; // mov rax, imm64 (10) + jmp rax (2) - see EmitAbsoluteJump
    private const int MinNearJumpSize = 5; // E9 rel32 - see EmitRelativeJump
    private const int MaxPrologueBytes = 64;

    private readonly byte* _target;
    private byte* _detour;
    private readonly int _patchLength;
    private readonly bool _nearJump;
    private readonly byte[] _originalBytes;
    private byte[]? _installedBytes;
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

    private Detour(byte* target, int patchLength, bool nearJump, byte[] original, byte* trampoline, nuint trampolineSize)
    {
        _target = target;
        _patchLength = patchLength;
        _nearJump = nearJump;
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

        // Decode the prologue until we have >= needed relocatable bytes.
        static bool TryDecode(byte* t, int need, out List<X64Decoder.Instruction> decoded, out int offset)
        {
            decoded = new List<X64Decoder.Instruction>();
            offset = 0;
            while (offset < need)
            {
                var ins = X64Decoder.Decode(t + offset, MaxPrologueBytes - offset);
                if (ins is null)
                {
                    return false;
                }

                decoded.Add(ins);
                offset += ins.Length;

                // If the prologue ends (ret/jmp/...) before covering the jump, we cannot
                // install a detour at this address safely.
                if (ins.EndsBasicBlock && offset < need)
                {
                    return false;
                }

                // If the block ends exactly at/after the need with a relative/indirect
                // control-flow instruction as the last decoded one, we refuse: copying a
                // relative branch into the trampoline would point at the wrong target.
                if (ins.IsRelativeControlFlow || ins.IsIndirectControlFlow)
                {
                    return false;
                }

                if (offset > MaxPrologueBytes)
                {
                    return false;
                }
            }

            return offset >= need;
        }

        // Preferred: 14-byte absolute jump. Fallback: 5-byte relative jump for prologues
        // with only 5+ clean bytes (tiny methods), with a near trampoline.
        var nearJump = false;
        List<X64Decoder.Instruction> decoded;
        int offset;
        if (!TryDecode(t, MinJumpSize, out decoded, out offset))
        {
            if (!TryDecode(t, MinNearJumpSize, out decoded, out offset))
            {
                return null;
            }

            nearJump = true;
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
        // Prefer near the target: relocated RIP-relative operands keep their reach
        // (a far-away trampoline silently truncates their disp32 - see TrampolinesSound).
        var trampolineSize = (nuint)(offset + MinJumpSize + 32);
        byte* trampoline;
        if (nearJump)
        {
            var near = RawMemory.TryAllocExecutableNear(t, trampolineSize);
            if (near == null)
            {
                return null;
            }

            trampoline = (byte*)near;
        }
        else
        {
            trampoline = (byte*)RawMemory.TryAllocExecutableNear(t, trampolineSize);
            if (trampoline == null)
            {
                trampoline = (byte*)RawMemory.AllocExecutable(trampolineSize);
            }
        }

        var detourObj = new Detour(t, offset, nearJump, original, trampoline, trampolineSize)
        {
            FrameSafe = pushedRegs.Count == 0 && totalReserve == 0,
            PushedRegisters = pushedRegs.ToArray(),
            TotalReserveBytes = totalReserve
        };
        detourObj._allocations.Add((IntPtr)trampoline);

        // Emits the relocated prologue into `dst`, fixing RIP-relative displacements for
        // that buffer's own location. Returns the byte offset just past the prologue.
        // Tracks whether every relocated RIP-relative target still fits disp32: when the
        // trampoline sits too far from the original's data, the fixup would silently
        // truncate (see TrampolinesSound) - executing such a trampoline corrupts memory.
        bool relocationsFit = true;
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
                    if (newDisp != (int)newDisp)
                    {
                        relocationsFit = false;
                    }
                    WriteI32(newDispField, (int)newDisp);
                }

                dp += ins.Length;
                srcOff += ins.Length;
            }

            return dp;
        }

        // Trampoline A: relocated prologue + jump back into the original past the patch site.
        // The jump-back preserves all registers (indirect form): a value-returning
        // original reaches it with its result live in rax/xmm0.
        int tp = EmitRelocated(trampoline);
        if (nearJump)
        {
            EmitRelativeJump(trampoline + tp, t + offset);
            tp += MinNearJumpSize;
        }
        else
        {
            EmitIndirectJump(trampoline + tp, t + offset);
            tp += MinJumpSize;
        }
        RawMemory.FlushCode(trampoline, (nuint)tp);
        RawMemory.MakeExecutable(trampoline, (nuint)tp);

        // Trampoline B (skip): relocated prologue + add rsp,reserve + pop pushed regs (reverse)
        // + ret. Only allocated when the prologue has stack effects; otherwise the dispatcher
        // stub returns directly. Allocated near for the same relocation-reach reason.
        if (!detourObj.FrameSafe)
        {
            var skipAt = RawMemory.TryAllocExecutableNear(t, (nuint)(offset + 64));
            var skip = (byte*)(skipAt == null
                ? RawMemory.AllocExecutable((nuint)(offset + 64))
                : skipAt);
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

        detourObj.TrampolinesSound = relocationsFit;
        return detourObj;
    }

    private byte* _skipTrampoline;

    /// <summary>Trampoline that runs the relocated prologue, unwinds the frame and returns
    /// (used when a gate skips the original). Null when the prologue is frame-safe.</summary>
    public IntPtr SkipTrampoline => (IntPtr)_skipTrampoline;

    /// <summary>
    /// True when every relocated RIP-relative operand still reaches its target from the
    /// trampoline buffers. False means executing either trampoline would corrupt memory
    /// (the disp32 fixup overflowed) - the fast stub path must not be used (the IL-copy
    /// path never executes trampolines, so it is unaffected).
    /// </summary>
    public bool TrampolinesSound { get; private set; } = true;

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

    /// <summary>The method-body address this detour was created for.</summary>
    public IntPtr TargetAddress => (IntPtr)_target;

    /// <summary>
    /// True when the prologue patch is a 5-byte relative jump: anything it points at
    /// (including the site stub) must live within ±2GB of the target.
    /// </summary>
    public bool IsNearJump => _nearJump;

    /// <summary>Points the detour at its final target (the per-site dispatcher stub).</summary>
    public void Retarget(IntPtr detour) => _detour = (byte*)detour;

    public void Install()
    {
        if (_installed || _disposed || _detour == null)
        {
            return;
        }

        // Re-validate immediately before writing: tiered re-JIT can reclaim or replace
        // the code page between resolution and install. Refuse loudly instead of faulting.
        if (!RawMemory.IsExecutableCode(_target))
        {
            throw new WaveHookException(
                $"cannot install detour: target {(nint)_target:X} is no longer executable code " +
                "(tiered re-JIT raced the install — retry the patch)");
        }

        if (_nearJump)
        {
            // 5-byte relative jump; reachability was verified at creation.
            var saved = RawMemory.MakeWritable(_target, (nuint)MinNearJumpSize);
            try
            {
                EmitRelativeJump(_target, _detour);
            }
            finally
            {
                RawMemory.RestoreProtection(_target, (nuint)MinNearJumpSize, saved);
            }
        }
        else
        {
            // 14-byte absolute jump to the detour over the prologue.
            var saved = RawMemory.MakeWritable(_target, (nuint)MinJumpSize);
            try
            {
                EmitAbsoluteJump(_target, _detour);
            }
            finally
            {
                RawMemory.RestoreProtection(_target, (nuint)MinJumpSize, saved);
            }
        }

        _installedBytes = new byte[_patchLength];
        for (int i = 0; i < _patchLength; i++)
        {
            _installedBytes[i] = _target[i];
        }
        _installed = true;
    }

    /// <summary>True when the bytes currently at the target are exactly what Install wrote.</summary>
    private bool MatchesInstalled()
    {
        if (_installedBytes is null || _installedBytes.Length != _patchLength)
        {
            return false;
        }

        for (int i = 0; i < _patchLength; i++)
        {
            if (_target[i] != _installedBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    public void Uninstall()
    {
        if (!_installed || _disposed)
        {
            return;
        }

        // Restore only if OUR jump is still there. Tiered re-JIT can replace the method
        // entry out from under the patch (or reclaim the page): restoring then would
        // corrupt foreign code or fault. Anything else present means there is nothing
        // of ours left to restore.
        if (_installedBytes is null || !RawMemory.IsExecutableCode(_target) || !MatchesInstalled())
        {
            _installed = false;
            return;
        }

        // Restore via VirtualProtect like install: WriteProcessMemory is blocked on
        // hardened hosts (HVCI denies protection-bypassing writes to executable pages
        // with NOACCESS) while the VP flip is the tracked, legitimate flow.
        var saved = RawMemory.MakeWritable(_target, (nuint)_patchLength);
        try
        {
            for (int i = 0; i < _patchLength; i++)
            {
                _target[i] = _originalBytes[i];
            }
        }
        finally
        {
            RawMemory.RestoreProtection(_target, (nuint)_patchLength, saved);
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

    /// <summary>
    /// Emits `jmp qword ptr [rip+0]` + inline address slot at <paramref name="p"/>
    /// (14 bytes, same footprint as <see cref="EmitAbsoluteJump"/>). Unlike the absolute
    /// form it preserves every register and flag - required for trampoline jump-backs,
    /// which run with the original's value return live in rax/xmm0.
    /// </summary>
    public static void EmitIndirectJump(byte* p, byte* destination)
    {
        p[0] = 0xFF; p[1] = 0x25; // jmp [rip+disp32]
        WriteI32(p + 2, 0);       // disp32 = 0: the slot immediately follows
        WriteU64(p + 6, (ulong)destination);
    }

    /// <summary>Emits `jmp rel32` at <paramref name="p"/> (5 bytes); destination must be ±2GB.</summary>
    public static void EmitRelativeJump(byte* p, byte* destination)
    {
        p[0] = 0xE9; // jmp rel32
        WriteI32(p + 1, (int)(destination - (p + 5)));
    }

    private static long ReadI32(byte* p) => *(int*)p;

    private static void WriteI32(byte* p, int v) => *(int*)p = v;

    private static void WriteU64(byte* p, ulong v) => *(ulong*)p = v;
}
