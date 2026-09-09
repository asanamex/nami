// ---------------------------------------------------------------------------
// Tide IL2CPP typed ops. Executed on the game's main thread inside its window proc
// (see tide_il2cpp.cpp). Mirrors the Mono tide_objects.cpp semantics 1:1 against the
// IL2CPP runtime so the managed Nami.Tide API behaves identically on IL2CPP titles.
//
// IL2CPP API notes (empirically verified):
//   - il2cpp_domain_assembly_open returns an Il2CppAssembly* - the IMAGE comes from
//     il2cpp_assembly_get_image(assembly).
//   - class/method/field lookups are safe on the main thread inside the window proc.
//   - Objects are kept alive with il2cpp_gchandle_new (the IL2CPP GC).
//   - Strings are UTF-16 internally: convert via il2cpp_string_chars/length.
// ---------------------------------------------------------------------------

#include "il2cpp_boxing.h"
#include "tide_il2cpp.h"
#include "tide_member_cache.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <cwchar>

namespace nami::tide {
// The shared handle table (declared in tide_abi.h). Implemented in this TU.
}  // namespace nami::tide

#include "tide_abi.h"

namespace nami::il2cpp {

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

// ---------------------------------------------------------------------------
// The IL2CPP export surface (subset needed by the typed ops). All names verified
// present on Unity 2020.3.18 and 6000.0.61 IL2CPP export tables.
// ---------------------------------------------------------------------------
struct Il2CppApi {
    void* (*domain_get)() = nullptr;
    void* (*thread_attach)(void*) = nullptr;
    void* (*domain_assembly_open)(void*, const char*) = nullptr;
    void* (*assembly_get_image)(void*) = nullptr;
    void* (*class_from_name)(void*, const char*, const char*) = nullptr;
    void* (*class_get_parent)(void*) = nullptr;
    void* (*class_is_enum)(void*) = nullptr;
    void* (*class_get_type)(void*) = nullptr;
    void* (*class_from_type)(void*) = nullptr;
    // Optional (scene-object discovery only): il2cpp_type_get_object.
    void* (*type_get_object)(void*) = nullptr;
    void* (*class_get_field_from_name)(void*, const char*) = nullptr;
    void* (*class_get_method_from_name)(void*, const char*, int) = nullptr;
    void* (*class_get_methods)(void*, void**) = nullptr;
    void* (*method_get_name)(void*) = nullptr;
    void* (*method_get_param_count)(void*) = nullptr;
    void* (*method_get_param)(void*, uint32_t) = nullptr;  // -> Il2CppParameterInfo*
    int (*type_get_type)(void*) = nullptr;                 // Il2CppType* -> Il2CppTypeEnum
    void* (*method_get_class)(void*) = nullptr;
    void* (*field_get_name)(void*) = nullptr;
    void* (*field_get_type)(void*) = nullptr;
    void* (*field_get_value)(void*, void*, void*) = nullptr;
    void* (*field_set_value)(void*, void*, void*) = nullptr;
    void* (*field_static_get_value)(void*, void*) = nullptr;
    void* (*field_static_set_value)(void*, void*) = nullptr;
    void* (*runtime_invoke)(void*, void*, void**, void**) = nullptr;
    void* (*object_new)(void*) = nullptr;
    void* (*runtime_class_init)(void*) = nullptr;
    void* (*object_get_class)(void*) = nullptr;
    void* (*value_box)(void*, void*) = nullptr;
    void* (*object_unbox)(void*) = nullptr;
    void* (*string_new)(const char*) = nullptr;  // NB: single-arg (unlike Mono's (domain, str))
    const void* (*string_chars)(void*) = nullptr;
    int32_t (*string_length)(void*) = nullptr;
    int32_t (*array_length)(void*) = nullptr;
    uint32_t (*array_element_size)(void*) = nullptr;
    void* (*class_get_element_class)(void*) = nullptr;
    int (*class_is_valuetype)(void*) = nullptr;
    // IL2CPP GC handles are FULL 64-bit page-table indices (get_target masks the low 21
    // bits: `and rdi, ~0x1FFFFF`). Truncating to 32 bits AVs on lookup - same class of bug
    // as Unity 6 Mono's gchandle truncation. Keep them pointer-sized end to end.
    uint64_t (*gchandle_new)(void*, int32_t) = nullptr;
    void* (*gchandle_get_target)(uint64_t) = nullptr;
    void (*gchandle_free)(uint64_t) = nullptr;
    void* (*format_exception)(void*) = nullptr;

    void* domain = nullptr;
    bool ready = false;
};

Il2CppApi g_api{};
HMODULE g_module = nullptr;

void* resolve_export(HMODULE ga, const char* name) {
    // IMPORTANT: call the EXPORT address, NOT the followed E9 body. The real bodies on
    // Unity 6000 use internal register conventions; the export thunk is the canonical
    // entry point (verified: calling followed bodies AVs, calling exports works).
    return reinterpret_cast<void*>(GetProcAddress(ga, name));
}

#define LOAD(name) \
    g_api.name = reinterpret_cast<decltype(g_api.name)>(resolve_export(g_module, "il2cpp_" #name))

bool resolve_api() {
    if (g_api.ready) {
        return true;
    }
    if (g_module == nullptr) {
        g_module = GetModuleHandleW(L"GameAssembly.dll");
        if (g_module == nullptr) {
            return false;
        }
        log_tide("il2cpp: GameAssembly at %p", (void*)g_module);
    }

    LOAD(domain_get);
    LOAD(thread_attach);
    LOAD(domain_assembly_open);
    LOAD(assembly_get_image);
    LOAD(class_from_name);
    LOAD(class_get_parent);
    LOAD(class_is_enum);
    LOAD(class_get_type);
    LOAD(class_from_type);
    LOAD(type_get_object);
    LOAD(class_get_field_from_name);
    LOAD(class_get_method_from_name);
    LOAD(class_get_methods);
    LOAD(method_get_name);
    LOAD(method_get_param_count);
    LOAD(method_get_param);
    LOAD(type_get_type);
    LOAD(method_get_class);
    LOAD(field_get_name);
    LOAD(field_get_type);
    LOAD(field_get_value);
    LOAD(field_set_value);
    LOAD(field_static_get_value);
    LOAD(field_static_set_value);
    LOAD(runtime_invoke);
    LOAD(object_new);
    LOAD(runtime_class_init);
    LOAD(object_get_class);
    LOAD(value_box);
    LOAD(object_unbox);
    LOAD(string_new);
    LOAD(string_chars);
    LOAD(string_length);
    LOAD(array_length);
    LOAD(array_element_size);
    LOAD(class_get_element_class);
    LOAD(class_is_valuetype);
    LOAD(gchandle_new);
    LOAD(gchandle_get_target);
    LOAD(gchandle_free);
    LOAD(format_exception);
#undef LOAD

    if (g_api.domain_get != nullptr) {
        g_api.domain = g_api.domain_get();
    }

    g_api.ready = g_api.domain != nullptr && g_api.domain_assembly_open != nullptr &&
                  g_api.assembly_get_image != nullptr && g_api.class_from_name != nullptr &&
                  g_api.class_get_field_from_name != nullptr && g_api.field_get_value != nullptr &&
                  g_api.field_set_value != nullptr && g_api.field_static_get_value != nullptr &&
                  g_api.field_static_set_value != nullptr && g_api.class_get_method_from_name != nullptr &&
                  g_api.runtime_invoke != nullptr && g_api.object_new != nullptr &&
                  g_api.gchandle_new != nullptr && g_api.gchandle_get_target != nullptr &&
                  g_api.gchandle_free != nullptr && g_api.string_new != nullptr;

    log_tide("il2cpp: api ready=%d", g_api.ready ? 1 : 0);
    return g_api.ready;
}

#undef LOAD

// Opens an assembly's IMAGE (assembly_get_image) for a named assembly.
void* find_image(const char* name) {
    char with_dll[192];
    snprintf(with_dll, sizeof(with_dll), "%s.dll", name);
    const char* names[] = {name, with_dll};
    for (const char* n : names) {
        void* asm_ = g_api.domain_assembly_open(g_api.domain, n);
        if (asm_ != nullptr) {
            void* image = g_api.assembly_get_image(asm_);
            if (image != nullptr) {
                return image;
            }
        }
    }
    return nullptr;
}

void* find_class_uncached(const char* assembly, const char* ns, const char* klass) {
    void* image = find_image(assembly);
    if (image == nullptr) {
        log_tide("il2cpp: assembly '%s' not found", assembly);
        return nullptr;
    }
    void* klass_ = g_api.class_from_name(image, ns, klass);
    if (klass_ == nullptr) {
        log_tide("il2cpp: class '%s.%s' not found", ns, klass);
    }
    return klass_;
}

// Searches a class AND its base classes for a method.
void* find_method_in_hierarchy_uncached(void* klass, const char* name, int argc) {
    for (void* k = klass; k != nullptr; k = g_api.class_get_parent(k)) {
        void* m = g_api.class_get_method_from_name(k, name, argc);
        if (m != nullptr) {
            return m;
        }
    }
    return nullptr;
}

// Il2CppTypeEnum (ECMA roots, shared with Mono's MonoTypeEnum values).
enum : int {
    IL2CPP_TYPE_BOOLEAN = 0x02,
    IL2CPP_TYPE_I4 = 0x08,
    IL2CPP_TYPE_I8 = 0x0A,
    IL2CPP_TYPE_R4 = 0x0C,
    IL2CPP_TYPE_R8 = 0x0D,
    IL2CPP_TYPE_STRING = 0x0E,
    IL2CPP_TYPE_CLASS = 0x12,
    IL2CPP_TYPE_ARRAY = 0x14,
    IL2CPP_TYPE_GENERICINST = 0x15,
    IL2CPP_TYPE_OBJECT = 0x1C,
    IL2CPP_TYPE_SZARRAY = 0x1D,
};

// Il2CppParameterInfo layout on x64: name(0) position(8) token(12) parameter_type(16).
int param_type_enum(void* method, int index) {
    if (g_api.method_get_param == nullptr || g_api.type_get_type == nullptr) {
        return -1;
    }
    void* info = g_api.method_get_param(method, static_cast<uint32_t>(index));
    if (info == nullptr) {
        return -1;
    }
    void* ptype = *reinterpret_cast<void**>(static_cast<char*>(info) + 16);
    if (ptype == nullptr) {
        return -1;
    }
    return g_api.type_get_type(ptype);
}

int tide_type_to_il2cpp_type(int tide_type) {
    using nami::tide::TideType_Bool;
    using nami::tide::TideType_I32;
    using nami::tide::TideType_I64;
    using nami::tide::TideType_Object;
    using nami::tide::TideType_R4;
    using nami::tide::TideType_R8;
    using nami::tide::TideType_String;
    switch (tide_type) {
        case TideType_I32:
            return IL2CPP_TYPE_I4;
        case TideType_I64:
            return IL2CPP_TYPE_I8;
        case TideType_R4:
            return IL2CPP_TYPE_R4;
        case TideType_R8:
            return IL2CPP_TYPE_R8;
        case TideType_Bool:
            return IL2CPP_TYPE_BOOLEAN;
        case TideType_String:
            return IL2CPP_TYPE_STRING;
        case TideType_Object:
            return IL2CPP_TYPE_OBJECT;
        default:
            return -1;
    }
}

bool param_is_reference(int type_enum) {
    return type_enum == IL2CPP_TYPE_OBJECT || type_enum == IL2CPP_TYPE_CLASS ||
           type_enum == IL2CPP_TYPE_ARRAY || type_enum == IL2CPP_TYPE_SZARRAY ||
           type_enum == IL2CPP_TYPE_GENERICINST || type_enum == IL2CPP_TYPE_STRING;
}

// Overload-aware lookup mirroring the Mono backend (tide_objects.cpp
// find_method_for_args): exact primitive matches win, System.Object params accept
// anything (boxed), primitives never flow to string params. Falls back to the
// classic first-match name+argc lookup when signature info is unavailable.
void* find_method_for_args_uncached(void* klass, const char* name, int argc,
                                    const nami::tide::TideValue* args) {
    if (g_api.class_get_methods == nullptr || g_api.method_get_name == nullptr ||
        g_api.method_get_param_count == nullptr) {
        // UNCACHED call: may run inside the typed-method cache resolver, which
        // already holds the cache lock (SRW locks are not recursive).
        return find_method_in_hierarchy_uncached(klass, name, argc);
    }
    for (void* k = klass; k != nullptr && g_api.class_get_parent != nullptr;
         k = g_api.class_get_parent(k)) {
        void* best = nullptr;
        int best_score = -1;
        void* iter = nullptr;
        void* method = nullptr;
        while ((method = g_api.class_get_methods(k, &iter)) != nullptr) {
            const char* mname = static_cast<const char*>(g_api.method_get_name(method));
            if (mname == nullptr || std::strcmp(mname, name) != 0) {
                continue;
            }
            // method_get_param_count is int-returning; the struct types it as void*.
            const int count =
                static_cast<int>(reinterpret_cast<intptr_t>(g_api.method_get_param_count(method)));
            if (count != argc) {
                continue;
            }
            int score = 0;
            bool usable = true;
            for (int i = 0; i < argc; i++) {
                const int pt = param_type_enum(method, i);
                if (pt < 0) {
                    usable = false;
                    break;
                }
                const int want = tide_type_to_il2cpp_type(args[i].type);
                if (pt == want) {
                    score += 3;
                } else if (pt == IL2CPP_TYPE_OBJECT) {
                    score += 2;
                } else if (want == IL2CPP_TYPE_STRING) {
                    if (param_is_reference(pt)) {
                        score += 1;
                    } else {
                        usable = false;
                        break;
                    }
                } else if (pt == IL2CPP_TYPE_STRING) {
                    usable = false;
                    break;
                } else if (param_is_reference(pt)) {
                    score += 1;
                } else {
                    usable = false;
                    break;
                }
            }
            if (usable && score > best_score) {
                best = method;
                best_score = score;
            }
        }
        if (best != nullptr) {
            return best;
        }
    }
    // No scored match (or no signature info): classic first-match behavior.
    return find_method_in_hierarchy_uncached(klass, name, argc);
}

// Searches a class AND its base classes for a field.
void* find_field_in_hierarchy_uncached(void* klass, const char* name) {
    for (void* k = klass; k != nullptr; k = g_api.class_get_parent(k)) {
        void* f = g_api.class_get_field_from_name(k, name);
        if (f != nullptr) {
            return f;
        }
    }
    return nullptr;
}

// ---------------------------------------------------------------------------
// Cached resolution wrappers - THE call path used by the ops (see the Mono twin
// in tide_objects.cpp). IL2CPP metadata is process-lifetime, so results (incl.
// negative) are cached for the loader's lifetime. kind_tags: kTagIl2CppBase -
// distinct from the Mono backend's kTagMonoBase.
// ---------------------------------------------------------------------------

constexpr uint64_t kTagIl2CppBase = 0x2000000000000000ULL;
constexpr uint64_t kIl2CppClass = kTagIl2CppBase + 1;
constexpr uint64_t kIl2CppMethod = kTagIl2CppBase + 2;
constexpr uint64_t kIl2CppMethodTyped = kTagIl2CppBase + 3;
constexpr uint64_t kIl2CppField = kTagIl2CppBase + 4;

namespace {

struct IlClassResolveCtx { const char* assembly; const char* ns; const char* klass; };
struct IlMethodResolveCtx { void* klass; const char* name; int argc; };
struct IlTypedMethodResolveCtx { void* klass; const char* name; int argc; const nami::tide::TideValue* args; };
struct IlMemberResolveCtx { void* klass; const char* name; };

nami::tide::MemberCache::ResolveResult IlResolveClassCb(void* user) {
    auto* c = static_cast<IlClassResolveCtx*>(user);
    void* v = find_class_uncached(c->assembly, c->ns, c->klass);
    return {v, v != nullptr};
}

nami::tide::MemberCache::ResolveResult IlResolveMethodCb(void* user) {
    auto* c = static_cast<IlMethodResolveCtx*>(user);
    void* v = find_method_in_hierarchy_uncached(c->klass, c->name, c->argc);
    return {v, v != nullptr};
}

nami::tide::MemberCache::ResolveResult IlResolveTypedMethodCb(void* user) {
    auto* c = static_cast<IlTypedMethodResolveCtx*>(user);
    void* v = find_method_for_args_uncached(c->klass, c->name, c->argc, c->args);
    return {v, v != nullptr};
}

nami::tide::MemberCache::ResolveResult IlResolveFieldCb(void* user) {
    auto* c = static_cast<IlMemberResolveCtx*>(user);
    void* v = find_field_in_hierarchy_uncached(c->klass, c->name);
    return {v, v != nullptr};
}

}  // namespace

// Cached: class by (assembly, ns, name).
void* find_class(const char* assembly, const char* ns, const char* klass) {
    IlClassResolveCtx ctx{assembly, ns, klass};
    bool found = false;
    void* v = nami::tide::MemberCacheLookup(kIl2CppClass, 0, 0, assembly, ns, klass, nullptr,
                                            IlResolveClassCb, &ctx, &found);
    return found ? v : nullptr;
}

void* find_class(const nami::tide::CallRequest& req) {
    return find_class(req.assembly, req.ns, req.klass);
}

// Cached: method (any base class) by name + argc.
void* find_method_in_hierarchy(void* klass, const char* name, int argc) {
    IlMethodResolveCtx ctx{klass, name, argc};
    bool found = false;
    void* v = nami::tide::MemberCacheLookup(kIl2CppMethod, reinterpret_cast<uint64_t>(klass),
                                            static_cast<uint64_t>(argc), name, nullptr, nullptr,
                                            nullptr, IlResolveMethodCb, &ctx, &found);
    return found ? v : nullptr;
}

// Cached: overload-scored method for the given argument values. k2 hashes the
// argument type masks so different overload shapes land on different entries.
void* find_method_for_args(void* klass, const char* name, int argc,
                           const nami::tide::TideValue* args) {
    uint64_t mask = 1469598103934665603ULL;
    for (int i = 0; i < argc; i++) {
        mask = (mask ^ static_cast<uint64_t>(args[i].type)) * 0x100000001b3ULL;
        mask ^= mask >> 29;
    }
    IlTypedMethodResolveCtx ctx{klass, name, argc, args};
    bool found = false;
    void* v = nami::tide::MemberCacheLookup(kIl2CppMethodTyped, reinterpret_cast<uint64_t>(klass),
                                            mask, name, nullptr, nullptr, nullptr,
                                            IlResolveTypedMethodCb, &ctx, &found);
    return found ? v : nullptr;
}

// Cached: field (any base class) by name.
void* find_field_in_hierarchy(void* klass, const char* name) {
    IlMemberResolveCtx ctx{klass, name};
    bool found = false;
    void* v = nami::tide::MemberCacheLookup(kIl2CppField, reinterpret_cast<uint64_t>(klass), 0,
                                            name, nullptr, nullptr, nullptr,
                                            IlResolveFieldCb, &ctx, &found);
    return found ? v : nullptr;
}

void* resolve_instance(const nami::tide::TideValue& v) {
    if (v.type != nami::tide::TideType_Object || v.data.handle == 0) {
        log_tide("il2cpp: expected an object handle argument");
        return nullptr;
    }
    void* obj = g_api.gchandle_get_target(static_cast<uint64_t>(v.data.handle));
    return obj;
}

// ---------------------------------------------------------------------------
// Direct Il2CppArray element access.
// Layout (IL2CPP x64): Il2CppArray = Il2CppObject (0x10) + { void* bounds; size_t
// max_length; } then items at offset 0x20. Each element is `element_size` bytes:
// references (string/object arrays) are 8-byte object pointers; value arrays are raw.
// ---------------------------------------------------------------------------
constexpr size_t kIl2CppArrayItemsOffset = 0x20;

// Element address for index i.
char* array_element_ptr(void* arr, int index, size_t elem_size) {
    return static_cast<char*>(arr) + kIl2CppArrayItemsOffset +
           static_cast<size_t>(index) * elem_size;
}

// Resolves an array's element size (via its element class).
size_t array_element_size(void* arr) {
    void* arr_class = g_api.object_get_class(arr);
    if (arr_class == nullptr) {
        return 8;
    }
    void* elem_class = g_api.class_get_element_class != nullptr
                           ? g_api.class_get_element_class(arr_class)
                           : nullptr;
    if (elem_class != nullptr && g_api.array_element_size != nullptr) {
        uint32_t sz = g_api.array_element_size(elem_class);
        if (sz != 0) {
            return sz;
        }
    }
    // Reference arrays (string[]/object[]/T[]): each slot is a pointer.
    return 8;
}

// True if the array's element type is a reference type (object/string/array/class).
bool array_element_is_reference(void* arr) {
    void* arr_class = g_api.object_get_class(arr);
    if (arr_class == nullptr || g_api.class_get_element_class == nullptr) {
        return true;  // unknown: assume reference (8-byte slots are safe to read)
    }
    void* elem_class = g_api.class_get_element_class(arr_class);
    if (elem_class == nullptr) {
        return true;
    }
    const bool is_valuetype =
        g_api.class_is_valuetype != nullptr && g_api.class_is_valuetype(elem_class) != 0;
    return !is_valuetype;  // enums ARE valuetypes (underlying int storage)
}

// Converts a UTF-8 TideValue string into an Il2CppString.
void* make_string(const nami::tide::TideValue& v) {
    if (v.type != nami::tide::TideType_String || v.data.str.utf8 == nullptr) {
        return nullptr;
    }
    return g_api.string_new(v.data.str.utf8);
}

// Converts an Il2CppString to a UTF-8 heap buffer (malloc'd; caller frees with free()).
char* string_to_utf8(void* str) {
    if (str == nullptr || g_api.string_chars == nullptr || g_api.string_length == nullptr) {
        return nullptr;
    }
    const int32_t len = g_api.string_length(str);
    const auto* chars = static_cast<const wchar_t*>(g_api.string_chars(str));
    if (chars == nullptr || len < 0) {
        return nullptr;
    }
    // UTF-16LE -> UTF-8. Worst case 3 bytes per char (surrogates excluded); +1 NUL.
    int need = WideCharToMultiByte(CP_UTF8, 0, chars, len, nullptr, 0, nullptr, nullptr);
    if (need <= 0) {
        return nullptr;
    }
    auto* out = static_cast<char*>(malloc(need + 1));
    if (out == nullptr) {
        return nullptr;
    }
    WideCharToMultiByte(CP_UTF8, 0, chars, len, out, need, nullptr, nullptr);
    out[need] = '\0';
    return out;
}

// Reads the boxed value's underlying primitive by requested type. `obj` is an Il2CppObject*.
bool write_ret_unbox(nami::tide::TideValue* ret, void* obj) {
    void* data = g_api.object_unbox(obj);
    if (data == nullptr) {
        return false;
    }
    switch (ret->type) {
        case nami::tide::TideType_I32:
            ret->data.i32 = *static_cast<int32_t*>(data);
            return true;
        case nami::tide::TideType_I64:
            ret->data.i64 = *static_cast<int64_t*>(data);
            return true;
        case nami::tide::TideType_R4:
            ret->data.r4 = *static_cast<float*>(data);
            return true;
        case nami::tide::TideType_R8:
            ret->data.r8 = *static_cast<double*>(data);
            return true;
        case nami::tide::TideType_Bool:
            ret->data.boolean = *static_cast<int32_t*>(data);
            return true;
        default:
            return false;
    }
}

// Writes `result` (an Il2CppObject* from runtime_invoke / field_get_value / ...) into the
// ret slot according to the requested type.
void write_ret(nami::tide::TideValue* ret, void* result) {
    using namespace nami::tide;
    if (ret == nullptr || result == nullptr) {
        return;
    }
    // Is the result a string? (Il2CppString is an object whose class is System.String.)
    if (g_api.string_length != nullptr && ret->type == TideType_String) {
        // runtime_invoke returns boxed values for value types; strings come back as
        // Il2CppString*. Check if it LOOKS like a string: try string_length - safe via
        // class check instead.
        void* cls = g_api.object_get_class(result);
        void* str_cls = nullptr;
        void* corlib = find_image("mscorlib");
        if (corlib != nullptr) {
            str_cls = g_api.class_from_name(corlib, "System", "String");
        }
        if (str_cls != nullptr && cls == str_cls) {
            char* utf8 = string_to_utf8(result);
            if (utf8 != nullptr) {
                ret->data.str.utf8 = utf8;
                ret->data.str.len = static_cast<int32_t>(strlen(utf8));
            } else {
                ret->type = TideType_Void;
            }
            return;
        }
    }

    // Enums box as their underlying value type; most are int-backed (4 bytes).
    const bool is_enum = g_api.class_is_enum != nullptr &&
                         g_api.object_get_class != nullptr &&
                         g_api.class_is_enum(g_api.object_get_class(result)) != 0;

    switch (ret->type) {
        case TideType_I32:
        case TideType_I64:
        case TideType_R4:
        case TideType_R8:
        case TideType_Bool: {
            if (ret->type == TideType_I64 && is_enum) {
                // Enums narrower than 8 bytes cannot be safely read as I64.
                ret->type = TideType_Void;
                break;
            }
            if (!write_ret_unbox(ret, result)) {
                ret->type = TideType_Void;
            }
            break;
        }
        case TideType_Object:
            ret->data.handle = g_api.gchandle_new(result, 1);
            break;
        case TideType_String: {
            // A non-string object requested as string: fail loudly.
            ret->type = TideType_Void;
            break;
        }
        default:
            ret->type = TideType_Void;
            break;
    }
}

// Value-type params (int/float/enum...) are passed as pointers to raw value storage.
bool to_il2cpp_arg(const nami::tide::TideValue& v, void* box, void** out) {
    using namespace nami::tide;
    switch (v.type) {
        case TideType_I32:
            *static_cast<int32_t*>(box) = v.data.i32;
            *out = box;
            return true;
        case TideType_Bool:
            *static_cast<int32_t*>(box) = v.data.boolean ? 1 : 0;
            *out = box;
            return true;
        case TideType_I64:
            *static_cast<int64_t*>(box) = v.data.i64;
            *out = box;
            return true;
        case TideType_R4:
            *static_cast<float*>(box) = v.data.r4;
            *out = box;
            return true;
        case TideType_R8:
            *static_cast<double*>(box) = v.data.r8;
            *out = box;
            return true;
        case TideType_String: {
            void* s = make_string(v);
            if (s == nullptr) {
                return false;
            }
            *out = s;
            return true;
        }
        case TideType_Object: {
            void* obj = resolve_instance(v);
            if (obj == nullptr) {
                return false;
            }
            *out = obj;
            return true;
        }
        default:
            return false;
    }
}

// Boxes a primitive into an object of the given class (for object-typed params and
// SetValue/array writes). `klass` must be the boxed type (Int32/Boolean/...) or an enum.
// Packing is owned by il2cpp_boxing.h (single copy shared with the hook-frame path).
void* box_into_class(void* klass, const nami::tide::TideValue& v) {
    using namespace nami::tide;
    if (klass == nullptr || g_api.value_box == nullptr) {
        return nullptr;
    }
    alignas(8) unsigned char raw[8] = {};
    if (!nami::il2cpp::box::PackBoxedRaw(v, raw)) {
        return nullptr;
    }
    return g_api.value_box(klass, raw);
}

// Boxes a primitive into its System.* wrapper (for `object` params).
// Class-name table and packing owned by il2cpp_boxing.h; image lookup stays local.
void* box_primitive(const nami::tide::TideValue& v) {
    using namespace nami::tide;
    void* corlib = find_image("mscorlib");
    if (corlib == nullptr) {
        return nullptr;
    }
    nami::il2cpp::box::BoxHost host{g_api.class_from_name, g_api.value_box};
    return nami::il2cpp::box::BoxPrimitive(host, corlib, v);
}

// Resolves an exception's ToString into the request error_message (UTF-8, truncated).
// NOTE: il2cpp_format_exception crashes in the window-proc context (verified) - instead
// invoke System.Exception.ToString() on the exception object, a normal runtime_invoke.
void capture_exception(nami::tide::CallRequest& req, void* exc) {
    if (exc == nullptr) {
        return;
    }
    char* text = nullptr;
    // Find ToString on the exception's class hierarchy.
    void* exc_class = g_api.object_get_class(exc);
    void* tostring = exc_class != nullptr ? find_method_in_hierarchy(exc_class, "ToString", 0)
                                          : nullptr;
    if (tostring != nullptr) {
        void* exc2 = nullptr;
        void* result = g_api.runtime_invoke(tostring, exc, nullptr, &exc2);
        if (exc2 == nullptr && result != nullptr) {
            text = string_to_utf8(result);
        }
    }
    if (text != nullptr) {
        snprintf(req.error_message, sizeof(req.error_message), "%s", text);
        free(text);
    } else {
        snprintf(req.error_message, sizeof(req.error_message),
                 "<IL2CPP exception (no message)>");
    }
    log_tide("il2cpp: exception: %s", req.error_message);
}

// True when the method's class (or a base) is UnityEngine.Object.
bool is_unity_object_class(void* klass) {
    void* umod = find_image("UnityEngine.CoreModule");
    if (umod == nullptr) {
        return false;
    }
    void* obj_class = g_api.class_from_name(umod, "UnityEngine", "Object");
    if (obj_class == nullptr) {
        return false;
    }
    for (void* k = klass; k != nullptr; k = g_api.class_get_parent(k)) {
        if (k == obj_class) {
            return true;
        }
    }
    return false;
}

}  // namespace

// ---------------------------------------------------------------------------
// The typed op dispatcher. Mirrors the Mono backend's opcodes (tide_abi.h).
// Runs on the game's main thread inside its window proc.
// ---------------------------------------------------------------------------
int il2cpp_object_op_impl(nami::tide::CallRequest* req) {
    using namespace nami::tide;
    if (req == nullptr) {
        return -1;
    }
    req->result_code = -1;
    if (req->ret != nullptr) {
        req->ret->data.i64 = 0;  // NOTE: preserve ret->type (the REQUESTED type)
    }

    if (!resolve_api()) {
        log_tide("il2cpp: api not ready");
        return -1;
    }

    alignas(8) unsigned char box_storage[16 * 16] = {};
    void* il2cpp_args[16] = {};

    switch (req->op) {
        case TideCall_GetStaticField:
        case TideCall_SetStaticField:
        case TideCall_GetInstanceField:
        case TideCall_SetInstanceField: {
            void* klass = nullptr;
            void* obj = nullptr;
            const bool is_instance =
                req->op == TideCall_GetInstanceField || req->op == TideCall_SetInstanceField;
            if (is_instance) {
                if (req->arg_count < 1) {
                    log_tide("il2cpp: instance field op needs an instance handle");
                    return -1;
                }
                obj = resolve_instance(req->args[0]);
                if (obj == nullptr) {
                    return -1;
                }
                klass = g_api.object_get_class(obj);
            } else {
                klass = find_class(*req);
            }
            if (klass == nullptr) {
                return -1;
            }

            const bool is_set =
                req->op == TideCall_SetStaticField || req->op == TideCall_SetInstanceField;
            const int value_index = is_instance ? 1 : 0;

            void* field = find_field_in_hierarchy(klass, req->member);
            // IL2CPP has no property-reflection API: C# properties compile to get_X/set_X
            // methods. When no field matches, fall back to the accessor methods.
            void* getter = nullptr;
            void* setter = nullptr;
            if (field == nullptr) {
                char accessor[192];
                if (is_set) {
                    snprintf(accessor, sizeof(accessor), "set_%s", req->member);
                    setter = find_method_in_hierarchy(klass, accessor, 1);
                } else {
                    snprintf(accessor, sizeof(accessor), "get_%s", req->member);
                    getter = find_method_in_hierarchy(klass, accessor, 0);
                }
                if (getter == nullptr && setter == nullptr) {
                    log_tide("il2cpp: neither field '%s' nor accessor on %s.%s", req->member,
                             req->ns, req->klass);
                    return -1;
                }
            }

            if (is_set) {
                if (req->arg_count <= value_index) {
                    log_tide("il2cpp: set-field needs a value");
                    return -1;
                }
                const TideValue& v = req->args[value_index];
                // Property setter: invoke set_X(obj, value).
                if (setter != nullptr) {
                    void* mono_val = nullptr;
                    void* box = box_storage;
                    if (v.type == TideType_String || v.type == TideType_Object) {
                        void* slot = box_storage;
                        void* mono_obj =
                            v.type == TideType_String ? make_string(v) : resolve_instance(v);
                        if (mono_obj == nullptr) {
                            log_tide("il2cpp: cannot build set-property value");
                            return -1;
                        }
                        *reinterpret_cast<void**>(slot) = mono_obj;
                        mono_val = slot;
                    } else {
                        if (!to_il2cpp_arg(v, box, &mono_val)) {
                            log_tide("il2cpp: unsupported set-property value type %d",
                                     (int)v.type);
                            return -1;
                        }
                    }
                    void* args_arr[1] = { mono_val };
                    void* exc = nullptr;
                    g_api.runtime_invoke(setter, obj, args_arr, &exc);
                    if (exc != nullptr) {
                        log_tide("il2cpp: property setter '%s' threw", req->member);
                        capture_exception(*req, exc);
                        return -2;
                    }
                    if (req->ret != nullptr) {
                        req->ret->type = TideType_Bool;
                        req->ret->data.boolean = 1;
                    }
                    log_tide("il2cpp: set property %s OK", req->member);
                    return 0;
                }
                // Reference types (string/object): field_set_value takes a POINTER to the
                // object pointer. Value types: pointer to the raw value.
                if (v.type == TideType_String || v.type == TideType_Object) {
                    void* slot = box_storage;
                    void* mono_obj =
                        v.type == TideType_String ? make_string(v) : resolve_instance(v);
                    if (mono_obj == nullptr) {
                        log_tide("il2cpp: cannot build set-field value");
                        return -1;
                    }
                    *reinterpret_cast<void**>(slot) = mono_obj;
                    if (is_instance) {
                        g_api.field_set_value(obj, field, slot);
                    } else {
                        g_api.field_static_set_value(field, slot);
                    }
                } else {
                    void* box = box_storage;
                    void* val = nullptr;
                    if (!to_il2cpp_arg(v, box, &val)) {
                        log_tide("il2cpp: unsupported set-field value type %d", (int)v.type);
                        return -1;
                    }
                    if (is_instance) {
                        g_api.field_set_value(obj, field, val);
                    } else {
                        g_api.field_static_set_value(field, val);
                    }
                }
                if (req->ret != nullptr) {
                    req->ret->type = TideType_Bool;
                    req->ret->data.boolean = 1;
                }
                log_tide("il2cpp: set field %s OK", req->member);
                return 0;
            }

            // Get: fields read raw values; properties invoke get_X.
            if (req->ret == nullptr) {
                return -1;
            }
            if (getter != nullptr) {
                void* exc = nullptr;
                void* value = g_api.runtime_invoke(getter, obj, nullptr, &exc);
                if (exc != nullptr) {
                    log_tide("il2cpp: property getter '%s' threw", req->member);
                    capture_exception(*req, exc);
                    return -2;
                }
                write_ret(req->ret, value);
                log_tide("il2cpp: get property %s OK", req->member);
                return 0;
            }
            const nami::tide::TideValueType want = req->ret->type;
            if (want == nami::tide::TideType_Object) {
                void* out = nullptr;
                if (is_instance) {
                    g_api.field_get_value(obj, field, &out);
                } else {
                    g_api.field_static_get_value(field, &out);
                }
                if (out != nullptr) {
                    req->ret->data.handle = g_api.gchandle_new(out, 1);
                } else {
                    req->ret->data.handle = 0;
                }
                return 0;
            }
            if (want == TideType_String) {
                void* out = nullptr;
                if (is_instance) {
                    g_api.field_get_value(obj, field, &out);
                } else {
                    g_api.field_static_get_value(field, &out);
                }
                char* utf8 = string_to_utf8(out);
                if (utf8 != nullptr) {
                    req->ret->data.str.utf8 = utf8;
                    req->ret->data.str.len = static_cast<int32_t>(strlen(utf8));
                } else {
                    req->ret->type = TideType_Void;
                }
                return 0;
            }
            // Primitive read: field_get_value writes the raw value.
            {
                alignas(8) unsigned char raw[8] = {};
                if (is_instance) {
                    g_api.field_get_value(obj, field, raw);
                } else {
                    g_api.field_static_get_value(field, raw);
                }
                // The field's raw storage is the enum/int/float/etc.
                switch (want) {
                    case TideType_I32:
                        req->ret->data.i32 = *reinterpret_cast<int32_t*>(raw);
                        break;
                    case TideType_I64:
                        req->ret->data.i64 = *reinterpret_cast<int64_t*>(raw);
                        break;
                    case TideType_R4:
                        req->ret->data.r4 = *reinterpret_cast<float*>(raw);
                        break;
                    case TideType_R8:
                        req->ret->data.r8 = *reinterpret_cast<double*>(raw);
                        break;
                    case TideType_Bool:
                        req->ret->data.boolean = *reinterpret_cast<int32_t*>(raw);
                        break;
                    default:
                        req->ret->type = TideType_Void;
                        break;
                }
                return 0;
            }
        }

        case TideCall_InvokeStatic:
        case TideCall_InvokeInstance: {
            void* klass = nullptr;
            void* obj = nullptr;
            int value_start = 0;
            if (req->op == TideCall_InvokeInstance) {
                if (req->arg_count < 1) {
                    log_tide("il2cpp: instance invoke needs an instance handle");
                    return -1;
                }
                obj = resolve_instance(req->args[0]);
                if (obj == nullptr) {
                    return -1;
                }
                klass = g_api.object_get_class(obj);
                value_start = 1;
            } else {
                klass = find_class(*req);
            }
            if (klass == nullptr) {
                return -1;
            }

            const int method_argc = req->arg_count - value_start;
            // Overload-aware lookup mirroring the Mono backend (exact primitive
            // matches win; System.Object params accept boxed primitives); falls back
            // to first-match name+arity when signature info is unavailable.
            // Properties resolve to their accessor methods.
            void* method = find_method_for_args(klass, req->member, method_argc,
                                                req->args + value_start);
            if (method == nullptr) {
                log_tide("il2cpp: method '%s' with %d args not found on %s.%s", req->member,
                         method_argc, req->ns, req->klass);
                return -1;
            }
            log_tide("il2cpp: invoke '%s' argc=%d", req->member, method_argc);

            for (int i = 0; i < method_argc; i++) {
                const TideValue& v = req->args[value_start + i];
                void* box = box_storage + (i * 16);
                // On IL2CPP, runtime_invoke expects value-type params as raw pointers and
                // reference params as object pointers (same as Mono). Box primitives for
                // System.Object params (covers the Debug.Log(object) case generally);
                // everything else flows through the raw-arg path below unchanged.
                const int param = param_type_enum(method, i);
                if (param == IL2CPP_TYPE_OBJECT && v.type != TideType_String &&
                    v.type != TideType_Object) {
                    void* boxed = box_primitive(v);
                    if (boxed == nullptr) {
                        log_tide("il2cpp: failed to box arg for object param");
                        return -1;
                    }
                    il2cpp_args[i] = boxed;
                    continue;
                }
                if (!to_il2cpp_arg(v, box, &il2cpp_args[i])) {
                    log_tide("il2cpp: unsupported arg type %d at index %d", (int)v.type, i);
                    return -1;
                }
            }

            void* exc = nullptr;
            void* result = g_api.runtime_invoke(method, obj,
                                                method_argc > 0 ? il2cpp_args : nullptr, &exc);
            if (exc != nullptr) {
                log_tide("il2cpp: invoke '%s' threw", req->member);
                capture_exception(*req, exc);
                return -2;
            }
            write_ret(req->ret, result);
            log_tide("il2cpp: invoke %s OK", req->member);
            return 0;
        }

        case TideCall_NewObject: {
            void* klass = find_class(*req);
            if (klass == nullptr) {
                return -1;
            }
            g_api.runtime_class_init(klass);
            void* obj = g_api.object_new(klass);
            if (obj == nullptr) {
                log_tide("il2cpp: object_new failed for %s.%s", req->ns, req->klass);
                return -1;
            }
            void* ctor = find_method_in_hierarchy(klass, ".ctor", 0);
            if (ctor != nullptr) {
                void* exc = nullptr;
                g_api.runtime_invoke(ctor, obj, nullptr, &exc);
                if (exc != nullptr) {
                    log_tide("il2cpp: .ctor threw for %s.%s", req->ns, req->klass);
                    capture_exception(*req, exc);
                    return -2;
                }
            }
            if (req->ret != nullptr) {
                req->ret->type = TideType_Object;
                req->ret->data.handle = g_api.gchandle_new(obj, 1);
            }
            log_tide("il2cpp: new %s.%s OK", req->ns, req->klass);
            return 0;
        }

        case TideCall_FindObject: {
            // Scene-object discovery as FindObjectsOfType + element 0: the SINGULAR
            // FindObjectOfType wrapper aborts the process (0xe0000001) when invoked
            // from outside managed game code (verified on Unity 2022.3 Mono across
            // drain pre/post and window contexts), while the plural path returns
            // cleanly. The window-proc executor has no nested invoke frame.
            void* klass = find_class(*req);
            if (klass == nullptr) {
                return -1;
            }
            if (g_api.class_get_type == nullptr || g_api.type_get_object == nullptr) {
                log_tide("il2cpp: type reflection unavailable for FindObject");
                return -1;
            }
            void* type = g_api.class_get_type(klass);
            void* type_obj = type != nullptr ? g_api.type_get_object(type) : nullptr;
            if (type_obj == nullptr) {
                log_tide("il2cpp: cannot make System.Type for %s.%s", req->ns, req->klass);
                return -1;
            }
            void* core = find_image("UnityEngine.CoreModule");
            void* obj_class =
                core != nullptr ? g_api.class_from_name(core, "UnityEngine", "Object")
                                : nullptr;
            if (obj_class == nullptr) {
                log_tide("il2cpp: UnityEngine.Object not found");
                return -1;
            }
            void* find = find_method_in_hierarchy(obj_class, "FindObjectsOfType", 1);
            if (find == nullptr) {
                log_tide("il2cpp: no FindObjectsOfType entry on UnityEngine.Object");
                return -1;
            }
            void* find_args[1] = {type_obj};
            void* exc = nullptr;
            void* found = g_api.runtime_invoke(find, nullptr, find_args, &exc);
            if (exc != nullptr) {
                log_tide("il2cpp: FindObject threw");
                capture_exception(*req, exc);
                return -2;
            }
            // Plural returns an (never-null) array: miss on empty, else element 0
            // (reference array - direct slot read, same as ArrayGet).
            void* element = nullptr;
            if (found != nullptr && g_api.array_length != nullptr &&
                g_api.array_length(found) > 0 && array_element_is_reference(found)) {
                element = *reinterpret_cast<void**>(array_element_ptr(found, 0, 8));
            }
            if (req->ret != nullptr) {
                req->ret->type = TideType_Object;
                req->ret->data.handle =
                    element != nullptr
                        ? static_cast<int64_t>(g_api.gchandle_new(element, 1))
                        : 0;
            }
            log_tide("il2cpp: FindObject %s.%s %s", req->ns, req->klass,
                     element != nullptr ? "hit" : "miss");
            return 0;
        }

        case TideCall_FreeHandle: {
            if (req->arg_count >= 1 && req->args[0].type == TideType_Object) {
                g_api.gchandle_free(static_cast<uint64_t>(req->args[0].data.handle));
                return 0;
            }
            return -1;
        }

        case TideCall_ArrayLength: {
            if (req->arg_count < 1 || req->args[0].type != TideType_Object ||
                g_api.array_length == nullptr) {
                return -1;
            }
            void* arr = resolve_instance(req->args[0]);
            if (arr == nullptr) {
                return -1;
            }
            if (req->ret != nullptr) {
                req->ret->type = TideType_I32;
                req->ret->data.i32 = g_api.array_length(arr);
            }
            return 0;
        }

        case TideCall_ArrayGet: {
            if (req->arg_count < 2 || req->args[0].type != TideType_Object ||
                req->args[1].type != TideType_I32) {
                return -1;
            }
            void* arr = resolve_instance(req->args[0]);
            if (arr == nullptr) {
                return -1;
            }
            const int index = req->args[1].data.i32;
            const int len = g_api.array_length(arr);
            if (index < 0 || index >= len) {
                log_tide("il2cpp: array index %d out of range (len %d)", index, len);
                return -1;
            }
            if (req->ret == nullptr) {
                return 0;
            }

            // Reference array (string[]/object[]/T[]): slot holds an object pointer.
            if (array_element_is_reference(arr)) {
                void* elem = *reinterpret_cast<void**>(array_element_ptr(arr, index, 8));
                if (elem == nullptr) {
                    req->ret->type = TideType_Void;
                    req->ret->data.i64 = 0;
                    return 0;
                }
                if (req->ret->type == TideType_String) {
                    char* utf8 = string_to_utf8(elem);
                    if (utf8 != nullptr) {
                        req->ret->data.str.utf8 = utf8;
                        req->ret->data.str.len = static_cast<int32_t>(strlen(utf8));
                    } else {
                        req->ret->type = TideType_Void;
                    }
                    return 0;
                }
                // Object/element request: wrap in a handle.
                req->ret->type = TideType_Object;
                req->ret->data.handle = g_api.gchandle_new(elem, 1);
                return 0;
            }

            // Value array (int/float/enum/...): read the raw element bytes.
            const size_t esz = array_element_size(arr);
            void* slot = array_element_ptr(arr, index, esz);
            switch (req->ret->type) {
                case TideType_I32:
                    if (esz >= 4) {
                        req->ret->data.i32 = *reinterpret_cast<int32_t*>(slot);
                    } else if (esz == 1) {
                        req->ret->data.i32 = *reinterpret_cast<int8_t*>(slot);
                    } else if (esz == 2) {
                        req->ret->data.i32 = *reinterpret_cast<int16_t*>(slot);
                    } else {
                        req->ret->type = TideType_Void;
                    }
                    break;
                case TideType_I64:
                    if (esz >= 8) {
                        req->ret->data.i64 = *reinterpret_cast<int64_t*>(slot);
                    } else {
                        req->ret->type = TideType_Void;
                    }
                    break;
                case TideType_R4:
                    if (esz >= 4) {
                        req->ret->data.r4 = *reinterpret_cast<float*>(slot);
                    } else {
                        req->ret->type = TideType_Void;
                    }
                    break;
                case TideType_R8:
                    if (esz >= 8) {
                        req->ret->data.r8 = *reinterpret_cast<double*>(slot);
                    } else {
                        req->ret->type = TideType_Void;
                    }
                    break;
                case TideType_Bool:
                    req->ret->data.boolean = *reinterpret_cast<int8_t*>(slot) != 0;
                    break;
                default:
                    req->ret->type = TideType_Void;
                    break;
            }
            return 0;
        }

        case TideCall_ArraySet: {
            if (req->arg_count < 3 || req->args[0].type != TideType_Object ||
                req->args[1].type != TideType_I32) {
                return -1;
            }
            void* arr = resolve_instance(req->args[0]);
            if (arr == nullptr) {
                return -1;
            }
            const int index = req->args[1].data.i32;
            const int len = g_api.array_length(arr);
            if (index < 0 || index >= len) {
                log_tide("il2cpp: array index %d out of range (len %d)", index, len);
                return -1;
            }
            const TideValue& v = req->args[2];

            if (array_element_is_reference(arr)) {
                void* elem = nullptr;
                if (v.type == TideType_String) {
                    elem = make_string(v);
                } else if (v.type == TideType_Object) {
                    elem = resolve_instance(v);
                } else if (v.type == TideType_Void) {
                    elem = nullptr;
                } else {
                    log_tide("il2cpp: cannot set primitive into reference array");
                    return -1;
                }
                *reinterpret_cast<void**>(array_element_ptr(arr, index, 8)) = elem;
                return 0;
            }

            // Value array: write raw bytes.
            const size_t esz = array_element_size(arr);
            void* slot = array_element_ptr(arr, index, esz);
            switch (v.type) {
                case TideType_I32:
                    if (esz == 1) {
                        *reinterpret_cast<int8_t*>(slot) = static_cast<int8_t>(v.data.i32);
                    } else if (esz == 2) {
                        *reinterpret_cast<int16_t*>(slot) = static_cast<int16_t>(v.data.i32);
                    } else if (esz >= 4) {
                        *reinterpret_cast<int32_t*>(slot) = v.data.i32;
                    } else {
                        return -1;
                    }
                    break;
                case TideType_I64:
                    if (esz >= 8) {
                        *reinterpret_cast<int64_t*>(slot) = v.data.i64;
                    } else {
                        return -1;
                    }
                    break;
                case TideType_R4:
                    if (esz >= 4) {
                        *reinterpret_cast<float*>(slot) = v.data.r4;
                    } else {
                        return -1;
                    }
                    break;
                case TideType_R8:
                    if (esz >= 8) {
                        *reinterpret_cast<double*>(slot) = v.data.r8;
                    } else {
                        return -1;
                    }
                    break;
                case TideType_Bool:
                    *reinterpret_cast<int8_t*>(slot) = v.data.boolean ? 1 : 0;
                    break;
                default:
                    log_tide("il2cpp: unsupported value-array set type %d", (int)v.type);
                    return -1;
            }
            return 0;
        }

        default:
            log_tide("il2cpp: unknown object op %d", (int)req->op);
            return -1;
    }
}

}  // namespace nami::il2cpp

// ---------------------------------------------------------------------------
// Exports consumed by the managed Tide layer (Nami.Tide).
// ---------------------------------------------------------------------------

// Export: run a CallRequest on the game main thread via the IL2CPP executor.
extern "C" __declspec(dllexport) int nami_il2cpp_object_op(void* request) {
    using nami::tide::CallRequest;
    auto* req = static_cast<CallRequest*>(request);
    CallRequest local = *req;

    struct Shim {
        static int run(void* arg) {
            auto* r = static_cast<CallRequest*>(arg);
            int code = nami::il2cpp::il2cpp_object_op_impl(r);
            r->result_code = code;
            return code;
        }
    };

    const bool ok = nami::il2cpp::run_il2cpp_op(Shim::run, &local);
    *req = local;
    return ok ? local.result_code : -3;
}

// Export: run a BATCH of CallRequests in ONE main-thread round trip (the IL2CPP
// twin of nami_tide_object_op_batch - same contract: every op executes, per-op
// codes land in batch.codes[i], returns 0 when the batch ran).
extern "C" __declspec(dllexport) int nami_il2cpp_object_op_batch(void* batch) {
    using nami::tide::BatchRequest;
    using nami::tide::CallRequest;

    auto* b = static_cast<BatchRequest*>(batch);
    if (b == nullptr || b->count <= 0 || b->count > 256 || b->requests == nullptr ||
        b->codes == nullptr) {
        return -1;
    }

    auto locals = static_cast<CallRequest*>(
        HeapAlloc(GetProcessHeap(), 0, sizeof(CallRequest) * static_cast<size_t>(b->count)));
    if (locals == nullptr) {
        return -3;
    }
    for (int i = 0; i < b->count; i++) {
        locals[i] = *b->requests[i];
    }

    struct BatchCtx {
        CallRequest* locals;
        int32_t* codes;
        int count;
    };
    struct Shim {
        static int run(void* arg) {
            auto* c = static_cast<BatchCtx*>(arg);
            for (int i = 0; i < c->count; i++) {
                const int code = nami::il2cpp::il2cpp_object_op_impl(&c->locals[i]);
                c->locals[i].result_code = code;
                c->codes[i] = code;
            }
            return 0;
        }
    };

    BatchCtx ctx{locals, b->codes, b->count};
    const bool ok = nami::il2cpp::run_il2cpp_op(Shim::run, &ctx);

    for (int i = 0; i < b->count; i++) {
        *b->requests[i] = locals[i];
    }
    HeapFree(GetProcessHeap(), 0, locals);

    if (!ok) {
        for (int i = 0; i < b->count; i++) {
            b->codes[i] = -3;
        }
        return -3;
    }
    return 0;
}

