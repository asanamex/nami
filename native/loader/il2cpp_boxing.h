#pragma once

// Shared IL2CPP primitive-boxing vocabulary (single copy).
//
// `tide_il2cpp_ops.cpp` (typed Tide calls) and `tide_il2cpp_patch.cpp` (typed hook
// frames) both need the same rule: which TideValue primitives box into which
// System.* wrapper, how the raw bytes pack, and the box call itself. Two copies of
// that table drifting independently is a silent-behavior-difference bug (a type added
// on one path but not the other), so both translation units delegate here.
// The Mono backend (`tide_objects.cpp`) boxes through mono_value_box and is untouched.
//
// The header is dependency-light (tide_abi.h + cstdint only): the host supplies
// already-resolved exports via BoxHost and the mscorlib image via its own lookup,
// so property-style unit tests (native/smoke) can include it without a game.

#include "tide_abi.h"

#include <cstdint>

namespace nami::il2cpp::box {

// The System.* wrapper class for a boxable TideValue type, or null when the type
// has no boxed form (String/Object are already objects; Void boxes to nothing).
inline const char* BoxedClassName(nami::tide::TideValueType type) {
    using namespace nami::tide;
    switch (type) {
        case TideType_I32: return "Int32";
        case TideType_Bool: return "Boolean";
        case TideType_I64: return "Int64";
        case TideType_R4: return "Single";
        case TideType_R8: return "Double";
        default: return nullptr;
    }
}

// Packs a boxable primitive's raw bytes (little-endian, zero-extended to 8).
// Mirrors the historical box_into_class packing exactly (Bool as int32 0/1).
// `raw` must point at 8 aligned bytes (call sites use alignas(8)).
inline bool PackBoxedRaw(const nami::tide::TideValue& v, unsigned char raw[8]) {
    using namespace nami::tide;
    for (int i = 0; i < 8; i++) {
        raw[i] = 0;
    }
    switch (v.type) {
        case TideType_I32:
            *reinterpret_cast<int32_t*>(raw) = v.data.i32;
            return true;
        case TideType_Bool:
            *reinterpret_cast<int32_t*>(raw) = v.data.boolean ? 1 : 0;
            return true;
        case TideType_I64:
            *reinterpret_cast<int64_t*>(raw) = v.data.i64;
            return true;
        case TideType_R4:
            *reinterpret_cast<float*>(raw) = v.data.r4;
            return true;
        case TideType_R8:
            *reinterpret_cast<double*>(raw) = v.data.r8;
            return true;
        default:
            return false;
    }
}

// Minimal resolved-export view a boxing call needs. Populated from the caller's
// own API table (no re-resolution, no ownership transfer).
struct BoxHost {
    void* (*class_from_name)(void*, const char*, const char*) = nullptr;
    void* (*value_box)(void*, void*) = nullptr;
};

// Boxes a primitive into its System.* wrapper (for `object` params/slots).
// Returns the boxed Il2CppObject*, or null when the value is not boxable, the
// image/host is missing, or the box call fails. Null-host/image safe by contract.
inline void* BoxPrimitive(const BoxHost& host, void* corlib_image,
                          const nami::tide::TideValue& v) {
    if (corlib_image == nullptr || host.class_from_name == nullptr ||
        host.value_box == nullptr) {
        return nullptr;
    }
    const char* boxed_class = BoxedClassName(v.type);
    if (boxed_class == nullptr) {
        return nullptr;
    }
    void* klass = host.class_from_name(corlib_image, "System", boxed_class);
    if (klass == nullptr) {
        return nullptr;
    }
    alignas(8) unsigned char raw[8];
    if (!PackBoxedRaw(v, raw)) {
        return nullptr;
    }
    return host.value_box(klass, raw);
}

}  // namespace nami::il2cpp::box
