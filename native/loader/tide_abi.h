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

// Result codes returned by the op exports (negative = failure).
enum TideResult : int32_t {
    TideResult_Ok = 0,
    TideResult_ApiNotReady = -1,   // mono exports not resolved / not on main thread
    TideResult_NotFound = -1,      // assembly/class/member not found (aliased: see docs)
    TideResult_InvalidArg = -1,    // bad argument shape (aliased)
    TideResult_MonoException = -2, // the invoked game method threw a Mono exception
    TideResult_PumpUnavailable = -3, // main-thread drain not installed / timeout
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
    TideCall_ArrayLength = 9,      // args[0].handle is a System.Array; I32 length in ret
    TideCall_ArrayGet = 10,        // args[0].handle array, args[1].i32 index; element in ret
                                   //   (string -> UTF-8, primitive -> typed, object -> handle)
    TideCall_ArraySet = 11,        // args[0].handle array, args[1].i32 index, args[2] value
    TideCall_FindObject = 12,      // First loaded object of <klass> via FindObjectsOfType
                                    // + element 0 (the singular FindObjectOfType wrapper aborts
                                    // the process when invoked outside managed game code).
                                    // Must run via the WINDOW export (frame boundary): the
                                    // invoke drain still nests inside the game's in-flight
                                    // invoke. Handle or 0 in ret.
};

// The single request shape. All strings are fixed-size ANSI buffers for class/field/method
// names; VALUES are pointers to caller-owned TideValue arrays (read on the main thread).
// `result_code` is one of TideResult; on TideResult_MonoException the op fills
// `error_message` (fixed UTF-8 buffer, best-effort truncated) with the Mono exception's
// ToString() so the managed side can surface WHY a game call failed.
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
    int32_t result_code;       // TideResult; filled by the op (see above)
    char error_message[512];   // UTF-8 Mono exception text (set on TideResult_MonoException)
};

// Handle table API used by the ops.
int64_t handle_store_create(void* mono_object);  // returns an opaque handle
void* handle_store_resolve(int64_t handle);
void handle_store_release(int64_t handle);

// Batch execution: N CallRequests submitted in ONE main-thread round trip.
// `requests[i]` are caller-owned request buffers (filled as for the single-op
// export); per-op results are written back into each request (result_code, ret
// slot, error_message) exactly like the single-op path, AND mirrored into
// `codes[i]` for quick scanning. An op that fails does not stop the batch:
// every request runs, codes carry per-op outcomes. Executed on the game main
// thread (standard drain for Mono; the window/IL2CPP executor for IL2CPP).
struct BatchRequest {
    CallRequest** requests;  // caller-owned array of `count` pointers
    int32_t* codes;          // caller-owned, `count` slots, filled by the op
    int32_t count;
};

}  // namespace nami::tide
