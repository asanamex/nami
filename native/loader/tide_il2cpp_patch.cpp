// IL2CPP method patching (see tide_il2cpp_patch.h).

#include "il2cpp_boxing.h"
#include "tide_il2cpp_patch.h"

#include "native_stub.h"
#include "tide_il2cpp.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <new>
#include <string>
#include <unordered_map>

namespace nami::il2cpp::patch {

namespace {

void log_tide(const char* fmt, ...) {
    wchar_t path[MAX_PATH]{};
    const HMODULE self = GetModuleHandleW(L"nami_loader.dll");
    if (self != nullptr) {
        GetModuleFileNameW(self, path, MAX_PATH);
        wchar_t* slash = wcsrchr(path, L'\\');
        if (slash != nullptr) {
            wcscpy_s(slash + 1, MAX_PATH - static_cast<size_t>(slash + 1 - path),
                     L"nami-tide.log");
        }
    }
    FILE* f = nullptr;
    if (path[0] != L'\0' && _wfopen_s(&f, path, L"a") == 0 && f != nullptr) {
        va_list ap;
        va_start(ap, fmt);
        std::vfprintf(f, fmt, ap);
        va_end(ap);
        std::fputc('\n', f);
        std::fclose(f);
    }
}

// The il2cpp export surface needed for method resolution. Resolved lazily on the
// main thread (the only context where the VM is safe).
struct PatchApi {
    void* (*domain_get)() = nullptr;
    void* (*domain_assembly_open)(void*, const char*) = nullptr;
    void* (*assembly_get_image)(void*) = nullptr;
    void* (*class_from_name)(void*, const char*, const char*) = nullptr;
    void* (*class_get_parent)(void*) = nullptr;
    void* (*class_get_method_from_name)(void*, const char*, int) = nullptr;
    void* (*class_get_methods)(void*, void**) = nullptr;
    const char* (*method_get_name)(void*) = nullptr;
    uint32_t (*method_get_param_count)(void*) = nullptr;
    const void* (*method_get_param)(void*, uint32_t) = nullptr;
    uint32_t (*method_get_flags)(void*, uint32_t*) = nullptr;
    void* (*method_get_return_type)(void*) = nullptr;
    int (*method_is_generic)(void*) = nullptr;
    int (*method_is_inflated)(void*) = nullptr;
    int (*type_get_type)(void*) = nullptr;
    int (*type_is_byref)(void*) = nullptr;
    void* (*class_from_type)(void*) = nullptr;
    int (*class_is_enum)(void*) = nullptr;
    const void* (*class_enum_basetype)(void*) = nullptr;
    void* (*object_get_class)(void*) = nullptr;
    void* (*string_new)(const char*) = nullptr;
    const void* (*string_chars)(void*) = nullptr;
    int32_t (*string_length)(void*) = nullptr;
    uint64_t (*gchandle_new)(void*, int32_t) = nullptr;
    void* (*gchandle_get_target)(uint64_t) = nullptr;
    void (*gchandle_free)(uint64_t) = nullptr;
    // Optional: present on all verified titles; when missing only primitive-to-Object
    // boxing refuses (-1) while exact-type hook reads/writes keep working.
    void* (*value_box)(void*, void*) = nullptr;
    bool ready = false;
};

PatchApi g_api{};

bool resolve_api() {
    if (g_api.ready) {
        return true;
    }
    HMODULE ga = GetModuleHandleW(L"GameAssembly.dll");
    if (ga == nullptr) {
        return false;
    }
    auto load = [&](const char* name) -> void* {
        // Call the EXPORT address (E9 jmp-thunk), never the followed body (Unity 6000
        // register conventions) - same rule as tide_il2cpp_ops.cpp.
        return reinterpret_cast<void*>(GetProcAddress(ga, name));
    };
    g_api.domain_get = reinterpret_cast<decltype(g_api.domain_get)>(load("il2cpp_domain_get"));
    g_api.domain_assembly_open = reinterpret_cast<decltype(g_api.domain_assembly_open)>(
        load("il2cpp_domain_assembly_open"));
    g_api.assembly_get_image =
        reinterpret_cast<decltype(g_api.assembly_get_image)>(load("il2cpp_assembly_get_image"));
    g_api.class_from_name =
        reinterpret_cast<decltype(g_api.class_from_name)>(load("il2cpp_class_from_name"));
    g_api.class_get_parent =
        reinterpret_cast<decltype(g_api.class_get_parent)>(load("il2cpp_class_get_parent"));
    g_api.class_get_method_from_name = reinterpret_cast<decltype(g_api.class_get_method_from_name)>(
        load("il2cpp_class_get_method_from_name"));
    g_api.class_get_methods =
        reinterpret_cast<decltype(g_api.class_get_methods)>(load("il2cpp_class_get_methods"));
    g_api.method_get_name =
        reinterpret_cast<decltype(g_api.method_get_name)>(load("il2cpp_method_get_name"));
    g_api.method_get_param_count = reinterpret_cast<decltype(g_api.method_get_param_count)>(
        load("il2cpp_method_get_param_count"));
    g_api.method_get_param =
        reinterpret_cast<decltype(g_api.method_get_param)>(load("il2cpp_method_get_param"));
    g_api.method_get_flags =
        reinterpret_cast<decltype(g_api.method_get_flags)>(load("il2cpp_method_get_flags"));
    g_api.method_get_return_type = reinterpret_cast<decltype(g_api.method_get_return_type)>(
        load("il2cpp_method_get_return_type"));
    g_api.method_is_generic =
        reinterpret_cast<decltype(g_api.method_is_generic)>(load("il2cpp_method_is_generic"));
    g_api.method_is_inflated =
        reinterpret_cast<decltype(g_api.method_is_inflated)>(load("il2cpp_method_is_inflated"));
    g_api.type_get_type =
        reinterpret_cast<decltype(g_api.type_get_type)>(load("il2cpp_type_get_type"));
    g_api.type_is_byref =
        reinterpret_cast<decltype(g_api.type_is_byref)>(load("il2cpp_type_is_byref"));
    g_api.class_from_type =
        reinterpret_cast<decltype(g_api.class_from_type)>(load("il2cpp_class_from_type"));
    g_api.class_is_enum =
        reinterpret_cast<decltype(g_api.class_is_enum)>(load("il2cpp_class_is_enum"));
    g_api.class_enum_basetype = reinterpret_cast<decltype(g_api.class_enum_basetype)>(
        load("il2cpp_class_enum_basetype"));
    g_api.object_get_class =
        reinterpret_cast<decltype(g_api.object_get_class)>(load("il2cpp_object_get_class"));
    g_api.string_new =
        reinterpret_cast<decltype(g_api.string_new)>(load("il2cpp_string_new"));
    g_api.string_chars =
        reinterpret_cast<decltype(g_api.string_chars)>(load("il2cpp_string_chars"));
    g_api.string_length =
        reinterpret_cast<decltype(g_api.string_length)>(load("il2cpp_string_length"));
    g_api.gchandle_new =
        reinterpret_cast<decltype(g_api.gchandle_new)>(load("il2cpp_gchandle_new"));
    g_api.gchandle_get_target = reinterpret_cast<decltype(g_api.gchandle_get_target)>(
        load("il2cpp_gchandle_get_target"));
    g_api.gchandle_free =
        reinterpret_cast<decltype(g_api.gchandle_free)>(load("il2cpp_gchandle_free"));

    // Optional boxing export: absence only disables primitive-to-Object writes.
    g_api.value_box =
        reinterpret_cast<decltype(g_api.value_box)>(load("il2cpp_value_box"));

    g_api.ready = g_api.domain_get != nullptr && g_api.domain_assembly_open != nullptr &&
                  g_api.assembly_get_image != nullptr && g_api.class_from_name != nullptr &&
                  g_api.class_get_method_from_name != nullptr;
    if (!g_api.ready) {
        log_tide("il2cpp patch: required exports not found");
    }
    return g_api.ready;
}

bool IsExecutable(const void* p) {
    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(p, &mbi, sizeof(mbi)) == 0) {
        return false;
    }
    return (mbi.Protect & 0xF0) != 0;  // any EXECUTE* protection
}

// Follows leading jump thunks (E9 rel32, FF 25 disp32) like the managed Wave resolver -
// some il2cpp method pointers are shared-generic thunks or export-style trampolines.
void* FollowJumpStubs(void* entry) {
    auto* p = static_cast<unsigned char*>(entry);
    if (!IsExecutable(p)) {
        return nullptr;
    }
    for (int hops = 0; hops < 8; hops++) {
        if (p[0] == 0xE9) {
            const int rel = *reinterpret_cast<int*>(p + 1);
            p = p + 5 + rel;
            if (!IsExecutable(p)) {
                return nullptr;
            }
            continue;
        }
        if (p[0] == 0xFF && p[1] == 0x25) {
            const int disp = *reinterpret_cast<int*>(p + 2);
            auto** slot = reinterpret_cast<unsigned char**>(p + 6 + disp);
            p = *slot;
            if (!IsExecutable(p)) {
                return nullptr;
            }
            continue;
        }
        break;
    }
    return p;
}

// ---------------------------------------------------------------------------
// Typed signature and frame marshaling
// ---------------------------------------------------------------------------

enum : int {
    kTypeVoid = 0x01,
    kTypeBoolean = 0x02,
    kTypeI1 = 0x04,
    kTypeU1 = 0x05,
    kTypeI2 = 0x06,
    kTypeU2 = 0x07,
    kTypeI4 = 0x08,
    kTypeU4 = 0x09,
    kTypeI8 = 0x0A,
    kTypeU8 = 0x0B,
    kTypeR4 = 0x0C,
    kTypeR8 = 0x0D,
    kTypeString = 0x0E,
    kTypePtr = 0x0F,
    kTypeByRef = 0x10,
    kTypeValueType = 0x11,
    kTypeClass = 0x12,
    kTypeArray = 0x14,
    kTypeGenericInst = 0x15,
    kTypeObject = 0x1C,
    kTypeSzArray = 0x1D,
};

constexpr uint32_t kTypedSignatureVersion = 1;
constexpr uint32_t kMethodAttrStatic = 0x0010;
constexpr uint32_t kMethodAttrVirtual = 0x0040;

bool typed_api_ready() {
    return g_api.class_get_parent != nullptr && g_api.class_get_methods != nullptr &&
           g_api.method_get_name != nullptr && g_api.method_get_param_count != nullptr &&
           g_api.method_get_param != nullptr && g_api.method_get_flags != nullptr &&
           g_api.method_get_return_type != nullptr && g_api.method_is_generic != nullptr &&
           g_api.method_is_inflated != nullptr && g_api.type_get_type != nullptr &&
           g_api.type_is_byref != nullptr && g_api.class_from_type != nullptr &&
           g_api.class_is_enum != nullptr && g_api.class_enum_basetype != nullptr &&
           g_api.object_get_class != nullptr && g_api.string_new != nullptr &&
           g_api.string_chars != nullptr && g_api.string_length != nullptr &&
           g_api.gchandle_new != nullptr && g_api.gchandle_get_target != nullptr &&
           g_api.gchandle_free != nullptr;
}

const void* parameter_type(void* method, int index) {
    if (g_api.method_get_param == nullptr || method == nullptr || index < 0) {
        return nullptr;
    }
    // Unlike Mono's ParameterInfo API, il2cpp_method_get_param directly returns the
    // Il2CppType pointer. Do not reinterpret a ParameterInfo layout here.
    return g_api.method_get_param(method, static_cast<uint32_t>(index));
}

bool map_typed_type(const void* type, nami::il2cpp::patch::TypedHookValueSpec* out,
                    bool allow_void = false) {
    using namespace nami::il2cpp::patch;
    if (type == nullptr || out == nullptr || g_api.type_get_type == nullptr ||
        g_api.type_is_byref == nullptr) {
        return false;
    }
    auto* mutable_type = const_cast<void*>(type);
    if (g_api.type_is_byref(mutable_type) != 0) {
        return false; // ref/out needs a distinct pointer-to-value ABI; refuse for v1.
    }

    const int code = g_api.type_get_type(mutable_type);
    out->kind = TypedHook_Unsupported;
    out->location = TypedHook_Gp;
    out->position = 0;
    out->width = 0;
    switch (code) {
        case kTypeVoid:
            if (!allow_void) {
                return false;
            }
            out->kind = TypedHook_Void;
            out->width = 0;
            return true;
        case kTypeBoolean:
            out->kind = TypedHook_Bool;
            out->width = 1;
            return true;
        case kTypeI1:
        case kTypeU1:
            out->kind = TypedHook_I32;
            out->width = 1;
            return true;
        case kTypeI2:
        case kTypeU2:
            out->kind = TypedHook_I32;
            out->width = 2;
            return true;
        case kTypeI4:
        case kTypeU4:
            out->kind = TypedHook_I32;
            out->width = 4;
            return true;
        case kTypeI8:
        case kTypeU8:
            out->kind = TypedHook_I64;
            out->width = 8;
            return true;
        case kTypeR4:
            out->kind = TypedHook_R4;
            out->width = 4;
            return true;
        case kTypeR8:
            out->kind = TypedHook_R8;
            out->width = 8;
            return true;
        case kTypeString:
            out->kind = TypedHook_String;
            out->width = 8;
            return true;
        case kTypeClass:
        case kTypeArray:
        case kTypeGenericInst:
        case kTypeObject:
        case kTypeSzArray:
            out->kind = TypedHook_Object;
            out->width = 8;
            return true;
        case kTypeValueType: {
            // Enums are the only value types supported by the v1 Tide vocabulary. Resolve
            // the enum's basetype; arbitrary structs are deliberately refused.
            if (g_api.class_from_type == nullptr || g_api.class_is_enum == nullptr ||
                g_api.class_enum_basetype == nullptr) {
                return false;
            }
            void* klass = g_api.class_from_type(const_cast<void*>(type));
            if (klass == nullptr || g_api.class_is_enum(klass) == 0) {
                return false;
            }
            const void* base = g_api.class_enum_basetype(klass);
            return map_typed_type(base, out, false);
        }
        default:
            return false;
    }
}

bool matches_expected(uint8_t kind, int expected) {
    using namespace nami::il2cpp::patch;
    using namespace nami::tide;
    if (expected < 0) {
        return true;
    }
    switch (kind) {
        case TypedHook_Bool: return expected == TideType_Bool;
        case TypedHook_I32: return expected == TideType_I32;
        case TypedHook_I64: return expected == TideType_I64;
        case TypedHook_R4: return expected == TideType_R4;
        case TypedHook_R8: return expected == TideType_R8;
        case TypedHook_String: return expected == TideType_String;
        case TypedHook_Object: return expected == TideType_Object;
        case TypedHook_Void: return expected == TideType_Void;
        default: return false;
    }
}

struct TypedResolution {
    void* method = nullptr;
    void* entry = nullptr;
    nami::il2cpp::patch::TypedHookSignature signature{};
    std::string error;
};

bool build_typed_signature(void* method, int expected_count, const int32_t* expected_types,
                           int expected_return, TypedResolution* out) {
    using namespace nami::il2cpp::patch;
    if (method == nullptr || out == nullptr || !typed_api_ready()) {
        return false;
    }
    const uint32_t user_count = g_api.method_get_param_count(method);
    if (user_count > 12 || expected_types == nullptr ||
        expected_count != static_cast<int>(user_count)) {
        return false;
    }
    uint32_t iflags = 0;
    const uint32_t flags = g_api.method_get_flags(method, &iflags);
    const bool is_static = (flags & kMethodAttrStatic) != 0;
    if ((flags & kMethodAttrVirtual) != 0) {
        return false; // v2 patches direct method pointers, not derived vtable slots.
    }
    // IL2CPP generated method pointers conventionally carry a trailing MethodInfo*
    // metadata argument. Generic/inflated methods may also carry RGCTX/context state;
    // v2 refuses those rather than pretending the visible parameter list is the native ABI.
    if (g_api.method_is_generic(method) != 0 || g_api.method_is_inflated(method) != 0) {
        return false;
    }
    constexpr uint32_t hidden_method = 1;
    const uint32_t machine_count = user_count + (is_static ? 0u : 1u) + hidden_method;
    if (machine_count > 13) {
        return false;
    }

    TypedHookSignature sig{};
    sig.version = kTypedSignatureVersion;
    sig.user_arg_count = user_count;
    sig.machine_arg_count = machine_count;
    sig.instance_method = is_static ? 0u : 1u;
    sig.hidden_method = hidden_method;
    uint32_t visible_offset = 0;
    if (!is_static) {
        sig.args[0] = {TypedHook_Object, TypedHook_Gp, 0, 8};
        visible_offset = 1;
    }
    for (uint32_t i = 0; i < user_count; i++) {
        auto& spec = sig.args[i + visible_offset];
        if (!map_typed_type(parameter_type(method, static_cast<int>(i)), &spec)) {
            return false;
        }
        const uint32_t machine_pos = i + visible_offset;
        spec.position = static_cast<uint8_t>(machine_pos < 4 ? machine_pos : machine_pos - 4);
        spec.location = static_cast<uint8_t>(machine_pos < 4
                                                 ? (spec.kind == TypedHook_R4 || spec.kind == TypedHook_R8
                                                        ? TypedHook_Xmm
                                                        : TypedHook_Gp)
                                                 : TypedHook_Stack);
        if (expected_types != nullptr && !matches_expected(spec.kind, expected_types[i])) {
            return false;
        }
    }

    if (!map_typed_type(g_api.method_get_return_type(method), &sig.result, true)) {
        return false;
    }
    if (sig.result.kind == TypedHook_Void) {
        sig.result.location = TypedHook_ResultRax;
    } else if (sig.result.kind == TypedHook_R4 || sig.result.kind == TypedHook_R8) {
        sig.result.location = TypedHook_ResultXmm;
    } else {
        sig.result.location = TypedHook_ResultRax;
    }
    if (!matches_expected(sig.result.kind, expected_return)) {
        return false;
    }
    out->signature = sig;
    return true;
}

TypedResolution resolve_typed_method(const char* assembly, const char* ns, const char* klass,
                                     const char* name, int argc, const int32_t* expected_types,
                                     int expected_count, int expected_return) {
    TypedResolution result{};
    if (!typed_api_ready()) {
        result.error = "required IL2CPP signature exports are unavailable";
        return result;
    }
    void* image = nullptr;
    char with_dll[192]{};
    snprintf(with_dll, sizeof(with_dll), "%s.dll", assembly);
    const char* names[] = {assembly, with_dll};
    for (const char* candidate : names) {
        void* asm_ = g_api.domain_assembly_open(g_api.domain_get(), candidate);
        if (asm_ != nullptr) {
            image = g_api.assembly_get_image(asm_);
            if (image != nullptr) break;
        }
    }
    if (image == nullptr) {
        result.error = "assembly not found";
        return result;
    }
    void* start = g_api.class_from_name(image, ns, klass);
    if (start == nullptr) {
        result.error = "class not found";
        return result;
    }

    bool found = false;
    for (void* k = start; k != nullptr; k = g_api.class_get_parent(k)) {
        void* iter = nullptr;
        while (void* method = g_api.class_get_methods(k, &iter)) {
            const char* method_name = g_api.method_get_name(method);
            if (method_name == nullptr || std::strcmp(method_name, name) != 0 ||
                static_cast<int>(g_api.method_get_param_count(method)) != argc) {
                continue;
            }
            TypedResolution candidate{};
            if (!build_typed_signature(method, expected_count, expected_types, expected_return,
                                       &candidate)) {
                continue;
            }
            if (found) {
                // Two overloads with the same Tide-visible signature are still ambiguous:
                // a raw native entry cannot be chosen safely by size or address.
                result.error = "ambiguous or unsupported overload; supply exact TideType arguments";
                return result;
            }
            candidate.method = method;
            candidate.entry = *reinterpret_cast<void**>(method);
            result = candidate;
            found = true;
        }
    }
    if (!found) {
        result.error = expected_types == nullptr
                           ? "no supported overload; typed hooks refuse ambiguous/complex signatures"
                           : "no overload matched the expected TideType arguments";
        return result;
    }
    result.entry = FollowJumpStubs(result.entry);
    if (result.entry == nullptr) {
        result.error = "method entry is not executable";
    }
    return result;
}

uint64_t* raw_gp(nami::stub::TypedHookFrame* frame, const nami::il2cpp::patch::TypedHookValueSpec& spec) {
    if (spec.location == nami::il2cpp::patch::TypedHook_Gp) {
        return reinterpret_cast<uint64_t*>(frame->gp) + spec.position;
    }
    if (spec.location == nami::il2cpp::patch::TypedHook_Stack) {
        return reinterpret_cast<uint64_t*>(frame->stack) + spec.position;
    }
    return nullptr;
}

unsigned char* raw_xmm(nami::stub::TypedHookFrame* frame,
                       const nami::il2cpp::patch::TypedHookValueSpec& spec) {
    if (spec.location != nami::il2cpp::patch::TypedHook_Xmm) {
        return nullptr;
    }
    return reinterpret_cast<unsigned char*>(frame->xmm) + spec.position * 16;
}

bool is_string_object(void* object) {
    if (object == nullptr || g_api.object_get_class == nullptr) {
        return false;
    }
    void* image = nullptr;
    void* asm_ = g_api.domain_assembly_open(g_api.domain_get(), "mscorlib");
    if (asm_ != nullptr) image = g_api.assembly_get_image(asm_);
    if (image == nullptr) return false;
    void* string_class = g_api.class_from_name(image, "System", "String");
    return string_class != nullptr && g_api.object_get_class(object) == string_class;
}

char* typed_string_to_utf8(void* object) {
    if (!is_string_object(object) || g_api.string_chars == nullptr || g_api.string_length == nullptr) {
        return nullptr;
    }
    const int32_t length = g_api.string_length(object);
    const auto* chars = static_cast<const wchar_t*>(g_api.string_chars(object));
    if (length < 0 || chars == nullptr) return nullptr;
    const int need = WideCharToMultiByte(CP_UTF8, 0, chars, length, nullptr, 0, nullptr, nullptr);
    if (need <= 0) return nullptr;
    auto* text = static_cast<char*>(std::malloc(static_cast<size_t>(need) + 1));
    if (text == nullptr) return nullptr;
    WideCharToMultiByte(CP_UTF8, 0, chars, length, text, need, nullptr, nullptr);
    text[need] = '\0';
    return text;
}

int tide_type_for(const nami::il2cpp::patch::TypedHookValueSpec& spec) {
    using namespace nami::il2cpp::patch;
    using namespace nami::tide;
    switch (spec.kind) {
        case TypedHook_Bool: return TideType_Bool;
        case TypedHook_I32: return TideType_I32;
        case TypedHook_I64: return TideType_I64;
        case TypedHook_R4: return TideType_R4;
        case TypedHook_R8: return TideType_R8;
        case TypedHook_String: return TideType_String;
        case TypedHook_Object: return TideType_Object;
        default: return TideType_Void;
    }
}

const nami::il2cpp::patch::TypedHookValueSpec* argument_spec(
    nami::stub::TypedHookFrame* frame, int index) {
    if (frame == nullptr || frame->signature == 0 || index < 0) return nullptr;
    auto* sig = reinterpret_cast<const nami::il2cpp::patch::TypedHookSignature*>(frame->signature);
    const int machine = index + (sig->instance_method != 0 ? 1 : 0);
    if (sig->version != kTypedSignatureVersion || index >= static_cast<int>(sig->user_arg_count) ||
        machine >= static_cast<int>(sig->machine_arg_count)) return nullptr;
    return &sig->args[machine];
}

// Borrowed receiver spec: machine slot 0 exists only on instance-method hooks.
// Returns null for static hooks (no receiver) so the export can refuse with -1.
const nami::il2cpp::patch::TypedHookValueSpec* this_spec(
    nami::stub::TypedHookFrame* frame) {
    if (frame == nullptr || frame->signature == 0) return nullptr;
    auto* sig = reinterpret_cast<const nami::il2cpp::patch::TypedHookSignature*>(frame->signature);
    if (sig->version != kTypedSignatureVersion || sig->instance_method == 0 ||
        sig->machine_arg_count < 1) return nullptr;
    return &sig->args[0];
}

const nami::il2cpp::patch::TypedHookValueSpec* result_spec(nami::stub::TypedHookFrame* frame) {
    if (frame == nullptr || frame->signature == 0) return nullptr;
    auto* sig = reinterpret_cast<const nami::il2cpp::patch::TypedHookSignature*>(frame->signature);
    return sig->version == kTypedSignatureVersion ? &sig->result : nullptr;
}

bool append_temporary_handle(nami::stub::TypedHookFrame* frame, uint64_t handle);

bool read_typed_value(nami::stub::TypedHookFrame* frame,
                      const nami::il2cpp::patch::TypedHookValueSpec& spec,
                      nami::tide::TideValue* out, bool result) {
    using namespace nami::tide;
    if (frame == nullptr || out == nullptr) return false;
    *out = {};
    out->type = static_cast<nami::tide::TideValueType>(tide_type_for(spec));
    uint64_t* gp = result ? reinterpret_cast<uint64_t*>(frame->result) : raw_gp(frame, spec);
    unsigned char* xmm = result ? reinterpret_cast<unsigned char*>(frame->result) + 8 : raw_xmm(frame, spec);
    if (spec.kind == nami::il2cpp::patch::TypedHook_Object) {
        const uint64_t raw = gp != nullptr ? *gp : 0;
        if (raw == 0) {
            out->data.handle = 0;
            return true;
        }
        if (g_api.gchandle_new == nullptr) return false;
        const uint64_t handle = g_api.gchandle_new(reinterpret_cast<void*>(raw), 1);
        if (handle == 0 || !append_temporary_handle(frame, handle)) {
            if (handle != 0 && g_api.gchandle_free != nullptr) g_api.gchandle_free(handle);
            return false;
        }
        out->data.handle = static_cast<int64_t>(handle);
        return true;
    }
    if (spec.kind == nami::il2cpp::patch::TypedHook_String) {
        const uint64_t raw = gp != nullptr ? *gp : 0;
        if (raw == 0) return true;
        char* text = typed_string_to_utf8(reinterpret_cast<void*>(raw));
        if (text == nullptr) return false;
        out->data.str.utf8 = text;
        out->data.str.len = static_cast<int32_t>(std::strlen(text));
        return true;
    }
    if (spec.kind == nami::il2cpp::patch::TypedHook_R4) {
        if (spec.location == nami::il2cpp::patch::TypedHook_Stack) {
            if (gp == nullptr) return false;
            std::memcpy(&out->data.r4, gp, sizeof(float));
        } else {
            if (xmm == nullptr) return false;
            std::memcpy(&out->data.r4, xmm, sizeof(float));
        }
        return true;
    }
    if (spec.kind == nami::il2cpp::patch::TypedHook_R8) {
        if (spec.location == nami::il2cpp::patch::TypedHook_Stack) {
            if (gp == nullptr) return false;
            std::memcpy(&out->data.r8, gp, sizeof(double));
        } else {
            if (xmm == nullptr) return false;
            std::memcpy(&out->data.r8, xmm, sizeof(double));
        }
        return true;
    }
    if (gp == nullptr) return false;
    if (spec.kind == nami::il2cpp::patch::TypedHook_Bool) {
        out->data.boolean = static_cast<int32_t>(*reinterpret_cast<unsigned char*>(gp) != 0);
        return true;
    }
    if (spec.kind == nami::il2cpp::patch::TypedHook_I32) {
        if (spec.width == 1) out->data.i32 = *reinterpret_cast<int8_t*>(gp);
        else if (spec.width == 2) out->data.i32 = *reinterpret_cast<int16_t*>(gp);
        else out->data.i32 = *reinterpret_cast<int32_t*>(gp);
        return true;
    }
    if (spec.kind == nami::il2cpp::patch::TypedHook_I64) {
        out->data.i64 = *reinterpret_cast<int64_t*>(gp);
        return true;
    }
    return false;
}

bool append_temporary_handle(nami::stub::TypedHookFrame* frame, uint64_t handle) {
    if (frame == nullptr || handle == 0 || frame->temporary_count >= 64) return false;
    frame->temporary_handles[frame->temporary_count++] = handle;
    return true;
}

bool write_boxed_to_object_slot(nami::stub::TypedHookFrame* frame,
                               const nami::il2cpp::patch::TypedHookValueSpec& spec,
                               const nami::tide::TideValue& value, bool result) {
    using namespace nami::tide;
    uint64_t* gp = result ? reinterpret_cast<uint64_t*>(frame->result) : raw_gp(frame, spec);
    if (gp == nullptr) return false;
    // Strings are already objects: materialize exactly like the String-spec write.
    // Primitives box through the shared il2cpp_boxing.h table (same rule as Tide's
    // object-param calls). Anything else (structs, handles-as-values) stays refused.
    void* object = nullptr;
    if (value.type == TideType_String) {
        if (value.data.str.utf8 != nullptr) {
            if (g_api.string_new == nullptr) return false;
            object = g_api.string_new(value.data.str.utf8);
            if (object == nullptr) return false;
        }
    } else {
        if (g_api.value_box == nullptr || g_api.domain_get == nullptr ||
            g_api.domain_assembly_open == nullptr || g_api.assembly_get_image == nullptr) {
            return false;
        }
        void* image = nullptr;
        char with_dll[192]{};
        snprintf(with_dll, sizeof(with_dll), "%s.dll", "mscorlib");
        const char* names[] = {"mscorlib", with_dll};
        for (const char* candidate : names) {
            void* asm_ = g_api.domain_assembly_open(g_api.domain_get(), candidate);
            if (asm_ != nullptr) {
                image = g_api.assembly_get_image(asm_);
                if (image != nullptr) break;
            }
        }
        if (image == nullptr) return false;
        nami::il2cpp::box::BoxHost host{g_api.class_from_name, g_api.value_box};
        object = nami::il2cpp::box::BoxPrimitive(host, image, value);
        if (object == nullptr) return false;
    }
    // Pin exactly like neighboring string writes: the slot holds a raw pointer the
    // stub re-presents, and cleanup frees the temporary after dispatch.
    if (object != nullptr) {
        if (g_api.gchandle_new == nullptr) return false;
        const uint64_t handle = g_api.gchandle_new(object, 1);
        if (!append_temporary_handle(frame, handle)) {
            if (g_api.gchandle_free != nullptr) g_api.gchandle_free(handle);
            return false;
        }
    }
    *gp = reinterpret_cast<uint64_t>(object);
    return true;
}

bool write_typed_value(nami::stub::TypedHookFrame* frame,
                       const nami::il2cpp::patch::TypedHookValueSpec& spec,
                       const nami::tide::TideValue& value, bool result) {
    using namespace nami::tide;
    if (frame == nullptr) return false;
    if (static_cast<int>(value.type) != tide_type_for(spec)) {
        // Tide parity: primitives and strings flow into Object slots boxed, mirroring
        // Tide's "System.Object params accept boxed primitives" overload rule. All
        // other mismatches (notably any struct) stay refused rather than guessed.
        if (spec.kind != nami::il2cpp::patch::TypedHook_Object) return false;
        return write_boxed_to_object_slot(frame, spec, value, result);
    }
    uint64_t* gp = result ? reinterpret_cast<uint64_t*>(frame->result) : raw_gp(frame, spec);
    unsigned char* xmm = result ? reinterpret_cast<unsigned char*>(frame->result) + 8 : raw_xmm(frame, spec);
    if (spec.kind == nami::il2cpp::patch::TypedHook_Object || spec.kind == nami::il2cpp::patch::TypedHook_String) {
        if (gp == nullptr) return false;
        void* object = nullptr;
        if (spec.kind == nami::il2cpp::patch::TypedHook_Object) {
            if (value.data.handle != 0 && g_api.gchandle_get_target != nullptr) {
                object = g_api.gchandle_get_target(static_cast<uint64_t>(value.data.handle));
                if (object == nullptr) return false;
            }
        } else if (value.data.str.utf8 != nullptr && g_api.string_new != nullptr) {
            object = g_api.string_new(value.data.str.utf8);
            if (object == nullptr) return false;
            const uint64_t handle = g_api.gchandle_new(object, 1);
            if (!append_temporary_handle(frame, handle)) {
                g_api.gchandle_free(handle);
                return false;
            }
        }
        *gp = reinterpret_cast<uint64_t>(object);
        return true;
    }
    if (spec.kind == nami::il2cpp::patch::TypedHook_R4) {
        if (spec.location == nami::il2cpp::patch::TypedHook_Stack) {
            return gp != nullptr && (std::memcpy(gp, &value.data.r4, sizeof(float)), true);
        }
        return xmm != nullptr && (std::memcpy(xmm, &value.data.r4, sizeof(float)), true);
    }
    if (spec.kind == nami::il2cpp::patch::TypedHook_R8) {
        if (spec.location == nami::il2cpp::patch::TypedHook_Stack) {
            return gp != nullptr && (std::memcpy(gp, &value.data.r8, sizeof(double)), true);
        }
        return xmm != nullptr && (std::memcpy(xmm, &value.data.r8, sizeof(double)), true);
    }
    if (gp == nullptr) return false;
    if (spec.kind == nami::il2cpp::patch::TypedHook_Bool) {
        *reinterpret_cast<unsigned char*>(gp) = value.data.boolean != 0 ? 1 : 0;
        return true;
    }
    if (spec.kind == nami::il2cpp::patch::TypedHook_I32) {
        if (spec.width == 1) *reinterpret_cast<int8_t*>(gp) = static_cast<int8_t>(value.data.i32);
        else if (spec.width == 2) *reinterpret_cast<int16_t*>(gp) = static_cast<int16_t>(value.data.i32);
        else *reinterpret_cast<int32_t*>(gp) = value.data.i32;
        return true;
    }
    if (spec.kind == nami::il2cpp::patch::TypedHook_I64) {
        *reinterpret_cast<int64_t*>(gp) = value.data.i64;
        return true;
    }
    return false;
}

// Hook registry: hook_id -> record.
std::unordered_map<uint64_t, nami::stub::HookRecord*> g_hooks;
std::mutex g_hooks_lock;
uint64_t g_next_hook_id = 1;

struct HookOp {
    char assembly[160];
    char ns[160];
    char klass[160];
    char method[160];
    int argc;
    uint64_t dispatch;         // prefix entry (fast path) or prefix dispatch (full path)
    uint64_t dispatch_postfix; // 0 = fast path; non-null = full path
    int return_kind;           // full path only: 0..4 (see native_stub.h)
    uint64_t user_handle;
    uint64_t trampoline;
    uint64_t hook_id;
    int result;
};

int RunHookOp(void* arg) {
    auto* op = static_cast<HookOp*>(arg);
    op->result = -1;

    // The window-proc executor must be up (this op runs inside the game's message
    // pump - the only context where the VM is safe).
    install_window_executor();
    if (!resolve_api()) {
        log_tide("il2cpp patch: api resolve failed");
        return -1;
    }

    void* asm_ = g_api.domain_assembly_open(g_api.domain_get(), op->assembly);
    if (asm_ == nullptr) {
        log_tide("il2cpp patch: assembly '%s' not found", op->assembly);
        return -1;
    }
    void* image = g_api.assembly_get_image(asm_);
    void* klass = g_api.class_from_name(image, op->ns, op->klass);
    if (klass == nullptr) {
        log_tide("il2cpp patch: class '%s.%s' not found", op->ns, op->klass);
        return -1;
    }
    void* method_info = g_api.class_get_method_from_name(klass, op->method, op->argc);
    if (method_info == nullptr) {
        log_tide("il2cpp patch: method '%s(%d)' not found on %s.%s", op->method, op->argc,
                 op->ns, op->klass);
        return -1;
    }

    // Il2CppMethodInfo layout: methodPointer is the FIRST field in metadata v24-v39
    // (Unity 2020.3 through Unity 6). Resolved lazily by the runtime at domain init.
    void* entry = *reinterpret_cast<void**>(method_info);
    if (entry == nullptr) {
        log_tide("il2cpp patch: methodPointer is null (generic/uninitialized?)");
        return -1;
    }
    entry = FollowJumpStubs(entry);
    if (entry == nullptr) {
        log_tide("il2cpp patch: method entry not executable at %p", entry);
        return -1;
    }

    nami::stub::HookRecord* rec = nullptr;
    if (op->dispatch_postfix != 0) {
        rec = nami::stub::hook_native_full(entry, reinterpret_cast<void*>(op->dispatch),
                                           reinterpret_cast<void*>(op->dispatch_postfix),
                                           op->return_kind, op->user_handle, op->argc);
    } else {
        rec = nami::stub::hook_native_at(entry, reinterpret_cast<void*>(op->dispatch),
                                         op->user_handle, op->argc);
    }
    if (rec == nullptr) {
        const auto* b = static_cast<const unsigned char*>(entry);
        log_tide("il2cpp patch: detour refused at %p (prologue < 5 clean bytes? first bytes: "
                 "%02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X)",
                 entry, b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10],
                 b[11], b[12], b[13], b[14], b[15]);
        return -1;
    }

    {
        std::lock_guard<std::mutex> lock(g_hooks_lock);
        op->hook_id = g_next_hook_id++;
        g_hooks[op->hook_id] = rec;
    }
    op->trampoline = reinterpret_cast<uint64_t>(rec->trampoline);
    op->result = 0;
    log_tide("il2cpp patch: hooked %s.%s::%s at %p (hook=%llu)", op->ns, op->klass, op->method,
             entry, static_cast<unsigned long long>(op->hook_id));
    return 0;
}

struct TypedHookOp {
    char assembly[160];
    char ns[160];
    char klass[160];
    char method[160];
    int argc;
    int expected_types[12];
    int expected_count;
    int expected_return;
    uint64_t dispatch_prefix;
    uint64_t dispatch_postfix;
    uint64_t user_handle;
    uint64_t hook_id;
    int result;
    TypedHookSignature* signature;
};

int RunTypedHookOp(void* arg) {
    auto* op = static_cast<TypedHookOp*>(arg);
    op->result = -1;
    op->signature = nullptr;
    install_window_executor();
    if (!resolve_api() || !typed_api_ready()) {
        op->result = -4;
        log_tide("il2cpp typed patch: required signature/value exports not found");
        return -4;
    }

    auto resolved = resolve_typed_method(op->assembly, op->ns, op->klass, op->method, op->argc,
                                         op->expected_count > 0 ? op->expected_types : nullptr,
                                         op->expected_count, op->expected_return);
    if (resolved.entry == nullptr) {
        op->result = -4;
        log_tide("il2cpp typed patch: %s.%s::%s(%d): %s", op->ns, op->klass, op->method,
                 op->argc, resolved.error.c_str());
        return -4;
    }

    auto* signature = new (std::nothrow) TypedHookSignature(resolved.signature);
    if (signature == nullptr) {
        op->result = -1;
        return -1;
    }
    const int return_kind = signature->result.kind == TypedHook_R4
                                ? 3
                                : signature->result.kind == TypedHook_R8
                                      ? 4
                                      : signature->result.kind == TypedHook_Void ? 0 : 2;
    auto* rec = nami::stub::hook_native_typed(
        resolved.entry, reinterpret_cast<void*>(op->dispatch_prefix),
        reinterpret_cast<void*>(op->dispatch_postfix), return_kind, op->user_handle,
        static_cast<int>(signature->machine_arg_count), static_cast<int>(signature->user_arg_count),
        static_cast<int>(signature->instance_method), reinterpret_cast<uint64_t>(signature));
    if (rec == nullptr) {
        delete signature;
        const auto* b = static_cast<const unsigned char*>(resolved.entry);
        log_tide("il2cpp typed patch: detour refused at %p (bytes %02X %02X %02X %02X %02X)",
                 resolved.entry, b[0], b[1], b[2], b[3], b[4]);
        return -1;
    }
    rec->typed_signature = signature;
    {
        std::lock_guard<std::mutex> lock(g_hooks_lock);
        op->hook_id = g_next_hook_id++;
        g_hooks[op->hook_id] = rec;
    }
    op->signature = signature;
    op->result = 0;
    log_tide("il2cpp typed patch: hooked %s.%s::%s (%u user args, instance=%u, hook=%llu)",
             op->ns, op->klass, op->method, signature->user_arg_count, signature->instance_method,
             static_cast<unsigned long long>(op->hook_id));
    return 0;
}

}  // namespace

}  // namespace nami::il2cpp::patch

using namespace nami::il2cpp::patch;

extern "C" __declspec(dllexport) int nami_il2cpp_hook(const char* assembly, const char* ns,
                                                      const char* klass, const char* method,
                                                      int argc, uint64_t dispatch,
                                                      uint64_t dispatch_postfix,
                                                      int return_kind, uint64_t user_handle,
                                                      uint64_t* trampoline_out,
                                                      uint64_t* hook_id_out) {
    using namespace nami::il2cpp::patch;

    if (assembly == nullptr || ns == nullptr || klass == nullptr || method == nullptr ||
        argc < 0 || dispatch == 0 || trampoline_out == nullptr || hook_id_out == nullptr) {
        return -1;
    }
    if (dispatch_postfix != 0) {
        // Full path: stack args up to 12 slots, return_kind must be 0..4.
        if (argc > 12 || return_kind < 0 || return_kind > 4) {
            return -1;
        }
    } else if (argc > 4) {
        return -1;  // fast path exposes register args only
    }

    HookOp op{};
    strncpy_s(op.assembly, assembly, sizeof(op.assembly) - 1);
    strncpy_s(op.ns, ns, sizeof(op.ns) - 1);
    strncpy_s(op.klass, klass, sizeof(op.klass) - 1);
    strncpy_s(op.method, method, sizeof(op.method) - 1);
    op.argc = argc;
    op.dispatch = dispatch;
    op.dispatch_postfix = dispatch_postfix;
    op.return_kind = return_kind;
    op.user_handle = user_handle;

    const bool ran = nami::il2cpp::run_il2cpp_op(RunHookOp, &op, 30000);
    if (!ran) {
        log_tide("il2cpp patch: hook op did not run (executor unavailable?)");
        return -3;
    }
    *trampoline_out = op.trampoline;
    *hook_id_out = op.hook_id;
    return op.result;
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_typed(
    const char* assembly, const char* ns, const char* klass, const char* method, int argc,
    const int32_t* expected_types, int expected_count, int expected_return,
    uint64_t dispatch_prefix, uint64_t dispatch_postfix, uint64_t user_handle,
    uint64_t* hook_id_out) {
    using namespace nami::il2cpp::patch;
    if (assembly == nullptr || ns == nullptr || klass == nullptr || method == nullptr ||
        argc < 0 || argc > 12 || dispatch_prefix == 0 || dispatch_postfix == 0 ||
        hook_id_out == nullptr || expected_count < 0 || expected_count > 12 ||
        (expected_count > 0 && expected_types == nullptr) || expected_return < -1 || expected_return > 7) {
        return -1;
    }
    TypedHookOp op{};
    strncpy_s(op.assembly, assembly, sizeof(op.assembly) - 1);
    strncpy_s(op.ns, ns, sizeof(op.ns) - 1);
    strncpy_s(op.klass, klass, sizeof(op.klass) - 1);
    strncpy_s(op.method, method, sizeof(op.method) - 1);
    op.argc = argc;
    op.expected_count = expected_count;
    op.expected_return = expected_return;
    op.dispatch_prefix = dispatch_prefix;
    op.dispatch_postfix = dispatch_postfix;
    op.user_handle = user_handle;
    for (int i = 0; i < expected_count; i++) {
        op.expected_types[i] = expected_types[i];
    }
    const bool ran = nami::il2cpp::run_il2cpp_op(RunTypedHookOp, &op, 30000);
    if (!ran) {
        log_tide("il2cpp typed patch: executor unavailable");
        return -3;
    }
    *hook_id_out = op.hook_id;
    return op.result;
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_frame_arg_count(void* frame) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    return f == nullptr ? -1 : static_cast<int>(f->arg_count);
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_frame_is_instance(void* frame) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    return f == nullptr ? 0 : static_cast<int>(f->instance_method != 0);
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_frame_get_type(void* frame, int index) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    const auto* spec = argument_spec(f, index);
    return spec == nullptr ? nami::tide::TideType_Void : tide_type_for(*spec);
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_frame_get(void* frame, int index,
                                                                  nami::tide::TideValue* value) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    const auto* spec = argument_spec(f, index);
    return spec != nullptr && read_typed_value(f, *spec, value, false) ? 0 : -1;
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_frame_set(
    void* frame, int index, const nami::tide::TideValue* value) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    const auto* spec = argument_spec(f, index);
    return spec != nullptr && value != nullptr && write_typed_value(f, *spec, *value, false) ? 0 : -1;
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_frame_get_this(void* frame,
                                                                      nami::tide::TideValue* value) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    if (f == nullptr || value == nullptr) return -1;
    const auto* spec = this_spec(f);
    // The receiver decodes through the shared Object path (borrowed gchandle in the
    // frame's temporary scope); NativeFrameCleanup frees it after dispatch, so the
    // managed wrapper must treat the handle as borrowed (see Il2CppHookContext.This).
    return spec != nullptr && read_typed_value(f, *spec, value, false) ? 0 : -1;
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_frame_get_result_type(void* frame) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    const auto* spec = result_spec(f);
    return spec == nullptr ? nami::tide::TideType_Void : tide_type_for(*spec);
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_frame_get_result(
    void* frame, nami::tide::TideValue* value) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    const auto* spec = result_spec(f);
    return spec != nullptr && read_typed_value(f, *spec, value, true) ? 0 : -1;
}

extern "C" __declspec(dllexport) int nami_il2cpp_hook_frame_set_result(
    void* frame, const nami::tide::TideValue* value) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    const auto* spec = result_spec(f);
    return spec != nullptr && value != nullptr && write_typed_value(f, *spec, *value, true) ? 0 : -1;
}

extern "C" __declspec(dllexport) void nami_il2cpp_hook_frame_cleanup(void* frame) {
    auto* f = static_cast<nami::stub::TypedHookFrame*>(frame);
    if (f == nullptr || g_api.gchandle_free == nullptr) return;
    for (uint32_t i = 0; i < f->temporary_count; i++) {
        if (f->temporary_handles[i] != 0) {
            g_api.gchandle_free(f->temporary_handles[i]);
            f->temporary_handles[i] = 0;
        }
    }
    f->temporary_count = 0;
}

extern "C" __declspec(dllexport) int nami_il2cpp_unhook(uint64_t hook_id) {
    using namespace nami::il2cpp::patch;

    nami::stub::HookRecord* rec = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_hooks_lock);
        auto it = g_hooks.find(hook_id);
        if (it == g_hooks.end()) {
            return -1;
        }
        rec = it->second;
        g_hooks.erase(it);
    }
    // `unhook_native` retires typed executable state for process lifetime so an in-flight
    // callback cannot race a freed stub. The signature is retained with that record too.
    nami::stub::unhook_native(rec);
    log_tide("il2cpp patch: unhooked id=%llu", static_cast<unsigned long long>(hook_id));
    return 0;
}