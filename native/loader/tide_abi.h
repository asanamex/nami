#pragma once

#include <cstdint>

namespace nami::tide {

// ---------------------------------------------------------------------------
// Tide value ABI. CoreCLR and the native ops exchange TYPED values through a
// fixed array of slots. Object references travel as opaque 64-bit handles that
// index into a native handle table backed by Mono GCHandles (so the game GC
// keeps them alive and CoreCLR never holds raw MonoObject pointers).
// ---------------------------------------------------------------------------

enum TideValueType : int32_t {
    TideType_Void = 0,
    TideType_I32 = 1,
    TideType_I64 = 2,
    TideType_R4 = 3,
    TideType_R8 = 4,
    TideType_Bool = 5,
    TideType_String = 6,   // value carried as a CoreCLR-owned UTF-8 buffer (see below)
    TideType_Object = 7,   // value is a 64-bit handle from the handle table
};

// A single typed value slot. Strings are NOT inline: the caller passes a pointer
// to a UTF-8 buffer + length in the request; the op reads it while on the main thread.
union TideValueData {
    int32_t i32;
    int64_t i64;
    float r4;
    double r8;
    int32_t boolean;   // 0/1
    int64_t handle;    // for TideType_Object
    // For TideType_String: { const char* utf8; int32 len; } packed below via fields.
    struct {
        const char* utf8;
        int32_t len;
    } str;
};

struct TideValue {
    TideValueType type;
    TideValueData data;
};

// Capability sub-ops for nami_tide_call.
enum TideCallOp : int32_t {
    TideCall_GetStaticField = 1,   // target: class (assembly/ns/klass); field: name
    TideCall_SetStaticField = 2,   // target: class; field: name; value: args[0]
    TideCall_InvokeStatic = 3,     // target: class; method: name; args[0..n]; ret in ret
    TideCall_InvokeInstance = 4,   // target: instance (args[0].handle); method; args[1..n]
    TideCall_GetInstanceField = 5, // target: instance (args[0].handle); field
    TideCall_SetInstanceField = 6, // target: instance; field; value in args[1]
    TideCall_NewObject = 7,        // create instance of class; returns handle in ret
    TideCall_FreeHandle = 8,       // release a handle (args[0].handle)
};

// The single request shape. All strings are fixed-size ANSI buffers for class/field/method
// names; VALUES are pointers to caller-owned TideValue arrays (read on the main thread).
struct CallRequest {
    // Class target.
    char assembly[160];
    char ns[160];
    char klass[160];
    // Member.
    char member[160];
    // Operation.
    TideCallOp op;
    // Values.
    const TideValue* args;     // caller-owned array; valid for the duration of the call
    int32_t arg_count;
    TideValue* ret;            // optional single return slot, written by the op
    int32_t handle_capacity;   // max handles the op may create (0 = no handle table)
    int32_t result_code;       // 0 on success, negative on failure (set by the op)
};

// Handle table API used by the ops.
int64_t handle_store_create(void* mono_object);  // returns an opaque handle
void* handle_store_resolve(int64_t handle);
void handle_store_release(int64_t handle);

}  // namespace nami::tide
