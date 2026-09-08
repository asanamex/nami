// Native dispatch-stub detours (see native_stub.h).

#include "native_stub.h"

#include "tide_pump.h"

#include <windows.h>

#include <cstdlib>
#include <cstring>
#include <new>

namespace nami::stub {

namespace {

// The stub's machine code. Layout (offsets patched at build time):
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
//   call rax                      ; shadow space [rsp+0x00..0x20) — BELOW the args
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
// [rsp+0x20..0x40] — below the saved rbx ([rsp+0x50]) and above the dispatch call's
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

}  // namespace

HookRecord* hook_native_at(void* target, void* dispatch, uint64_t user_handle, int arg_count) {
    if (target == nullptr || dispatch == nullptr) {
        return nullptr;
    }
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
            VirtualAlloc(nullptr, kStubSize, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
        if (stub == nullptr) {
            VirtualFree(tramp, 0, MEM_RELEASE);
            return nullptr;
        }
    } else {
        // Stage 2: the 5-byte relative jump (E9 rel32). IL2CPP leaf getters are
        // tiny (`mov eax, [rip+x]; ret`) — far below 14 bytes but >= 5. Both the
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
        stub = static_cast<unsigned char*>(nami::tide::alloc_near(p, kStubSize));
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

    const int stub_size = EmitStub(stub, user_handle, dispatch, arg_count,
                                   reinterpret_cast<uint64_t>(tramp));
    if (stub_size <= 0 || stub_size > kStubSize) {
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