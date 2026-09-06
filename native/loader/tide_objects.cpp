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

namespace {

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
#undef LOAD

    if (g_api.get_root_domain != nullptr) {
        g_api.root_domain = g_api.get_root_domain();
    }

    g_api.ready = g_api.root_domain != nullptr && g_api.class_from_name != nullptr &&
                  g_api.class_get_field_from_name != nullptr && g_api.field_set_value != nullptr;
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

// Writes the mono object pointer for a string/object value into `slot` (a void*
// the caller provides), or boxes a primitive into `box` and returns it.
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
            void* method = find_method_in_hierarchy(klass, req->member, method_argc);
            if (method == nullptr) {
                // Try to find the method ignoring count as a fallback? No: mono needs the
                // exact count to disambiguate overloads; fail clearly.
                log_tide("tide: method '%s' with %d args not found on %s.%s",
                         req->member, method_argc, req->ns, req->klass);
                return -1;
            }

            for (int i = 0; i < method_argc; i++) {
                const TideValue& v = req->args[value_start + i];
                void* box = box_storage + (i * 16);
                if (!to_mono_arg(v, box, &mono_args[i])) {
                    log_tide("tide: unsupported arg type %d at index %d", (int)v.type, i);
                    return -1;
                }
            }

            void* exc = nullptr;
            void* result = g_api.runtime_invoke(method, obj, method_argc > 0 ? mono_args : nullptr, &exc);
            if (exc != nullptr) {
                log_tide("tide: invoke '%s' threw", req->member);
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
