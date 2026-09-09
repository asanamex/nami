// Native dispatch-stub detours (see native_stub.h).

#include "native_stub.h"

#include "tide_pump.h"

#include <windows.h>

#include <cstdlib>
#include <cstring>
#include <new>

namespace nami::stub {

namespace {

// ---------------------------------------------------------------------------
// Prefix-only stub (fast path). Layout (offsets patched at build time):
//   push rbx                      ; rsp = R-8, [R-8] = saved rbx
//   sub rsp, 0x50                 ; rsp = R-0x58
//   mov [rsp+0x20], rcx           ; saved arg slots: [rsp+0x20 .. 0x40)
//   mov [rsp+0x28], rdx
//   mov [rsp+0x30], r8
//   mov [rsp+0x38], r9
//   mov rcx, <handle>             (imm64)
//   lea rdx, [rsp+0x20]
//   mov r8d, <arg_count>          (imm32)
//   mov rax, <dispatch>           (imm64)
//   call rax                      ; shadow space [rsp+0x00..0x20) - BELOW the args
//   test eax, eax
//   jnz skip
//   mov rcx,[rsp+0x20]; mov rdx,[rsp+0x28]; mov r8,[rsp+0x30]; mov r9,[rsp+0x38]
//   add rsp, 0x50
//   pop rbx
//   mov rax, <trampoline>         (imm64)
//   jmp rax
// skip:
//   add rsp, 0x50
//   pop rbx
//   xor eax, eax
//   ret
//
// The dispatch call happens with the ORIGINAL argument registers saved in
// [rsp+0x20..0x40] - below the saved rbx ([rsp+0x50]) and above the dispatch call's
// shadow space ([rsp+0x00..0x20)), so nothing the dispatch does can clobber them.
// The tail-jump path restores them, so the original runs with its exact arguments and
// any stack-passed args (5+) are never touched. rsp stays 16-aligned at the call
// (R ≡ 8 mod 16 at entry; push 8 + sub 0x50 keeps ≡ 8).
constexpr int kStubSize = 128;

int EmitStub(unsigned char* s, uint64_t handle, void* dispatch, int arg_count, uint64_t trampoline) {
    int o = 0;
    // push rbx
    s[o++] = 0x53;
    // sub rsp, 0x50
    s[o++] = 0x48; s[o++] = 0x83; s[o++] = 0xEC; s[o++] = 0x50;
    // mov [rsp+0x20], rcx
    s[o++] = 0x48; s[o++] = 0x89; s[o++] = 0x4C; s[o++] = 0x24; s[o++] = 0x20;
    // mov [rsp+0x28], rdx
    s[o++] = 0x48; s[o++] = 0x89; s[o++] = 0x54; s[o++] = 0x24; s[o++] = 0x28;
    // mov [rsp+0x30], r8
    s[o++] = 0x4C; s[o++] = 0x89; s[o++] = 0x44; s[o++] = 0x24; s[o++] = 0x30;
    // mov [rsp+0x38], r9
    s[o++] = 0x4C; s[o++] = 0x89; s[o++] = 0x4C; s[o++] = 0x24; s[o++] = 0x38;
    // mov rcx, handle (imm64)
    s[o++] = 0x48; s[o++] = 0xB9;
    *reinterpret_cast<uint64_t*>(s + o) = handle; o += 8;
    // lea rdx, [rsp+0x20]
    s[o++] = 0x48; s[o++] = 0x8D; s[o++] = 0x54; s[o++] = 0x24; s[o++] = 0x20;
    // mov r8d, arg_count (imm32)
    s[o++] = 0x41; s[o++] = 0xB8;
    *reinterpret_cast<int*>(s + o) = arg_count; o += 4;
    // mov rax, dispatch (imm64)
    s[o++] = 0x48; s[o++] = 0xB8;
    *reinterpret_cast<uint64_t*>(s + o) = reinterpret_cast<uint64_t>(dispatch); o += 8;
    // call rax
    s[o++] = 0xFF; s[o++] = 0xD0;
    // test eax, eax
    s[o++] = 0x85; s[o++] = 0xC0;
    // jnz skip (displacement patched below; skip path at end)
    const int jnz_off = o;
    s[o++] = 0x75; s[o++] = 0x00;
    // restore rcx/rdx/r8/r9 from the saved slots
    s[o++] = 0x48; s[o++] = 0x8B; s[o++] = 0x4C; s[o++] = 0x24; s[o++] = 0x20;
    s[o++] = 0x48; s[o++] = 0x8B; s[o++] = 0x54; s[o++] = 0x24; s[o++] = 0x28;
    s[o++] = 0x4C; s[o++] = 0x8B; s[o++] = 0x44; s[o++] = 0x24; s[o++] = 0x30;
    s[o++] = 0x4C; s[o++] = 0x8B; s[o++] = 0x4C; s[o++] = 0x24; s[o++] = 0x38;
    // add rsp, 0x50
    s[o++] = 0x48; s[o++] = 0x83; s[o++] = 0xC4; s[o++] = 0x50;
    // pop rbx
    s[o++] = 0x5B;
    // mov rax, trampoline (imm64)
    s[o++] = 0x48; s[o++] = 0xB8;
    *reinterpret_cast<uint64_t*>(s + o) = trampoline; o += 8;
    // jmp rax
    s[o++] = 0xFF; s[o++] = 0xE0;

    // skip path
    const int skip_off = o;
    // add rsp, 0x50
    s[o++] = 0x48; s[o++] = 0x83; s[o++] = 0xC4; s[o++] = 0x50;
    // pop rbx
    s[o++] = 0x5B;
    // xor eax, eax
    s[o++] = 0x31; s[o++] = 0xC0;
    // ret
    s[o++] = 0xC3;

    s[jnz_off + 1] = static_cast<unsigned char>(skip_off - (jnz_off + 2));
    return o;
}

// ---------------------------------------------------------------------------
// Full-path stub (results + stack args + float args). Layout for n = stack-arg slots
// (n = max(0, arg_count - 4), capped at 8); all offsets relative to rsp after
// `sub rsp, FRAME`; R = the entry rsp. The dispatch functions are JIT-compiled
// managed code whose STACK FRAMES grow DOWN from their call site - a layout that
// places any stub data below the dispatch call site gets silently overwritten
// (observed in-game: the args buffer and result slot were clobbered by the JIT
// dispatcher's frame, producing garbage args/results while smoke tests with tiny
// C++ dispatchers passed). So ALL stub state lives ABOVE a reserved scratch zone
// that the dispatch callee may use freely:
//
//   [rsp+0x000 .. 0x140)          DISPATCH CALLEE SCRATCH. The dispatch calls happen
//                                 from rsp = R-FRAME (no rsp switch): shadow space
//                                 [0x00..0x20), 5th arg (return_kind) at [0x20..0x28),
//                                 return address at [-8], and up to ~0x138 bytes of
//                                 callee stack frame below. Nothing of ours lives
//                                 here - the JIT may spill/clobber at will. (The
//                                 trampoline's ABI stack-arg slots [0x20+8k] are
//                                 written here too, but only AFTER the prefix returns
//                                 and right before the trampoline call.)
//   [rsp+0x140 .. 0x180)          xmm0-3 save area (float/double arg regs - the
//                                 dispatch calls would clobber them)
//   [rsp+0x180 .. 0x1A0)          args buffer, register section: rcx/rdx/r8/r9
//   [rsp+0x1A0 .. 0x1A0+0x08n)    args buffer, stack section (n slots) - contiguous
//                                 with the register section, so args[i] = [rsp+buf_regs+8i]
//   [rsp+0x1A0+0x08n .. +0x10)    result slot: rax bits (8) + xmm0 bits (8), zeroed
//   FRAME = 0x1B8 + 0x08n         (≡ 8 mod 16: entry rsp ≡ 8, sub => ≡ 0, so the
//                                 dispatch calls and the trampoline call are all
//                                 16-aligned for their callees; the result slot's
//                                 second qword sits at [FRAME-8 .. FRAME), i.e. just
//                                 below the caller's return address at [R])
//
// Flow: sub → save regs + xmm0-3 (disp32 - offsets exceed disp8) → zero result →
// copy the caller's stack args ([R+0x28+8k]) into the buffer's stack section (the
// prefix must see the full arg list) → prefix dispatch (rsp = R-FRAME; the callee's
// frame grows into the scratch zone, never reaching the buffer/result) → test/jnz →
// [skip block at the end: return result slot] → restore xmm0-3 → reload the register
// args the dispatch clobbered (rcx/rdx/r8/r9 from the buffer) → RE-COPY the caller's
// stack args into the trampoline's ABI slots [rsp+0x20+8k] (the prefix callee's
// frame clobbered the previous copy - this one runs after it returned) → call
// trampoline (reads its stack args from [rsp+0x20+8k]) → save rax/xmm0 → postfix
// dispatch (may rewrite the slot) → restore rax or xmm0 by return_kind → add rsp → ret.
constexpr int kStubSizeFull = 640;

int EmitStubFull(unsigned char* s, uint64_t handle, void* dispatch_prefix,
                 void* dispatch_postfix, int return_kind, int arg_count,
                 uint64_t trampoline) {
    const int n = arg_count > 4 ? arg_count - 4 : 0;
    if (n > 8 || return_kind < 0 || return_kind > 4) {
        return 0;
    }
    const int xmm_off = 0x140;
    const int buf_regs = 0x180;
    const int buf_stack = 0x1A0;
    const int result_off = 0x1A0 + 8 * n;
    const int frame = 0x1B8 + 8 * n;  // result slot is 16 bytes: 0x1A0+8n+0x10 <= 0x1B8+8n
    const bool use_xmm = return_kind == 3 || return_kind == 4;

    int o = 0;
    // sub rsp, frame (imm8 for small frames, imm32 otherwise)
    if (frame <= 0x7F) {
        s[o++] = 0x48; s[o++] = 0x83; s[o++] = 0xEC; s[o++] = static_cast<unsigned char>(frame);
    } else {
        s[o++] = 0x48; s[o++] = 0x81; s[o++] = 0xEC;
        *reinterpret_cast<int*>(s + o) = frame; o += 4;
    }
    // mov [rsp+buf_regs+0x00], rcx / +0x08 rdx / +0x10 r8 / +0x18 r9 (disp32)
    s[o++] = 0x48; s[o++] = 0x89; s[o++] = 0x8C; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs + 0x00; o += 4;
    s[o++] = 0x48; s[o++] = 0x89; s[o++] = 0x94; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs + 0x08; o += 4;
    s[o++] = 0x4C; s[o++] = 0x89; s[o++] = 0x84; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs + 0x10; o += 4;
    s[o++] = 0x4C; s[o++] = 0x89; s[o++] = 0x8C; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs + 0x18; o += 4;
    // save xmm0-3: movdqu [rsp+xmm_off+16k], xmm k (disp32 form - offsets exceed
    // disp8; modrm reg field = k)
    for (int k = 0; k < 4; k++) {
        s[o++] = 0xF3; s[o++] = 0x0F; s[o++] = 0x7F;
        s[o++] = static_cast<unsigned char>(0x84 + 8 * k); s[o++] = 0x24;
        *reinterpret_cast<int*>(s + o) = xmm_off + 16 * k; o += 4;
    }
    // zero the result slot (two mov qword [rsp+disp32], 0)
    s[o++] = 0x48; s[o++] = 0xC7; s[o++] = 0x84; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = result_off; o += 4;
    *reinterpret_cast<int*>(s + o) = 0; o += 4;
    s[o++] = 0x48; s[o++] = 0xC7; s[o++] = 0x84; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = result_off + 8; o += 4;
    *reinterpret_cast<int*>(s + o) = 0; o += 4;

    if (n > 0) {
        // Copy the caller's stack args (5th+ args sit at [R+0x28+8k] where R is the
        // entry rsp: the caller pushed them above its return address) into the
        // buffer's stack section ONLY - the prefix must see the full arg list, but
        // the trampoline's ABI slots [rsp+0x20+8k] would be clobbered by the prefix
        // callee's frame, so those are re-copied AFTER the prefix returns (see
        // below). Clobbers only r10/r11/rax, none live into the dispatch.
        // lea r10, [rsp + frame + 0x28]
        s[o++] = 0x4C; s[o++] = 0x8D; s[o++] = 0x94; s[o++] = 0x24;
        *reinterpret_cast<int*>(s + o) = frame + 0x28; o += 4;
        // lea r11, [rsp + buf_stack]   (buffer stack-section base)
        s[o++] = 0x4C; s[o++] = 0x8D; s[o++] = 0x9C; s[o++] = 0x24;
        *reinterpret_cast<int*>(s + o) = buf_stack; o += 4;
        for (int k = 0; k < n; k++) {
            // mov rax, [r10+8k]   (modrm 0x42: mod01 reg000 rm010 = r10)
            s[o++] = 0x49; s[o++] = 0x8B; s[o++] = 0x42; s[o++] = static_cast<unsigned char>(8 * k);
            // mov [r11+8k], rax        (buffer stack section)
            s[o++] = 0x49; s[o++] = 0x89; s[o++] = 0x43; s[o++] = static_cast<unsigned char>(8 * k);
        }
    }

    // Emits one dispatch call; args already in rcx/rdx/r8/r9; `fn` is the callee.
    // The call happens from rsp = R-FRAME (the frame base - no rsp switch): the
    // callee's shadow is the scratch zone [0x00..0x20), the 5th arg (return_kind)
    // lands at [0x20..0x28), its return address at [-8], and its stack frame grows
    // down through the rest of the scratch - never reaching xmm/buffer/result.
    auto emit_dispatch_call = [&](uint64_t fn) {
        // mov dword [rsp+0x20], return_kind   (5th arg; callee reads [rsp+0x28])
        s[o++] = 0xC7; s[o++] = 0x44; s[o++] = 0x24; s[o++] = 0x20;
        *reinterpret_cast<int*>(s + o) = return_kind; o += 4;
        // mov rax, fn (imm64)
        s[o++] = 0x48; s[o++] = 0xB8;
        *reinterpret_cast<uint64_t*>(s + o) = fn; o += 8;
        // call rax  (callee's ret pops the push; rsp is back on the frame base)
        s[o++] = 0xFF; s[o++] = 0xD0;
    };

    // Prefix dispatch: rcx=handle, rdx=&args buffer, r8d=arg_count, r9=&result slot.
    s[o++] = 0x48; s[o++] = 0xB9;
    *reinterpret_cast<uint64_t*>(s + o) = handle; o += 8;
    s[o++] = 0x48; s[o++] = 0x8D; s[o++] = 0x94; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs; o += 4;
    s[o++] = 0x41; s[o++] = 0xB8;
    *reinterpret_cast<int*>(s + o) = arg_count; o += 4;
    s[o++] = 0x4C; s[o++] = 0x8D; s[o++] = 0x8C; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = result_off; o += 4;
    emit_dispatch_call(reinterpret_cast<uint64_t>(dispatch_prefix));
    // test eax, eax
    s[o++] = 0x85; s[o++] = 0xC0;
    // jnz skip - rel32 (the main path between here and the skip block is long)
    const int jnz_off = o;
    s[o++] = 0x0F; s[o++] = 0x85;
    *reinterpret_cast<int*>(s + o) = 0; o += 4;

    // ---- main path
    // restore xmm0-3 (float args must reach the trampoline intact)
    for (int k = 0; k < 4; k++) {
        s[o++] = 0xF3; s[o++] = 0x0F; s[o++] = 0x6F;
        s[o++] = static_cast<unsigned char>(0x84 + 8 * k); s[o++] = 0x24;
        *reinterpret_cast<int*>(s + o) = xmm_off + 16 * k; o += 4;
    }
    if (n > 0) {
        // Re-copy the caller's stack args into the trampoline's ABI slots
        // [rsp+0x20+8k] - the prefix callee's frame clobbered the earlier copy.
        // The caller's args are still intact at [rsp+frame+0x28+8k].
        // lea r10, [rsp + frame + 0x28]
        s[o++] = 0x4C; s[o++] = 0x8D; s[o++] = 0x94; s[o++] = 0x24;
        *reinterpret_cast<int*>(s + o) = frame + 0x28; o += 4;
        for (int k = 0; k < n; k++) {
            // mov rax, [r10+8k]
            s[o++] = 0x49; s[o++] = 0x8B; s[o++] = 0x42; s[o++] = static_cast<unsigned char>(8 * k);
            // mov [rsp+0x20+8k], rax  (the called trampoline reads its stack args
            // from [rsp+0x20+8k] - inside the scratch zone, but nothing runs
            // between this write and the call below)
            s[o++] = 0x48; s[o++] = 0x89; s[o++] = 0x44; s[o++] = 0x24;
            s[o++] = static_cast<unsigned char>(0x20 + 8 * k);
        }
    }
    // reload the register args the prefix dispatch clobbered (rcx/rdx/r8/r9 from the
    // args buffer, which is at [rsp+buf_regs]; rsp is back on the frame base here)
    s[o++] = 0x48; s[o++] = 0x8B; s[o++] = 0x8C; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs + 0x00; o += 4;
    s[o++] = 0x48; s[o++] = 0x8B; s[o++] = 0x94; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs + 0x08; o += 4;
    s[o++] = 0x4C; s[o++] = 0x8B; s[o++] = 0x84; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs + 0x10; o += 4;
    s[o++] = 0x4C; s[o++] = 0x8B; s[o++] = 0x8C; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs + 0x18; o += 4;
    // mov rax, trampoline; call rax   (trampoline reads stack args from [rsp+0x20+8k])
    s[o++] = 0x48; s[o++] = 0xB8;
    *reinterpret_cast<uint64_t*>(s + o) = trampoline; o += 8;
    s[o++] = 0xFF; s[o++] = 0xD0;
    // save result: mov [rsp+result_off], rax; movq [rsp+result_off+8], xmm0
    s[o++] = 0x48; s[o++] = 0x89; s[o++] = 0x84; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = result_off; o += 4;
    s[o++] = 0x66; s[o++] = 0x0F; s[o++] = 0xD6; s[o++] = 0x84; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = result_off + 8; o += 4;

    // Postfix dispatch (same args as prefix).
    s[o++] = 0x48; s[o++] = 0xB9;
    *reinterpret_cast<uint64_t*>(s + o) = handle; o += 8;
    s[o++] = 0x48; s[o++] = 0x8D; s[o++] = 0x94; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = buf_regs; o += 4;
    s[o++] = 0x41; s[o++] = 0xB8;
    *reinterpret_cast<int*>(s + o) = arg_count; o += 4;
    s[o++] = 0x4C; s[o++] = 0x8D; s[o++] = 0x8C; s[o++] = 0x24;
    *reinterpret_cast<int*>(s + o) = result_off; o += 4;
    emit_dispatch_call(reinterpret_cast<uint64_t>(dispatch_postfix));

    // Restore the (possibly rewritten) result by kind. The slot layout is
    // [result_off] = rax bits, [result_off+8] = xmm0 bits, so xmm results come back
    // from +8.
    if (use_xmm) {
        s[o++] = 0xF3; s[o++] = 0x0F; s[o++] = 0x7E; s[o++] = 0x84; s[o++] = 0x24;
        *reinterpret_cast<int*>(s + o) = result_off + 8; o += 4;
    } else {
        s[o++] = 0x48; s[o++] = 0x8B; s[o++] = 0x84; s[o++] = 0x24;
        *reinterpret_cast<int*>(s + o) = result_off; o += 4;
    }
    // add rsp, frame; ret
    if (frame <= 0x7F) {
        s[o++] = 0x48; s[o++] = 0x83; s[o++] = 0xC4; s[o++] = static_cast<unsigned char>(frame);
    } else {
        s[o++] = 0x48; s[o++] = 0x81; s[o++] = 0xC4;
        *reinterpret_cast<int*>(s + o) = frame; o += 4;
    }
    s[o++] = 0xC3;

    // ---- skip path (at the END - the not-taken jnz falls through to the main path)
    const int skip_off = o;
    if (use_xmm) {
        // movq xmm0, [rsp+result_off+8]  (the xmm0 slot)
        s[o++] = 0xF3; s[o++] = 0x0F; s[o++] = 0x7E; s[o++] = 0x84; s[o++] = 0x24;
        *reinterpret_cast<int*>(s + o) = result_off + 8; o += 4;
    } else {
        // mov rax, [rsp+result_off]
        s[o++] = 0x48; s[o++] = 0x8B; s[o++] = 0x84; s[o++] = 0x24;
        *reinterpret_cast<int*>(s + o) = result_off; o += 4;
    }
    // add rsp, frame; ret
    if (frame <= 0x7F) {
        s[o++] = 0x48; s[o++] = 0x83; s[o++] = 0xC4; s[o++] = static_cast<unsigned char>(frame);
    } else {
        s[o++] = 0x48; s[o++] = 0x81; s[o++] = 0xC4;
        *reinterpret_cast<int*>(s + o) = frame; o += 4;
    }
    s[o++] = 0xC3;

    *reinterpret_cast<int*>(s + jnz_off + 2) = skip_off - (jnz_off + 6);
    return o;
}

// ---------------------------------------------------------------------------
// Shared detour install: measure → allocate trampoline + stub → fixup → emit stub →
// patch the entry. The stub emitter is provided by the caller (fast or full shape).
using EmitStubFn = int (*)(unsigned char* s, void* ctx, uint64_t trampoline);

HookRecord* InstallDetour(void* target, int stub_capacity, EmitStubFn emit_stub, void* emit_ctx) {
    auto* p = static_cast<unsigned char*>(target);

    int call_offsets[4]{};
    int rip_offsets[4]{};

    // Stage 1: the absolute 14-byte jump (mov rax,imm64; jmp rax). Needs 14 clean
    // relocatable bytes; RIP-relative operands are tolerated with disp32 fixup.
    const int abs_len = nami::tide::measure_relocatable_prologue(
        p, 14, /*allow_relative_call=*/false, call_offsets, 4,
        /*allow_rip_relative=*/true, rip_offsets, 4);

    unsigned char* stub = nullptr;
    unsigned char* tramp = nullptr;
    int patch_len = 0;
    bool near_jump = false;

    if (abs_len > 0) {
        patch_len = 14;
        tramp = static_cast<unsigned char*>(nami::tide::build_trampoline(p, abs_len));
        if (tramp == nullptr) {
            return nullptr;
        }
        if (!nami::tide::fixup_relocations(p, tramp, call_offsets, rip_offsets)) {
            VirtualFree(tramp, 0, MEM_RELEASE);
            return nullptr;
        }
        stub = static_cast<unsigned char*>(
            VirtualAlloc(nullptr, stub_capacity, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
        if (stub == nullptr) {
            VirtualFree(tramp, 0, MEM_RELEASE);
            return nullptr;
        }
    } else {
        // Stage 2: the 5-byte relative jump (E9 rel32). IL2CPP leaf getters are
        // tiny (`mov eax, [rip+x]; ret`) - far below 14 bytes but >= 5. Both the
        // stub and the trampoline must sit within ±2GB of the patch site (rel32
        // entry + rel32 back-jump), and any RIP-relative operands in the copied
        // prologue get their disp32 rebased.
        call_offsets[0] = call_offsets[1] = call_offsets[2] = call_offsets[3] = 0;
        rip_offsets[0] = rip_offsets[1] = rip_offsets[2] = rip_offsets[3] = 0;
        const int near_len = nami::tide::measure_relocatable_prologue(
            p, 5, /*allow_relative_call=*/false, call_offsets, 4,
            /*allow_rip_relative=*/true, rip_offsets, 4);
        if (near_len <= 0) {
            return nullptr;  // < 5 clean bytes: refuse, never corrupt
        }

        patch_len = 5;
        stub = static_cast<unsigned char*>(nami::tide::alloc_near(p, stub_capacity));
        if (stub == nullptr) {
            return nullptr;
        }
        tramp = static_cast<unsigned char*>(nami::tide::alloc_near(p, near_len + 8));
        if (tramp == nullptr) {
            VirtualFree(stub, 0, MEM_RELEASE);
            return nullptr;
        }
        for (int i = 0; i < near_len; i++) {
            tramp[i] = p[i];
        }
        if (!nami::tide::fixup_relocations(p, tramp, call_offsets, rip_offsets)) {
            VirtualFree(stub, 0, MEM_RELEASE);
            VirtualFree(tramp, 0, MEM_RELEASE);
            return nullptr;
        }
        // jmp rel32 back to p+near_len (the rest of the original runs in place).
        tramp[near_len] = 0xE9;
        const long long back = (p + near_len) - (tramp + near_len + 5);
        if (back < INT_MIN || back > INT_MAX) {
            VirtualFree(stub, 0, MEM_RELEASE);
            VirtualFree(tramp, 0, MEM_RELEASE);
            return nullptr;  // alloc_near guarantees ±2GB, but never write a wrapped rel32
        }
        *reinterpret_cast<int*>(tramp + near_len + 1) = static_cast<int>(back);
        near_jump = true;
    }

    const int stub_size = emit_stub(stub, emit_ctx, reinterpret_cast<uint64_t>(tramp));
    if (stub_size <= 0 || stub_size > stub_capacity) {
        VirtualFree(stub, 0, MEM_RELEASE);
        VirtualFree(tramp, 0, MEM_RELEASE);
        return nullptr;
    }

    auto* rec = new (std::nothrow) HookRecord{};
    if (rec == nullptr) {
        VirtualFree(stub, 0, MEM_RELEASE);
        VirtualFree(tramp, 0, MEM_RELEASE);
        return nullptr;
    }
    rec->target = p;
    rec->patch_len = patch_len;
    rec->near_jump = near_jump;
    rec->trampoline = tramp;
    rec->stub = stub;
    rec->stub_size = stub_size;
    std::memcpy(rec->original, p, 14);

    DWORD old_protect = 0;
    if (!VirtualProtect(p, patch_len, PAGE_EXECUTE_READWRITE, &old_protect)) {
        delete rec;
        VirtualFree(stub, 0, MEM_RELEASE);
        VirtualFree(tramp, 0, MEM_RELEASE);
        return nullptr;
    }
    if (near_jump) {
        // E9 rel32 → stub (range guaranteed by alloc_near).
        p[0] = 0xE9;
        *reinterpret_cast<int*>(p + 1) = static_cast<int>(stub - (p + 5));
    } else {
        // mov rax, imm64; jmp rax; nop; nop (14 bytes).
        p[0] = 0x48; p[1] = 0xB8;
        *reinterpret_cast<uint64_t*>(p + 2) = reinterpret_cast<uint64_t>(stub);
        p[10] = 0xFF; p[11] = 0xE0;
        p[12] = 0x90; p[13] = 0x90;
    }
    VirtualProtect(p, patch_len, old_protect, &old_protect);

    rec->installed = true;
    return rec;
}

struct FastEmitCtx {
    uint64_t handle;
    void* dispatch;
    int argc;
};

int EmitFastStub(unsigned char* s, void* vctx, uint64_t trampoline) {
    auto* ctx = static_cast<FastEmitCtx*>(vctx);
    return EmitStub(s, ctx->handle, ctx->dispatch, ctx->argc, trampoline);
}

struct FullEmitCtx {
    uint64_t handle;
    void* prefix;
    void* postfix;
    int return_kind;
    int argc;
};

int EmitFullStub(unsigned char* s, void* vctx, uint64_t trampoline) {
    auto* ctx = static_cast<FullEmitCtx*>(vctx);
    return EmitStubFull(s, ctx->handle, ctx->prefix, ctx->postfix, ctx->return_kind,
                        ctx->argc, trampoline);
}

}  // namespace

HookRecord* hook_native_at(void* target, void* dispatch, uint64_t user_handle, int arg_count) {
    if (target == nullptr || dispatch == nullptr || arg_count < 0 || arg_count > 4) {
        return nullptr;
    }
    FastEmitCtx ctx{user_handle, dispatch, arg_count};
    return InstallDetour(target, kStubSize, EmitFastStub, &ctx);
}

HookRecord* hook_native_full(void* target, void* dispatch_prefix, void* dispatch_postfix,
                             int return_kind, uint64_t user_handle, int arg_count) {
    if (target == nullptr || dispatch_prefix == nullptr || dispatch_postfix == nullptr ||
        arg_count < 0 || arg_count > 12 || return_kind < 0 || return_kind > 4) {
        return nullptr;
    }
    FullEmitCtx ctx{user_handle, dispatch_prefix, dispatch_postfix, return_kind, arg_count};
    return InstallDetour(target, kStubSizeFull, EmitFullStub, &ctx);
}

void unhook_native(HookRecord* rec) {
    if (rec == nullptr) {
        return;
    }
    if (rec->installed && rec->target != nullptr) {
        DWORD old_protect = 0;
        if (VirtualProtect(rec->target, rec->patch_len, PAGE_EXECUTE_READWRITE, &old_protect)) {
            std::memcpy(rec->target, rec->original, rec->patch_len);
            VirtualProtect(rec->target, rec->patch_len, old_protect, &old_protect);
        }
        rec->installed = false;
    }
    if (rec->stub != nullptr) {
        VirtualFree(rec->stub, 0, MEM_RELEASE);
        rec->stub = nullptr;
    }
    if (rec->trampoline != nullptr) {
        VirtualFree(rec->trampoline, 0, MEM_RELEASE);
        rec->trampoline = nullptr;
    }
    delete rec;
}

}  // namespace nami::stub