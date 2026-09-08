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

#ifdef __cplusplus
extern "C" {
#endif

// Resolves <assembly>/<ns>/<klass>.<method> (arity = argc) and installs a dispatch-stub
// detour. `dispatch` is a native callable invoked on the game main thread with
// (user_handle, args /* 4 raw arg-register slots */, arg_count); nonzero return = skip.
// Returns 0 on success and fills *trampoline_out (call it for the original behavior)
// and *hook_id_out (for nami_il2cpp_unhook). -1 = resolution/install failure (see
// nami-tide.log), -3 = main-thread executor unavailable.
__declspec(dllexport) int nami_il2cpp_hook(const char* assembly, const char* ns,
                                           const char* klass, const char* method, int argc,
                                           uint64_t dispatch, uint64_t user_handle,
                                           uint64_t* trampoline_out, uint64_t* hook_id_out);

// Restores the original bytes exactly and releases the hook. 0 on success, -1 unknown id.
__declspec(dllexport) int nami_il2cpp_unhook(uint64_t hook_id);

#ifdef __cplusplus
}
#endif