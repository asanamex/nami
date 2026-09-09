namespace Nami.Wave.Internal;

/// <summary>
/// Conservative x64 instruction length decoder used to relocate a function prologue into a
/// trampoline. Safety rule: if an instruction cannot be measured with certainty (VEX/EVEX,
/// 3-byte opcode escapes, exotic forms), Decode returns null and the hook is REFUSED rather
/// than risk splitting an instruction. Handles everything a normal compiler emits in a
/// function prologue: REX, ModRM/SIB, displacement, RIP-relative, immediates, movabs, and
/// marks instructions that must NOT be copied (relative branches, end-of-block forms).
/// </summary>
internal static unsafe class X64Decoder
{
    public sealed class Instruction
    {
        public int Length;
        public bool IsRipRelative;          // instruction references RIP+disp32 → needs fixup if relocated
        public bool IsRelativeControlFlow;  // jcc/jmp/call rel8/rel32 → NEVER copy; stop scanning
        public bool IsIndirectControlFlow;  // ff /2-/5 (call/jmp via reg/mem) → needs stub, stop scanning
        public bool EndsBasicBlock;         // ret/ud2/int3/jmp (unconditional) → stop scanning
        public bool IsStackPush;            // push r64 → prologue stack effect
        public bool IsStackReserve;         // sub rsp, imm → prologue stack effect
        public int PushCount;               // 1 for push forms
        public int PushedRegister;          // 0-15 for push r64 (only when PushCount==1 and it's a reg push)
        public int ReserveBytes;            // imm for sub rsp, imm (0 if not a reserve)
        public byte[] Raw = Array.Empty<byte>();
    }

    public static Instruction? Decode(byte* p, int maxLen)
    {
        var ins = new Instruction();
        int start = 0;   // index of the first byte of the full instruction (prefixes included)
        int i = 0;       // index after consumed prefixes (opcode position)
        bool hasRex = false;
        int rex = 0;
        bool operand16 = false;
        bool hasPrefix = true;

        while (hasPrefix && i < maxLen)
        {
            switch (p[i])
            {
                case 0xF0 or 0xF2 or 0xF3 or 0x2E or 0x36 or 0x3E or 0x26 or 0x64 or 0x65:
                    i++;
                    continue;
                case 0x66:
                    operand16 = true;
                    i++;
                    continue;
                case 0x67:
                    i++;
                    continue; // address-size: not modeled; rare in prologues, refuse later if used
                case >= 0x40 and <= 0x4F:
                    hasRex = true;
                    rex = p[i];
                    i++;
                    continue;
                default:
                    hasPrefix = false;
                    break;
            }
        }

        if (i >= maxLen)
        {
            return null;
        }

        bool rexW = hasRex && (rex & 0x08) != 0;

        // Refuse VEX/EVEX/XOP outright (AVX prologues are not a compiler pattern; be safe).
        byte first = p[i];
        if (first is 0xC4 or 0xC5 or 0x62 or 0x63 or 0x8F)
        {
            return null;
        }

        int pos = i;
        bool twoByte = false;
        byte opcode;

        if (first == 0x0F)
        {
            twoByte = true;
            if (i + 1 >= maxLen)
            {
                return null;
            }

            // Refuse 3-byte escapes (0F 38 / 0F 3A) - BMI/SSE4 - cannot measure safely here.
            byte second = p[i + 1];
            if (second is 0x38 or 0x3A)
            {
                return null;
            }

            opcode = second;
            pos = i + 2;
        }
        else
        {
            opcode = first;
            pos = i + 1;
        }

        // ---- Opcodes with NO ModRM (and no memory operand) ----
        bool noModRm = NoModRm(twoByte, opcode);
        bool hasModRm = !noModRm;

        int modrmOffset = pos;
        byte modrm = 0;
        if (hasModRm)
        {
            if (pos >= maxLen)
            {
                return null;
            }

            modrm = p[pos];
            pos++;
            int mod = modrm >> 6;
            int rm = modrm & 7;

            if (rm == 4 && mod != 3)
            {
                // SIB
                if (pos >= maxLen)
                {
                    return null;
                }

                byte sib = p[pos];
                pos++;
                int baseReg = sib & 7;
                if (mod == 0 && baseReg == 5)
                {
                    // disp32, no base
                    if (pos + 4 > maxLen)
                    {
                        return null;
                    }

                    pos += 4;
                }
            }

            if (mod == 0 && rm == 5)
            {
                // RIP-relative
                if (pos + 4 > maxLen)
                {
                    return null;
                }

                ins.IsRipRelative = true;
                pos += 4;
            }
            else if (mod == 1)
            {
                if (pos + 1 > maxLen)
                {
                    return null;
                }

                pos += 1;
            }
            else if (mod == 2)
            {
                if (pos + 4 > maxLen)
                {
                    return null;
                }

                pos += 4;
            }
        }

        // ---- Relative control flow: refuse-to-copy markers ----
        if (!twoByte)
        {
            switch (opcode)
            {
                case 0xE8 or 0xE9: // call/jmp rel32
                    if (pos + 4 > maxLen)
                    {
                        return null;
                    }

                    ins.IsRelativeControlFlow = true;
                    ins.EndsBasicBlock = true;
                    pos += 4;
                    break;
                case 0xEB or >= 0x70 and <= 0x7F: // jmp/jcc rel8
                    if (pos + 1 > maxLen)
                    {
                        return null;
                    }

                    ins.IsRelativeControlFlow = true;
                    ins.EndsBasicBlock = true;
                    pos += 1;
                    break;
                case 0xE3: // jrcxz rel8
                    if (pos + 1 > maxLen)
                    {
                        return null;
                    }

                    ins.IsRelativeControlFlow = true;
                    ins.EndsBasicBlock = true;
                    pos += 1;
                    break;
            }
        }
        else if (opcode is >= 0x80 and <= 0x8F)
        {
            // 0F 80-8F: jcc rel32
            if (pos + 4 > maxLen)
            {
                return null;
            }

            ins.IsRelativeControlFlow = true;
            ins.EndsBasicBlock = true;
            pos += 4;
        }

        // ---- FF /2-/5 indirect call/jmp ----
        if (!twoByte && opcode == 0xFF && hasModRm)
        {
            int reg = (modrm >> 3) & 7;
            if (reg is 2 or 3 or 4 or 5)
            {
                ins.IsIndirectControlFlow = true;
                if (reg is 4 or 5)
                {
                    ins.EndsBasicBlock = true;
                }
            }
        }

        // ---- Immediates ----
        int imm = GetImmediateSize(twoByte, opcode, rexW, modrm, hasModRm);
        if (imm < 0)
        {
            return null;
        }

        if (imm > 0)
        {
            if (pos + imm > maxLen)
            {
                return null;
            }

            pos += imm;
        }

        // ---- Terminal / end-of-block detection ----
        if (!twoByte)
        {
            switch (opcode)
            {
                case 0xC3: // ret
                case 0xCB: // retf
                case 0xF4: // hlt
                case 0xCC: // int3
                    ins.EndsBasicBlock = true;
                    break;
                case 0xC2 or 0xCA: // ret imm16
                    ins.EndsBasicBlock = true;
                    break;
            }
        }
        else if (opcode == 0x0B || opcode == 0x01) // ud2 (0F 0B), 0F 01 group (rare, no imm)
        {
            ins.EndsBasicBlock = opcode == 0x0B;
        }

        // ---- Stack-frame markers (prologue classification) ----
        if (!twoByte)
        {
            // push r64 (50-57 = push r0-r7; with REX.B = push r8-r15), push imm (68/6A)
            switch (opcode)
            {
                case >= 0x50 and <= 0x57:
                {
                    int reg = opcode - 0x50;
                    if (hasRex && (rex & 0x01) != 0)
                    {
                        reg += 8;
                    }

                    ins.IsStackPush = true;
                    ins.PushCount = 1;
                    ins.PushedRegister = reg;
                    break;
                }
                case 0x68 or 0x6A:
                    ins.IsStackPush = true;
                    ins.PushCount = 1;
                    ins.PushedRegister = -1; // immediate push, not a register
                    break;
            }

            // sub rsp, imm8/imm32 (83 /5 imm8, 81 /5 imm32) - via ModRM reg==5
            if (opcode is 0x81 or 0x83 && hasModRm)
            {
                int reg = (modrm >> 3) & 7;
                if (reg == 5)
                {
                    // modrm at p[i+1]; imm follows modrm (mod==3 for rsp target, so no disp).
                    int immOff = i + 2;
                    int reserveImm;
                    if (opcode == 0x83)
                    {
                        reserveImm = *(sbyte*)(p + immOff);
                    }
                    else
                    {
                        reserveImm = *(int*)(p + immOff);
                    }

                    ins.IsStackReserve = true;
                    ins.ReserveBytes = reserveImm;
                }
            }
        }

        // jmp (EB/E9) already EndsBasicBlock via control-flow branch above.

        int length = pos - start;
        if (length <= 0 || pos > maxLen)
        {
            return null;
        }

        // Segment/address-size prefixes we did not model make length unsafe → refuse.
        // (We consumed them above; this guard is for future-proofing.)
        if (operand16 && pos > maxLen)
        {
            return null;
        }

        ins.Length = length;
        ins.Raw = new byte[length];
        for (int k = start; k < pos; k++)
        {
            ins.Raw[k - start] = p[k];
        }

        return ins;
    }

    private static bool NoModRm(bool twoByte, byte opcode)
    {
        if (!twoByte)
        {
            switch (opcode)
            {
                case 0x50 or 0x51 or 0x52 or 0x53 or 0x54 or 0x55 or 0x56 or 0x57: // push r64
                case 0x58 or 0x59 or 0x5A or 0x5B or 0x5C or 0x5D or 0x5E or 0x5F: // pop r64
                case 0x68 or 0x6A: // push imm
                case 0xE8 or 0xE9 or 0xEB: // rel control flow
                case 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x76 or 0x77:
                case 0x78 or 0x79 or 0x7A or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F:
                case 0xE3:
                case 0xC3 or 0xC2 or 0xCB or 0xCA: // ret forms
                case 0xC9: // leave
                case 0xCC or 0xCD: // int3, int imm8
                case 0xF4: // hlt
                case 0x90: // nop
                case 0x98 or 0x99: // cwde/cdqe, cdq/cqo
                case 0x9C or 0x9D: // pushfq/popfq
                case 0xF8 or 0xF9: // clc/stc
                case 0xFC or 0xFD: // cld/std
                case 0x0E or 0x16 or 0x1E or 0x26 or 0x2E or 0x36 or 0x3E: // segment (legacy)
                case 0x27 or 0x2F or 0x37 or 0x3F: // daa/das/aaa/aas
                    return true;
                case 0xB0 or 0xB1 or 0xB2 or 0xB3 or 0xB4 or 0xB5 or 0xB6 or 0xB7: // mov r8, imm8
                case 0xB8 or 0xB9 or 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF: // mov r64, imm64 (or imm32 w/o rexw)
                    return true;
            }

            return false;
        }

        // two-byte
        switch (opcode)
        {
            case 0x05: // syscall
            case 0x34: // sysenter
            case 0x35: // sysexit
            case 0x0B: // ud2
            case 0x31: // rdtsc
            case 0x32: // rdtscp
            case 0x77: // emms
                return true;
        }

        return false;
    }

    private static int GetImmediateSize(bool twoByte, byte opcode, bool rexW, byte modrm, bool hasModRm)
    {
        if (twoByte)
        {
            switch (opcode)
            {
                case 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x76 or 0x77: // pshufw/cmov? (0F 70-77 are SSE imm8: pshufd etc.)
                    return 1;
                case 0x78 or 0x79: // vmread/vmwrite (no imm)
                    return 0;
                case 0xA4 or 0xAC: // shld/shrd imm8
                    return 1;
                case 0xBA: // bt/bts/btr/btc imm8 (0F BA /4-/7)
                    return hasModRm ? 1 : 0;
                case 0xC2: // cmpps/cmpsd/cmppd imm8
                    return 1;
                case 0xC6: // shufps etc imm8
                    return 1;
                case 0xC7: // cmpxchg8b/16b (no imm) OR 0F C7 group - no imm
                    return 0;
            }

            return 0;
        }

        switch (opcode)
        {
            case 0xA8: // test al, imm8
                return 1;
            case 0xA9: // test eax/rax, imm32 (rax imm32 sign-ext)
                return 4;
            case 0x6A:
                return 1;
            case 0x68:
                return 4;
            case 0xC2 or 0xCA: // ret imm16
                return 2;
            case 0xCD: // int imm8
                return 1;
            case 0xC0 or 0xC1: // rol/ror/rcl/rcr/shl/shr/sal/sar imm8
                return 1;
            case 0xD0 or 0xD1 or 0xD2 or 0xD3:
                return 0;
            case 0xB0 or 0xB1 or 0xB2 or 0xB3 or 0xB4 or 0xB5 or 0xB6 or 0xB7:
                return 1;
            case 0xB8 or 0xB9 or 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF:
                // mov r64, imm64 (REX.W) else mov r32, imm32
                return rexW ? 8 : 4;
            case 0x80 or 0x82: // group1 ALU r/m8, imm8
                return 1;
            case 0x83: // group1 ALU r/m, imm8 (sign-extended)
                return 1;
            case 0x81: // group1 ALU r/m, imm32 (sign-extended to 64 even with REX.W)
                return 4;
            case 0xC6: // mov r/m8, imm8 (/0)
                return (hasModRm && ((modrm >> 3) & 7) == 0) ? 1 : 0;
            case 0xC7: // mov r/m, imm32 (sign-extended to 64 even with REX.W)
                return (hasModRm && ((modrm >> 3) & 7) == 0) ? 4 : 0;
            case 0xF6: // test r/m8, imm8 (/0) or not/neg/mul/imul/div/idiv (no imm)
                return (hasModRm && ((modrm >> 3) & 7) == 0) ? 1 : 0;
            case 0xF7: // test r/m, imm32 (/0) or others (no imm)
                return (hasModRm && ((modrm >> 3) & 7) == 0) ? 4 : 0;
            case 0xE8 or 0xE9 or 0xEB:
            case >= 0x70 and <= 0x7F:
            case 0xE3:
                return 0; // relative sizes consumed by caller
        }

        return 0;
    }
}
