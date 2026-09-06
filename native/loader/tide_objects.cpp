#include "tide_abi.h"
#include "tide_pump.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>

// ---------------------------------------------------------------------------
// Tide object ops: field access + method invocation with typed values, running
// on the game's main thread via the mono_runtime_invoke drain.
//
// Objects are exchanged as opaque handles backed by Mono GCHandles: the game GC
// keeps them alive, and CoreCLR never holds raw MonoObject pointers.
//
// STRING RETURN DISCIPLINE: when an op returns a string, it stores a mono_free-able
// UTF-8 pointer in ret->data.str.utf8. The MANAGED side must call nami_tide_free
// on it after copying (we add that export). All other returns are by value or handle.
// ---------------------------------------------------------------------------

namespace nami::tide {

// Keep the native CallRequest layout in sync with the managed mirror (Nami.Tide). On x64
// natural alignment the struct is 1192 bytes.
static_assert(sizeof(CallRequest) == 1192, "CallRequest layout changed; update the managed mirror");

namespace {

void* find_method_in_hierarchy(void* klass, const char* name, int argc);  // fwd (used early)

void log_tide(const char* fmt, ...) {
    wchar_t path[MAX_PATH]{};
    const HMODULE self = GetModuleHandleW(L"nami_loader.dll");
    if (self != nullptr) {
        GetModuleFileNameW(self, path, MAX_PATH);
        wchar_t* slash = wcsrchr(path, L'\\');
        if (slash != nullptr) {
            wcscpy_s(slash + 1, MAX_PATH - static_cast<size_t>(slash + 1 - path), L"nami-tide.log");
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

struct MonoApi {
    void* (*get_root_domain)() = nullptr;
    void* (*assembly_name_new)(const char*) = nullptr;
    void (*assembly_name_free)(void*) = nullptr;
    void* (*assembly_loaded)(void*) = nullptr;
    void* (*assembly_get_image)(void*) = nullptr;
    void* (*class_from_name)(void*, const char*, const char*) = nullptr;
    void* (*class_get_method_from_name)(void*, const char*, int) = nullptr;
    void* (*class_get_methods)(void*, void**) = nullptr;
    void* (*class_get_field_from_name)(void*, const char*) = nullptr;
    void* (*class_get_parent)(void*) = nullptr;
    void* (*class_get_property_from_name)(void*, const char*) = nullptr;
    void* (*property_get_get_method)(void*) = nullptr;
    void* (*property_get_set_method)(void*) = nullptr;
    void* (*runtime_invoke)(void*, void*, void**, void**) = nullptr;
    void* (*string_new)(void*, const char*) = nullptr;
    void* (*string_to_utf8)(void*) = nullptr;
    void (*mono_free)(void*) = nullptr;
    void* (*field_get_value_object)(void*, void*, void**) = nullptr;
    void (*field_set_value)(void*, void*, void*) = nullptr;
    void* (*gchandle_new)(void*, int) = nullptr;
    void* (*gchandle_get_target)(void*) = nullptr;
    void (*gchandle_free)(void*) = nullptr;
    void* (*object_unbox)(void*) = nullptr;
    void* (*object_get_class)(void*) = nullptr;
    void* (*object_new)(void*, void*) = nullptr;
    // Signature reflection (for boxing args to `object`/interface/base-class params).
    void* (*method_signature)(void*) = nullptr;
    void* (*method_get_name)(void*) = nullptr;
    void* (*signature_get_params)(void*, void**) = nullptr;
    int (*signature_get_param_count)(void*) = nullptr;
    void* (*class_from_mono_type)(void*) = nullptr;
    void* (*type_get_class)(void*) = nullptr;
    int (*type_get_type)(void*) = nullptr;
    // Type/array reflection (for scene-object discovery + array values).
    void* (*type_get_object)(void*, void*) = nullptr;
    void* (*class_get_type)(void*) = nullptr;
    int (*array_length)(void*) = nullptr;
    void* root_domain = nullptr;
    bool ready = false;
};

MonoApi g_api{};

void resolve_api() {
    if (g_api.ready) {
        return;
    }

    HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
    if (mono == nullptr) {
        mono = GetModuleHandleW(L"mono.dll");
    }
    if (mono == nullptr) {
        return;
    }

#define LOAD(name) \
    g_api.name = reinterpret_cast<decltype(g_api.name)>(GetProcAddress(mono, "mono_" #name))

    LOAD(get_root_domain);
    LOAD(assembly_name_new);
    LOAD(assembly_name_free);
    LOAD(assembly_loaded);
    LOAD(assembly_get_image);
    LOAD(class_from_name);
    LOAD(class_get_method_from_name);
    LOAD(class_get_methods);
    LOAD(class_get_field_from_name);
    LOAD(class_get_parent);
    LOAD(class_get_property_from_name);
    LOAD(property_get_get_method);
    LOAD(property_get_set_method);
    LOAD(runtime_invoke);
    LOAD(string_new);
    LOAD(string_to_utf8);
    g_api.mono_free = reinterpret_cast<decltype(g_api.mono_free)>(GetProcAddress(mono, "mono_free"));
    LOAD(field_get_value_object);
    LOAD(field_set_value);
    // GCHandle APIs: use the *_v2 variants. Unity's newer Mono (Unity 6) stores handles as
    // 64-bit encoded pointers into a page table that can live ABOVE 4 GB; the legacy
    // mono_gchandle_* entry points truncate the handle to 32 bits (mov %ecx) and fault on
    // such handles. The _v2 variants are full-64-bit and are exported by both old and new
    // Unity Mono builds.
    g_api.gchandle_new = reinterpret_cast<decltype(g_api.gchandle_new)>(
        GetProcAddress(mono, "mono_gchandle_new_v2"));
    g_api.gchandle_get_target = reinterpret_cast<decltype(g_api.gchandle_get_target)>(
        GetProcAddress(mono, "mono_gchandle_get_target_v2"));
    g_api.gchandle_free = reinterpret_cast<decltype(g_api.gchandle_free)>(
        GetProcAddress(mono, "mono_gchandle_free_v2"));
    LOAD(object_unbox);
    LOAD(object_get_class);
    LOAD(object_new);
    // Signature reflection (present on 2022.3 and Unity 6 Mono).
    LOAD(method_signature);
    LOAD(method_get_name);
    LOAD(signature_get_params);
    LOAD(signature_get_param_count);
    LOAD(class_from_mono_type);
    LOAD(type_get_class);
    LOAD(type_get_type);
    // Type/array reflection (present on 2022.3 and Unity 6 Mono).
    LOAD(type_get_object);
    LOAD(class_get_type);
    LOAD(array_length);
#undef LOAD

    if (g_api.get_root_domain != nullptr) {
        g_api.root_domain = g_api.get_root_domain();
    }

    g_api.ready = g_api.root_domain != nullptr && g_api.class_from_name != nullptr &&
                  g_api.class_get_field_from_name != nullptr && g_api.field_set_value != nullptr &&
                  g_api.runtime_invoke != nullptr && g_api.gchandle_new != nullptr &&
                  g_api.gchandle_get_target != nullptr && g_api.gchandle_free != nullptr;
}

// Writes the Mono exception's ToString() into the request's error_message buffer
// (best-effort, UTF-8, truncated to fit). Call only when `exc` is non-null.
void capture_exception(CallRequest& req, void* exc) {
    if (exc == nullptr) {
        return;
    }
    // exc is a Mono Exception object; calling ToString() on it is the most reliable way to
    // get a human-readable message (handles the message property + stack trace). ToString is
    // inherited from System.Exception, so walk the class hierarchy to find it.
    void* str = nullptr;
    void* exc_class = g_api.object_get_class(exc);
    void* tostring = exc_class != nullptr ? find_method_in_hierarchy(exc_class, "ToString", 0)
                                          : nullptr;
    if (tostring != nullptr) {
        void* exc2 = nullptr;
        void* result = g_api.runtime_invoke(tostring, exc, nullptr, &exc2);
        if (exc2 == nullptr && result != nullptr) {
            str = g_api.string_to_utf8(result);
        }
    }
    if (str != nullptr) {
        std::snprintf(req.error_message, sizeof(req.error_message), "%s",
                      static_cast<const char*>(str));
        g_api.mono_free(str);
    } else {
        std::snprintf(req.error_message, sizeof(req.error_message),
                      "<Mono exception (no message)>");
    }
    log_tide("tide: exception: %s", req.error_message);
}

void* find_assembly(const char* name) {
    char with_dll[192];
    snprintf(with_dll, sizeof(with_dll), "%s.dll", name);
    const char* names[] = {name, with_dll};
    for (const char* n : names) {
        void* aname = g_api.assembly_name_new(n);
        if (aname == nullptr) {
            continue;
        }
        void* asm_ = g_api.assembly_loaded(aname);
        g_api.assembly_name_free(aname);
        if (asm_ != nullptr) {
            return asm_;
        }
    }
    return nullptr;
}

void* find_class(const CallRequest& req) {
    void* asm_ = find_assembly(req.assembly);
    if (asm_ == nullptr) {
        log_tide("tide: assembly '%s' not found", req.assembly);
        return nullptr;
    }
    void* image = g_api.assembly_get_image(asm_);
    void* klass = g_api.class_from_name(image, req.ns, req.klass);
    if (klass == nullptr) {
        log_tide("tide: class '%s.%s' not found", req.ns, req.klass);
    }
    return klass;
}

// Searches a class AND its base classes for a method (mono only searches the class itself).
void* find_method_in_hierarchy(void* klass, const char* name, int argc) {
    for (void* k = klass; k != nullptr; k = g_api.class_get_parent(k)) {
        void* m = g_api.class_get_method_from_name(k, name, argc);
        if (m != nullptr) {
            return m;
        }
    }
    return nullptr;
}

// Searches a class AND its base classes for a field.
void* find_field_in_hierarchy(void* klass, const char* name) {
    for (void* k = klass; k != nullptr; k = g_api.class_get_parent(k)) {
        void* f = g_api.class_get_field_from_name(k, name);
        if (f != nullptr) {
            return f;
        }
    }
    return nullptr;
}

// Searches a class AND its base classes for a property.
void* find_property_in_hierarchy(void* klass, const char* name) {
    for (void* k = klass; k != nullptr; k = g_api.class_get_parent(k)) {
        void* p = g_api.class_get_property_from_name(k, name);
        if (p != nullptr) {
            return p;
        }
    }
    return nullptr;
}

void* resolve_instance(const TideValue& v) {
    if (v.type != TideType_Object || v.data.handle == 0) {
        log_tide("tide: expected an object handle argument");
        return nullptr;
    }
    return g_api.gchandle_get_target(reinterpret_cast<void*>(v.data.handle));
}

void* make_string(const TideValue& v) {
    if (v.type != TideType_String || v.data.str.utf8 == nullptr) {
        return nullptr;
    }
    return g_api.string_new(g_api.root_domain, v.data.str.utf8);
}

// MonoType constants (from mono/metadata/blob.h). We only need to distinguish
// reference types (object/string/class/array) from value types (I4/I8/R4/R8/BOOLEAN/...).
enum {
    MONO_TYPE_END = 0x00,
    MONO_TYPE_VOID = 0x01,
    MONO_TYPE_BOOLEAN = 0x02,
    MONO_TYPE_I4 = 0x08,
    MONO_TYPE_I8 = 0x0a,
    MONO_TYPE_R4 = 0x0c,
    MONO_TYPE_R8 = 0x0d,
    MONO_TYPE_STRING = 0x0e,
    MONO_TYPE_OBJECT = 0x1c,
    MONO_TYPE_CLASS = 0x12,
    MONO_TYPE_VALUETYPE = 0x11,
    MONO_TYPE_ARRAY = 0x1d,
    MONO_TYPE_SZARRAY = 0x1e,
    MONO_TYPE_GENERICINST = 0x2b,
};

// True when a mono parameter type is a reference type that requires a BOXED value
// (object, string, class, array, interface). Value types (including enums) are passed
// by their raw value via to_mono_arg.
bool param_type_is_reference(void* param_type) {
    const int t = g_api.type_get_type(param_type);
    switch (t) {
        case MONO_TYPE_OBJECT:
        case MONO_TYPE_STRING:
        case MONO_TYPE_CLASS:
        case MONO_TYPE_ARRAY:
        case MONO_TYPE_SZARRAY:
        case MONO_TYPE_GENERICINST:
            return true;
        default:
            return false;
    }
}

// Reads the parameter count of a method (from its signature). Returns -1 on failure.
int method_param_count(void* method) {
    if (g_api.method_signature == nullptr || method == nullptr) {
        return -1;
    }
    void* sig = g_api.method_signature(method);
    if (sig == nullptr) {
        return -1;
    }
    return g_api.signature_get_param_count(sig);
}

// Resolves the param MonoType* at index i (0-based) of a method, or nullptr.
// mono_signature_get_params returns ONE param per call (iterator API), so advance it
// `index` times.
void* method_param_type(void* method, int index) {
    if (g_api.method_signature == nullptr || g_api.signature_get_params == nullptr ||
        g_api.signature_get_param_count == nullptr || method == nullptr) {
        return nullptr;
    }
    void* sig = g_api.method_signature(method);
    if (sig == nullptr) {
        return nullptr;
    }
    int count = g_api.signature_get_param_count(sig);
    if (index < 0 || index >= count) {
        return nullptr;
    }
    void* iter = nullptr;
    void* param = nullptr;
    for (int i = 0; i <= index; i++) {
        param = g_api.signature_get_params(sig, &iter);
        if (param == nullptr) {
            return nullptr;
        }
    }
    return param;
}

// Boxes a primitive TideValue into a fresh Mono object (for object-typed params).
// The caller must keep the returned pointer alive until the invoke completes; it is a
// Mono heap object (GC-tracked), NOT something we free.
void* box_primitive(const TideValue& v) {
    switch (v.type) {
        case TideType_I32: {
            void* cls = g_api.class_from_name(
                g_api.assembly_get_image(find_assembly("mscorlib")), "System", "Int32");
            if (cls == nullptr) {
                return nullptr;
            }
            void* obj = g_api.object_new(g_api.root_domain, cls);
            if (obj == nullptr) {
                return nullptr;
            }
            *static_cast<int32_t*>(g_api.object_unbox(obj)) = v.data.i32;
            return obj;
        }
        case TideType_Bool: {
            void* cls = g_api.class_from_name(
                g_api.assembly_get_image(find_assembly("mscorlib")), "System", "Boolean");
            if (cls == nullptr) {
                return nullptr;
            }
            void* obj = g_api.object_new(g_api.root_domain, cls);
            if (obj == nullptr) {
                return nullptr;
            }
            *static_cast<int32_t*>(g_api.object_unbox(obj)) = v.data.boolean ? 1 : 0;
            return obj;
        }
        case TideType_I64: {
            void* cls = g_api.class_from_name(
                g_api.assembly_get_image(find_assembly("mscorlib")), "System", "Int64");
            if (cls == nullptr) {
                return nullptr;
            }
            void* obj = g_api.object_new(g_api.root_domain, cls);
            if (obj == nullptr) {
                return nullptr;
            }
            *static_cast<int64_t*>(g_api.object_unbox(obj)) = v.data.i64;
            return obj;
        }
        case TideType_R4: {
            void* cls = g_api.class_from_name(
                g_api.assembly_get_image(find_assembly("mscorlib")), "System", "Single");
            if (cls == nullptr) {
                return nullptr;
            }
            void* obj = g_api.object_new(g_api.root_domain, cls);
            if (obj == nullptr) {
                return nullptr;
            }
            *static_cast<float*>(g_api.object_unbox(obj)) = v.data.r4;
            return obj;
        }
        case TideType_R8: {
            void* cls = g_api.class_from_name(
                g_api.assembly_get_image(find_assembly("mscorlib")), "System", "Double");
            if (cls == nullptr) {
                return nullptr;
            }
            void* obj = g_api.object_new(g_api.root_domain, cls);
            if (obj == nullptr) {
                return nullptr;
            }
            *static_cast<double*>(g_api.object_unbox(obj)) = v.data.r8;
            return obj;
        }
        default:
            return nullptr;  // strings/objects are already references
    }
}

// Maps a TideValue type to the mono primitive type it marshals as.
int tide_type_to_mono_type(TideValueType t) {
    switch (t) {
        case TideType_I32: return MONO_TYPE_I4;
        case TideType_I64: return MONO_TYPE_I8;
        case TideType_R4: return MONO_TYPE_R4;
        case TideType_R8: return MONO_TYPE_R8;
        case TideType_Bool: return MONO_TYPE_BOOLEAN;
        case TideType_String: return MONO_TYPE_STRING;
        case TideType_Object: return MONO_TYPE_OBJECT;
        default: return MONO_TYPE_VOID;
    }
}

// Finds the method on `klass` (or a base) named `name` with `argc` params whose parameter
// types best match the given TideValue argument types. Scores exact primitive matches and
// reference-typed params (which accept boxed primitives). Falls back to the classic
// name+argc lookup (which may pick an arbitrary overload) when signature info is missing.
void* find_method_for_args(void* klass, const char* name, int argc,
                           const TideValue* args) {
    for (void* k = klass; k != nullptr; k = g_api.class_get_parent(k)) {
        // Collect candidates with the right name + arity.
        void* best = nullptr;
        int best_score = -1;

        void* iter = nullptr;
        void* method = nullptr;
        while (g_api.class_get_methods != nullptr &&
               (method = g_api.class_get_methods(k, &iter)) != nullptr) {
            const char* mname = static_cast<const char*>(g_api.method_get_name(method));
            if (mname == nullptr || std::strcmp(mname, name) != 0) {
                continue;
            }
            void* sig = g_api.method_signature(method);
            if (sig == nullptr) {
                continue;
            }
            const int count = g_api.signature_get_param_count(sig);
            if (count != argc) {
                continue;
            }

            // Score: +2 exact primitive/string/object match; +1 reference param (boxable);
            // -1 mismatch. Prefer the highest score; keep first on ties.
            int score = 0;
            bool usable = true;
            for (int i = 0; i < argc; i++) {
                // mono_signature_get_params is an iterator: one param per call.
                void* pit = nullptr;
                void* pt = nullptr;
                for (int j = 0; j <= i; j++) {
                    pt = g_api.signature_get_params(sig, &pit);
                    if (pt == nullptr) {
                        break;
                    }
                }
                if (pt == nullptr) {
                    usable = false;
                    break;
                }
                const int pt_type = g_api.type_get_type(pt);
                const int want = tide_type_to_mono_type(args[i].type);
                if (pt_type == want) {
                    score += 2;
                } else if (param_type_is_reference(pt)) {
                    // Any primitive can box to object; strings/objects pass as-is.
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

    // Fallback: classic name+argc lookup (no signature info / no match found).
    return nullptr;
}
bool to_mono_arg(const TideValue& v, void* box, void** mono_out) {
    switch (v.type) {
        case TideType_I32:
            *static_cast<int32_t*>(box) = v.data.i32;
            *mono_out = box;
            return true;
        case TideType_Bool:
            *static_cast<int32_t*>(box) = v.data.boolean ? 1 : 0;
            *mono_out = box;
            return true;
        case TideType_I64:
            *static_cast<int64_t*>(box) = v.data.i64;
            *mono_out = box;
            return true;
        case TideType_R4:
            *static_cast<float*>(box) = v.data.r4;
            *mono_out = box;
            return true;
        case TideType_R8:
            *static_cast<double*>(box) = v.data.r8;
            *mono_out = box;
            return true;
        case TideType_String: {
            void* s = make_string(v);
            if (s == nullptr) {
                return false;
            }
            *mono_out = s;
            return true;
        }
        case TideType_Object: {
            void* obj = resolve_instance(v);
            if (obj == nullptr) {
                return false;
            }
            *mono_out = obj;
            return true;
        }
        default:
            return false;
    }
}

// Writes `mono_result` into the managed ret slot according to the requested type.
void write_ret(TideValue* ret, void* mono_result) {
    if (ret == nullptr || mono_result == nullptr) {
        return;
    }
    switch (ret->type) {
        case TideType_I32:
            ret->data.i32 = *static_cast<int32_t*>(g_api.object_unbox(mono_result));
            break;
        case TideType_I64:
            ret->data.i64 = *static_cast<int64_t*>(g_api.object_unbox(mono_result));
            break;
        case TideType_R4:
            ret->data.r4 = *static_cast<float*>(g_api.object_unbox(mono_result));
            break;
        case TideType_R8:
            ret->data.r8 = *static_cast<double*>(g_api.object_unbox(mono_result));
            break;
        case TideType_Bool:
            ret->data.boolean = *static_cast<int32_t*>(g_api.object_unbox(mono_result));
            break;
        case TideType_String: {
            void* utf8 = g_api.string_to_utf8(mono_result);
            if (utf8 != nullptr) {
                ret->data.str.utf8 = static_cast<const char*>(utf8);
                ret->data.str.len = static_cast<int32_t>(strlen(static_cast<const char*>(utf8)));
            } else {
                ret->type = TideType_Void;
            }
            break;
        }
        case TideType_Object:
            ret->data.handle = handle_store_create(mono_result);
            break;
        default:
            ret->type = TideType_Void;
            break;
    }
}

}  // namespace

int64_t handle_store_create(void* mono_object) {
    if (mono_object == nullptr) {
        return 0;
    }
    void* gch = g_api.gchandle_new(mono_object, 1);
    return gch == nullptr ? 0 : reinterpret_cast<int64_t>(gch);
}

void* handle_store_resolve(int64_t handle) {
    if (handle == 0) {
        return nullptr;
    }
    return g_api.gchandle_get_target(reinterpret_cast<void*>(handle));
}

void handle_store_release(int64_t handle) {
    if (handle == 0) {
        return;
    }
    g_api.gchandle_free(reinterpret_cast<void*>(handle));
}
// ---------------------------------------------------------------------------

int tide_object_op(void* arg) {
    auto* req = static_cast<CallRequest*>(arg);
    req->result_code = -1;

    if (req->ret != nullptr) {
        req->ret->type = TideType_Void;
        req->ret->data.i64 = 0;
    }

    resolve_api();
    if (!g_api.ready) {
        log_tide("tide: mono api not ready");
        return -1;
    }

    // Staging for boxing primitives (16 args max, 16 bytes each).
    alignas(8) unsigned char box_storage[16 * 16] = {};
    void* mono_args[16] = {};

    switch (req->op) {
        case TideCall_GetStaticField:
        case TideCall_SetStaticField:
        case TideCall_GetInstanceField:
        case TideCall_SetInstanceField: {
            void* klass = nullptr;
            void* obj = nullptr;
            bool is_instance = req->op == TideCall_GetInstanceField || req->op == TideCall_SetInstanceField;
            if (is_instance) {
                if (req->arg_count < 1) {
                    log_tide("tide: instance field op needs an instance handle");
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

            void* field = find_field_in_hierarchy(klass, req->member);
            void* prop = nullptr;
            if (field == nullptr) {
                // Fall back to a property (e.g. Unity's static properties like Time.timeScale).
                prop = find_property_in_hierarchy(klass, req->member);
                if (prop == nullptr) {
                    log_tide("tide: neither field nor property '%s' found on %s.%s",
                             req->member, req->ns, req->klass);
                    return -1;
                }
            }

            bool is_set = req->op == TideCall_SetStaticField || req->op == TideCall_SetInstanceField;
            if (is_set) {
                int value_index = is_instance ? 1 : 0;
                if (req->arg_count <= value_index) {
                    log_tide("tide: set-field needs a value");
                    return -1;
                }
                const TideValue& v = req->args[value_index];

                if (prop != nullptr) {
                    // Property setter: invoke set_<name>(obj, value).
                    void* setter = g_api.property_get_set_method(prop);
                    if (setter == nullptr) {
                        log_tide("tide: property '%s' has no setter", req->member);
                        return -1;
                    }
                    // Build the value arg + a staging slot for the value pointer.
                    void* mono_val = nullptr;
                    void* box = box_storage;
                    if (v.type == TideType_String || v.type == TideType_Object) {
                        void* slot_storage = box_storage;
                        void* mono_obj = v.type == TideType_String ? make_string(v) : resolve_instance(v);
                        if (mono_obj == nullptr) {
                            return -1;
                        }
                        *reinterpret_cast<void**>(slot_storage) = mono_obj;
                        mono_val = slot_storage;
                    } else {
                        if (!to_mono_arg(v, box, &mono_val)) {
                            log_tide("tide: unsupported set-property value type %d", (int)v.type);
                            return -1;
                        }
                    }
                    void* args_arr[1] = { mono_val };
                    void* exc = nullptr;
                    g_api.runtime_invoke(setter, obj, args_arr, &exc);
                    if (exc != nullptr) {
                        log_tide("tide: property setter '%s' threw", req->member);
                        capture_exception(*req, exc);
                        return -2;
                    }
                    if (req->ret != nullptr) {
                        req->ret->type = TideType_Bool;
                        req->ret->data.boolean = 1;
                    }
                    log_tide("tide: set property %s OK", req->member);
                    return 0;
                }

                // mono_field_set_value wants: value types -> pointer to raw value;
                // strings/objects -> pointer to the object pointer.
                if (v.type == TideType_String || v.type == TideType_Object) {
                    void* slot_storage = box_storage;
                    void* mono_obj = v.type == TideType_String ? make_string(v) : resolve_instance(v);
                    if (mono_obj == nullptr) {
                        log_tide("tide: cannot build set-field value");
                        return -1;
                    }
                    *reinterpret_cast<void**>(slot_storage) = mono_obj;
                    g_api.field_set_value(field, obj, slot_storage);
                } else {
                    void* box = box_storage;
                    void* mono_val = nullptr;
                    if (!to_mono_arg(v, box, &mono_val)) {
                        log_tide("tide: unsupported set-field value type %d", (int)v.type);
                        return -1;
                    }
                    g_api.field_set_value(field, obj, mono_val);
                }
                if (req->ret != nullptr) {
                    req->ret->type = TideType_Bool;
                    req->ret->data.boolean = 1;
                }
                log_tide("tide: set field %s OK", req->member);
                return 0;
            }

            void* exc = nullptr;
            void* value = nullptr;
            if (prop != nullptr) {
                // Property getter: invoke get_<name>(obj).
                void* getter = g_api.property_get_get_method(prop);
                log_tide("tide: property '%s' getter=%p", req->member, (void*)getter);
                if (getter == nullptr) {
                    log_tide("tide: property '%s' has no getter", req->member);
                    return -1;
                }
                log_tide("tide: invoking property getter '%s' on main thread", req->member);
                value = g_api.runtime_invoke(getter, obj, nullptr, &exc);
                log_tide("tide: property getter '%s' returned value=%p exc=%p", req->member, (void*)value, (void*)exc);
                if (exc != nullptr) {
                    log_tide("tide: property getter '%s' threw", req->member);
                    capture_exception(*req, exc);
                    return -2;
                }
            } else {
                value = g_api.field_get_value_object(field, obj, &exc);
                if (exc != nullptr || value == nullptr) {
                    log_tide("tide: field_get_value_object failed for '%s'", req->member);
                    return -1;
                }
            }
            write_ret(req->ret, value);
            log_tide("tide: get member %s OK", req->member);
            return 0;
        }

        case TideCall_InvokeStatic:
        case TideCall_InvokeInstance: {
            void* klass = nullptr;
            void* obj = nullptr;
            int value_start = 0;
            if (req->op == TideCall_InvokeInstance) {
                if (req->arg_count < 1) {
                    log_tide("tide: instance invoke needs an instance handle");
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

            int method_argc = req->arg_count - value_start;
            // Type-aware overload selection: pick the method whose parameter types best match
            // the passed TideValue types (avoids binding the wrong same-arity overload, e.g.
            // Debug.Log(string) when an int was passed for Log(object)).
            const TideValue* method_args = method_argc > 0 ? req->args + value_start : nullptr;
            void* method = g_api.class_get_methods != nullptr && g_api.method_signature != nullptr
                               ? find_method_for_args(klass, req->member, method_argc, method_args)
                               : nullptr;
            if (method == nullptr) {
                method = find_method_in_hierarchy(klass, req->member, method_argc);
            }
            if (method == nullptr) {
                log_tide("tide: method '%s' with %d args not found on %s.%s",
                         req->member, method_argc, req->ns, req->klass);
                return -1;
            }

            for (int i = 0; i < method_argc; i++) {
                const TideValue& v = req->args[value_start + i];
                void* box = box_storage + (i * 16);
                // Signature-aware marshaling: if the target parameter is a reference type
                // (object/string/class/...), a primitive value must be BOXED into a Mono
                // object first; a raw value pointer would crash Mono when it treats it as
                // an object reference (e.g. Debug.Log(object) with an int).
                void* param_type = method_param_type(method, i);
                const bool need_box = param_type != nullptr && param_type_is_reference(param_type) &&
                                      v.type != TideType_String && v.type != TideType_Object;
                if (need_box) {
                    void* boxed = box_primitive(v);
                    if (boxed == nullptr) {
                        log_tide("tide: failed to box arg %d for reference parameter", i);
                        return -1;
                    }
                    mono_args[i] = boxed;
                    continue;
                }
                if (!to_mono_arg(v, box, &mono_args[i])) {
                    log_tide("tide: unsupported arg type %d at index %d", (int)v.type, i);
                    return -1;
                }
            }

            void* exc = nullptr;
            void* result = g_api.runtime_invoke(method, obj, method_argc > 0 ? mono_args : nullptr, &exc);
            if (exc != nullptr) {
                log_tide("tide: invoke '%s' threw", req->member);
                capture_exception(*req, exc);
                return -2;
            }
            write_ret(req->ret, result);
            log_tide("tide: invoke %s OK", req->member);
            return 0;
        }

        case TideCall_NewObject: {
            void* klass = find_class(*req);
            if (klass == nullptr) {
                return -1;
            }
            void* obj = g_api.object_new(g_api.root_domain, klass);
            if (obj == nullptr) {
                log_tide("tide: mono_object_new failed for %s.%s", req->ns, req->klass);
                return -1;
            }
            // Run the parameterless ctor if present.
            void* ctor = find_method_in_hierarchy(klass, ".ctor", 0);
            if (ctor != nullptr) {
                void* exc = nullptr;
                g_api.runtime_invoke(ctor, obj, nullptr, &exc);
                if (exc != nullptr) {
                    log_tide("tide: .ctor threw for %s.%s", req->ns, req->klass);
                    capture_exception(*req, exc);
                    return -2;
                }
            }
            if (req->ret != nullptr) {
                req->ret->type = TideType_Object;
                req->ret->data.handle = handle_store_create(obj);
            }
            log_tide("tide: new %s.%s OK", req->ns, req->klass);
            return 0;
        }

        case TideCall_FreeHandle: {
            if (req->arg_count >= 1 && req->args[0].type == TideType_Object) {
                handle_store_release(req->args[0].data.handle);
                return 0;
            }
            return -1;
        }

        default:
            log_tide("tide: unknown object op %d", (int)req->op);
            return -1;
    }
}

}  // namespace nami::tide

// Export: run a CallRequest on the game main thread. Returns the op's result code
// (0 = success, negative = failure).
extern "C" __declspec(dllexport) int nami_tide_object_op(void* request) {
    using nami::tide::CallRequest;

    auto* req = static_cast<CallRequest*>(request);
    CallRequest local = *req;  // copy so the export owns it during the wait

    // Shim: capture the op's return value into result_code.
    struct Shim {
        static int run(void* arg) {
            auto* r = static_cast<CallRequest*>(arg);
            int code = nami::tide::tide_object_op(r);
            r->result_code = code;
            return code;
        }
    };

    const bool ok = nami::tide::run_on_main_thread(Shim::run, &local);
    *req = local;  // write back (ret slot, result_code)
    return ok ? local.result_code : -3;
}

// Export: free a string buffer returned by an op (mono_string_to_utf8 result).
extern "C" __declspec(dllexport) void nami_tide_free(void* ptr) {
    if (ptr == nullptr) {
        return;
    }
    HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
    if (mono != nullptr) {
        using mono_free_fn = void (*)(void*);
        auto fn = reinterpret_cast<mono_free_fn>(
            reinterpret_cast<void*>(GetProcAddress(mono, "mono_free")));
        if (fn != nullptr) {
            fn(ptr);
        }
    }
}
