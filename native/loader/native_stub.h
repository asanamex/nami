#pragma once

#include <cstdint>

namespace nami::stub {

// ---------------------------------------------------------------------------
// Native dispatch-stub detours for IL2CPP method patching.
//
// An IL2CPP method is plain x64 native code (this in rcx, params in rdx/r8/r9 +
// stack). We install the same safe detour the Tide toolkit builds, but route the
// patched entry through a per-hook native stub that:
//
//   1. saves the argument registers (rcx/rdx/r8/r9) into a stack array,
//   2. calls a dispatch function (provided by the managed side) with
//      (user_handle, args_array, arg_count) — returning nonzero = SKIP,
//   3. on skip: returns 0 (value-typed returns are out of v1 scope),
//   4. otherwise restores the argument registers and tail-jumps to the
//      trampoline (the original runs with its original args; stack arguments
//      beyond r9 are untouched on the caller's stack).
//
// This mirrors Wave's M1 gate/observer semantics (raw-pointer observer + skip)
// for game-side native methods. The dispatch ABI is plain x64 cdecl:
//   int dispatch(uint64_t user_handle, uint64_t* args /* 4 slots */, int arg_count)
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

/// Installs a dispatch-stub detour over `target`. `dispatch` is the native callable
/// invoked with (user_handle, args, arg_count); the stub tail-jumps to the trampoline
/// unless dispatch returns nonzero (skip). Preferred form: 14-byte absolute jump.
/// Falls back to a 5-byte relative jump when the prologue is >= 5 clean bytes but
/// < 14 (IL2CPP leaf getters like `mov eax, [rip+x]; ret`) — RIP-relative operands
/// are relocated with disp32 fixup. Refuses only below 5 clean bytes or on genuinely
/// unsafe code — never corrupts. Returns the record (owned by the caller, free with
/// unhook_native) or nullptr on any failure.
HookRecord* hook_native_at(void* target, void* dispatch, uint64_t user_handle, int arg_count);

/// Restores the original bytes exactly and frees trampoline + stub. Idempotent.
void unhook_native(HookRecord* rec);

}  // namespace nami::stub