#pragma once

#include <cstdint>

namespace nami::stub {

// ---------------------------------------------------------------------------
// Native dispatch-stub detours for IL2CPP method patching.
//
// An IL2CPP method is plain x64 native code (this in rcx, params in rdx/r8/r9 +
// stack). We install the same safe detour the Tide toolkit builds, but route the
// patched entry through a per-hook native stub.
//
// Two stub shapes:
//
// 1. Prefix-only (fast path): saves the argument registers (rcx/rdx/r8/r9) into a
//    stack array, calls a dispatch function with (user_handle, args, arg_count) -
//    nonzero return = SKIP (returns 0), otherwise restores the argument registers
//    and tail-jumps to the trampoline (the original runs with its exact args; stack
//    arguments beyond r9 are untouched on the caller's stack).
//
// 2. Full path (results + stack args): CALLS the trampoline instead of tail-jumping
//    so the result can be observed, and dispatches twice:
//      int  prefix (user_handle, args, arg_count, result_slot, return_kind)
//      void postfix(user_handle, args, arg_count, result_slot, return_kind)
//    args is a contiguous buffer of arg_count raw 8-byte slots (register args
//    rcx/rdx/r8/r9 first, then the caller's stack args), so all arguments are
//    observable. The result slot is 16 bytes: [0] = rax bits, [1] = xmm0 bits
//    (floating-point returns). The prefix may write the slot before returning
//    nonzero (skip with a replacement result); the postfix may rewrite the slot
//    after the original ran. return_kind tells the stub which register to restore:
//    0 = void, 1 = i32 (rax), 2 = i64/pointer (rax), 3 = f32 (xmm0), 4 = f64 (xmm0).
//    Stack args are copied into the stub's own frame and re-presented to the
//    trampoline at the ABI-mandated offset, so the original still sees them.
//
// The dispatch ABI is plain x64 cdecl in both shapes. Dispatch calls run on the
// game main thread (window-proc executor).
// ---------------------------------------------------------------------------

struct HookRecord {
    unsigned char* target;         // patched address
    unsigned char original[14];    // saved bytes for exact restore
    int patch_len;                 // 14 (absolute jump) or 5 (relative jump)
    bool near_jump;                // true = E9 rel32 form (short prologue)
    unsigned char* trampoline;     // relocated prologue + jump back ("the original")
    unsigned char* stub;           // dispatch stub (executable)
    int stub_size;
    bool installed;
};

/// Installs a prefix-only dispatch-stub detour over `target` (fast path - see above).
/// Preferred form: 14-byte absolute jump; falls back to a 5-byte relative jump when
/// the prologue is >= 5 clean bytes but < 14 (IL2CPP leaf getters like
/// `mov eax, [rip+x]; ret`) - RIP-relative operands are relocated with disp32 fixup.
/// Refuses only below 5 clean bytes or on genuinely unsafe code - never corrupts.
/// Returns the record (owned by the caller, free with unhook_native) or nullptr.
HookRecord* hook_native_at(void* target, void* dispatch, uint64_t user_handle, int arg_count);

/// Installs a full-path dispatch-stub detour (results + stack args - see above).
/// `arg_count` may exceed 4 (stack args are copied and exposed); capped at 12.
/// Same prologue policy and safety guarantees as hook_native_at.
HookRecord* hook_native_full(void* target, void* dispatch_prefix, void* dispatch_postfix,
                             int return_kind, uint64_t user_handle, int arg_count);

/// Restores the original bytes exactly and frees trampoline + stub. Idempotent.
void unhook_native(HookRecord* rec);

}  // namespace nami::stub