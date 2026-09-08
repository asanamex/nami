#pragma once

#include <cstdint>

// IL2CPP method patching (Wave IL2CPP backend).
//
// IL2CPP methods are native x64 code inside GameAssembly.dll; there is no managed
// method to detour (Wave's IL-copy engine is CoreCLR-only). Instead we resolve the
// Il2CppMethodInfo for a class method (il2cpp_class_get_method_from_name), take its
// native entry (methodPointer, +0 — the first field in every metadata layout v24-v39),
// follow any leading jump thunks, and install a dispatch-stub detour (native_stub.h)
// that routes calls through the managed dispatch with raw argument pointers.
//
// Everything that touches the il2cpp VM runs on the game's main thread (window-proc
// executor), exactly like the typed ops.
//
// v1 scope (honest): prefix observer + skip semantics, raw pointer arguments
// (rcx/rdx/r8/r9 — instance methods see `this` in args[0]); value-typed returns are
// not observable (skip returns 0). Stack arguments (5+) and argument/result
// marshaling arrive with the next slice.
//
// The FULL path (pass dispatch_postfix != 0) observes results and stack args: the
// stub CALLS the trampoline, dispatches prefix + postfix with a 16-byte result slot
// (rax/xmm0 bits) and return_kind (0=void, 1=i32, 2=i64/ptr, 3=f32, 4=f64), and
// exposes all arg_count arguments as contiguous raw slots (regs first, then stack;
// arg_count <= 12). Prefix may supply a replacement result on skip; postfix may
// rewrite the slot. Stack out-params are not observable on the full path (the stub
// re-presents args from its own copy) — prefix-only hooks preserve them.

#ifdef __cplusplus
extern "C" {
#endif

// Resolves <assembly>/<ns>/<klass>.<method> (arity = argc) and installs a dispatch-stub
// detour. `dispatch` is a native callable invoked on the game main thread with
// (user_handle, args /* raw arg slots */, arg_count); nonzero return = skip.
// With dispatch_postfix == 0 this is the prefix-only fast path (argc <= 4). With a
// non-null dispatch_postfix it is the full path: dispatch and dispatch_postfix are
// called with (user_handle, args, arg_count, result_slot, return_kind) around the
// trampoline call, argc may be up to 12 (stack args exposed), and return_kind
// (0..4) selects the result register the stub restores.
// Returns 0 on success and fills *trampoline_out (call it for the original behavior)
// and *hook_id_out (for nami_il2cpp_unhook). -1 = resolution/install failure (see
// nami-tide.log), -3 = main-thread executor unavailable.
__declspec(dllexport) int nami_il2cpp_hook(const char* assembly, const char* ns,
                                           const char* klass, const char* method, int argc,
                                           uint64_t dispatch, uint64_t dispatch_postfix,
                                           int return_kind, uint64_t user_handle,
                                           uint64_t* trampoline_out, uint64_t* hook_id_out);

// Restores the original bytes exactly and releases the hook. 0 on success, -1 unknown id.
__declspec(dllexport) int nami_il2cpp_unhook(uint64_t hook_id);

#ifdef __cplusplus
}
#endif