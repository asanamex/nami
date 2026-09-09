#pragma once

#include "tide_abi.h"

#include <cstddef>
#include <cstdint>

namespace nami::il2cpp::patch {

// A deliberately small, ABI-safe signature vocabulary for typed native hooks. Complex
// value types, byrefs, generic instances and hidden struct returns are refused during
// installation instead of being guessed from their size.
enum TypedHookKind : uint8_t {
    TypedHook_Unsupported = 0,
    TypedHook_Bool = 1,
    TypedHook_I32 = 2,
    TypedHook_I64 = 3,
    TypedHook_R4 = 4,
    TypedHook_R8 = 5,
    TypedHook_String = 6,
    TypedHook_Object = 7,
    TypedHook_Void = 8,
};

enum TypedHookLocation : uint8_t {
    TypedHook_Gp = 0,
    TypedHook_Xmm = 1,
    TypedHook_Stack = 2,
    TypedHook_ResultRax = 3,
    TypedHook_ResultXmm = 4,
};

struct TypedHookValueSpec {
    uint8_t kind;
    uint8_t location;
    uint8_t position;
    uint8_t width;
};

struct TypedHookSignature {
    uint32_t version;
    uint32_t user_arg_count;
    uint32_t machine_arg_count;
    uint32_t instance_method;
    uint32_t hidden_method;
    TypedHookValueSpec args[13];
    TypedHookValueSpec result;
};

static_assert(sizeof(TypedHookValueSpec) == 4);
static_assert(offsetof(TypedHookSignature, args) == 20);

}  // namespace nami::il2cpp::patch

// IL2CPP method patching (Wave IL2CPP backend).
//
// IL2CPP methods are native x64 code inside GameAssembly.dll; there is no managed
// method to detour (Wave's IL-copy engine is CoreCLR-only). Instead we resolve the
// Il2CppMethodInfo for a class method (il2cpp_class_get_method_from_name), take its
// native entry (methodPointer, +0 - the first field in every metadata layout v24-v39),
// follow any leading jump thunks, and install a dispatch-stub detour (native_stub.h)
// that routes calls through the managed dispatch with raw argument pointers.
//
// Everything that touches the il2cpp VM runs on the game's main thread (window-proc
// executor), exactly like the typed ops.
//
// Raw v1 scope (honest): prefix observer + skip semantics, raw pointer arguments
// (rcx/rdx/r8/r9 - instance methods see `this` in args[0]); value-typed returns are
// not observable (skip returns 0). The FULL raw path (pass dispatch_postfix != 0) observes results and stack args: the
// stub CALLS the trampoline, dispatches prefix + postfix with a 16-byte result slot
// (rax/xmm0 bits) and return_kind (0=void, 1=i32, 2=i64/ptr, 3=f32, 4=f64), and
// exposes all arg_count arguments as contiguous raw slots (regs first, then stack;
// arg_count <= 12). Prefix may supply a replacement result on skip; postfix may
// rewrite the slot. Stack out-params are not observable on the full path (the stub
// re-presents args from its own copy) - prefix-only hooks preserve them.
// HookTyped is the v2 typed path: it resolves the exported signature APIs at install time,
// decodes the supported TideValue subset across GP/XMM/stack locations, and refuses
// arbitrary structs, ref/out values, hidden returns, and ambiguous overloads.

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

// Typed full-path hook. `expected_types` is optional; when supplied it disambiguates
// overloads using TideType values (one entry per user parameter). `expected_return` is
// -1 to infer from metadata, otherwise a TideType value used as an additional check.
// The typed dispatch ABI receives (user_handle, TypedHookFrame*). The native frame is
// valid only during the callback and its context accessors must be used on the game thread.
__declspec(dllexport) int nami_il2cpp_hook_typed(
    const char* assembly, const char* ns, const char* klass, const char* method, int argc,
    const int32_t* expected_types, int expected_count, int expected_return,
    uint64_t dispatch_prefix, uint64_t dispatch_postfix, uint64_t user_handle,
    uint64_t* hook_id_out);

// Typed frame accessors used by WaveIl2Cpp.Il2CppHookContext. They execute synchronously
// on the current game-main-thread callback and use TideValue's existing ABI.
__declspec(dllexport) int nami_il2cpp_hook_frame_arg_count(void* frame);
__declspec(dllexport) int nami_il2cpp_hook_frame_is_instance(void* frame);
__declspec(dllexport) int nami_il2cpp_hook_frame_get_type(void* frame, int index);
__declspec(dllexport) int nami_il2cpp_hook_frame_get(void* frame, int index, nami::tide::TideValue* value);
__declspec(dllexport) int nami_il2cpp_hook_frame_set(void* frame, int index,
                                                      const nami::tide::TideValue* value);
// Borrowed receiver read for instance-method hooks: decodes the native instance slot
// (machine arg 0) into a TideValue Object handle using the same temporary-handle scope
// as argument reads - freed by nami_il2cpp_hook_frame_cleanup after dispatch. Returns
// -1 when there is no receiver (static method) or the frame is invalid; the managed
// wrapper must NOT dispose the borrowed handle.
__declspec(dllexport) int nami_il2cpp_hook_frame_get_this(void* frame, nami::tide::TideValue* value);
__declspec(dllexport) int nami_il2cpp_hook_frame_get_result_type(void* frame);
__declspec(dllexport) int nami_il2cpp_hook_frame_get_result(void* frame, nami::tide::TideValue* value);
__declspec(dllexport) int nami_il2cpp_hook_frame_set_result(void* frame,
                                                               const nami::tide::TideValue* value);
__declspec(dllexport) void nami_il2cpp_hook_frame_cleanup(void* frame);

// Restores the original bytes exactly and releases the hook. 0 on success, -1 unknown id.
__declspec(dllexport) int nami_il2cpp_unhook(uint64_t hook_id);

#ifdef __cplusplus
}
#endif