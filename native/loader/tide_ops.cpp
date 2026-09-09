#include "tide_pump.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>

namespace nami::tide {

namespace {

// Appends a line to <loader dir>/nami-tide.log (best-effort diagnostics).
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

// Mono embedding exports, resolved lazily (executed on the game main thread via the drain).
struct MonoApi {
    // Resolved once.
    void* (*get_root_domain)() = nullptr;
    void* (*thread_attach)(void*) = nullptr;
    void* (*assembly_name_new)(const char*) = nullptr;
    void (*assembly_name_free)(void*) = nullptr;
    void* (*assembly_loaded)(void*) = nullptr;   // takes MonoAssemblyName*
    void* (*assembly_get_image)(void*) = nullptr;
    void* (*class_from_name)(void*, const char*, const char*) = nullptr;
    void* (*class_get_method_from_name)(void*, const char*, int) = nullptr;
    void* (*runtime_invoke)(void*, void*, void**, void**) = nullptr;
    void* (*string_new)(void*, const char*) = nullptr;
    void* (*string_to_utf8)(void*) = nullptr;
    void (*mono_free)(void*) = nullptr;
    int (*gc_is_incremental)() = nullptr;
    void (*gc_set_incremental)(int) = nullptr;
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
        log_tide("tide: mono module not found (err=%lu)", GetLastError());
        return;
    }

    log_tide("tide: mono module at %p", (void*)mono);

    g_api.get_root_domain = reinterpret_cast<decltype(g_api.get_root_domain)>(GetProcAddress(mono, "mono_get_root_domain"));
    g_api.thread_attach = reinterpret_cast<decltype(g_api.thread_attach)>(GetProcAddress(mono, "mono_thread_attach"));
    g_api.assembly_name_new = reinterpret_cast<decltype(g_api.assembly_name_new)>(GetProcAddress(mono, "mono_assembly_name_new"));
    g_api.assembly_name_free = reinterpret_cast<decltype(g_api.assembly_name_free)>(GetProcAddress(mono, "mono_assembly_name_free"));
    g_api.assembly_loaded = reinterpret_cast<decltype(g_api.assembly_loaded)>(GetProcAddress(mono, "mono_assembly_loaded"));
    g_api.assembly_get_image = reinterpret_cast<decltype(g_api.assembly_get_image)>(GetProcAddress(mono, "mono_assembly_get_image"));
    g_api.class_from_name = reinterpret_cast<decltype(g_api.class_from_name)>(GetProcAddress(mono, "mono_class_from_name"));
    g_api.class_get_method_from_name = reinterpret_cast<decltype(g_api.class_get_method_from_name)>(GetProcAddress(mono, "mono_class_get_method_from_name"));
    g_api.runtime_invoke = reinterpret_cast<decltype(g_api.runtime_invoke)>(GetProcAddress(mono, "mono_runtime_invoke"));
    g_api.string_new = reinterpret_cast<decltype(g_api.string_new)>(GetProcAddress(mono, "mono_string_new"));
    g_api.string_to_utf8 = reinterpret_cast<decltype(g_api.string_to_utf8)>(GetProcAddress(mono, "mono_string_to_utf8"));
    g_api.mono_free = reinterpret_cast<decltype(g_api.mono_free)>(GetProcAddress(mono, "mono_free"));

    log_tide("tide: resolved root=%p attach=%p name_new=%p loaded=%p class=%p invoke=%p",
             (void*)g_api.get_root_domain, (void*)g_api.thread_attach,
             (void*)g_api.assembly_name_new, (void*)g_api.assembly_loaded,
             (void*)g_api.class_from_name, (void*)g_api.runtime_invoke);

    if (g_api.get_root_domain != nullptr) {
        g_api.root_domain = g_api.get_root_domain();
    }

    g_api.ready = g_api.root_domain != nullptr && g_api.assembly_name_new != nullptr &&
                  g_api.assembly_loaded != nullptr && g_api.class_from_name != nullptr &&
                  g_api.runtime_invoke != nullptr;
}

}  // namespace

// Opcodes understood by the pump. Keep in sync with the managed Tide side.
enum TideOp : int {
    TideOp_UnityLog = 1,       // arg: { char message[512] }
    TideOp_InvokeStatic = 2,   // arg: { char assembly[128]; char ns[128]; char klass[128];
                               //        char method[128]; int32 argc (0 only for now) }
};

// Fixed-size request structs (plain data; CoreCLR marshals these by value).
struct UnityLogRequest {
    char message[512];
};

struct InvokeStaticRequest {
    char assembly[128];
    char ns[128];
    char klass[128];
    char method[128];
    int argc;  // only 0 supported for now (parameterless static)
};

// Logs the ToString() of a Mono exception object (best-effort; UTF-8, truncated).
void log_mono_exception(void* exc) {
    if (exc == nullptr) {
        return;
    }
    void* str = g_api.string_to_utf8(exc);
    if (str != nullptr) {
        log_tide("tide op: Mono exception: %s", static_cast<const char*>(str));
        g_api.mono_free(str);
    } else {
        log_tide("tide op: Mono exception (no message)");
    }
}

// Resolves an assembly by name (tries with and without .dll), returning the assembly ptr
// or null. Helper shared by ops.
void* find_assembly(const char* name) {
    // Try with and without .dll.
    char with_dll[160];
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

// The work function the pump runs for a queued request. `op` and the request buffer are
// passed via a small heap context allocated by the export below.
struct OpContext {
    int op;
    void* data;   // points to the request struct (owned by the caller until completion)
    int result;   // written by the op
};

int execute_op(void* arg) {
    auto* ctx = static_cast<OpContext*>(arg);
    ctx->result = -1;

    resolve_api();
    if (!g_api.ready) {
        log_tide("tide op: mono api not ready (root=%p asm=%p class=%p invoke=%p)",
                 (void*)g_api.root_domain, (void*)g_api.assembly_loaded,
                 (void*)g_api.class_from_name, (void*)g_api.runtime_invoke);
        return -1;
    }

    // This op runs on the game's MAIN thread (via the mono_runtime_invoke drain hook),
    // where Mono's GC is fully set up - no incremental-GC toggling needed.

    switch (ctx->op) {
        case TideOp_UnityLog: {
            auto* req = static_cast<UnityLogRequest*>(ctx->data);
            void* asm_ = find_assembly("UnityEngine.CoreModule");
            if (asm_ == nullptr) {
                log_tide("tide op: UnityEngine.CoreModule not loaded");
                ctx->result = -1;
                break;
            }
            void* image = g_api.assembly_get_image(asm_);
            void* debug_class = g_api.class_from_name(image, "UnityEngine", "Debug");
            if (debug_class == nullptr) {
                log_tide("tide op: UnityEngine.Debug class not found");
                ctx->result = -1;
                break;
            }
            void* log = g_api.class_get_method_from_name(debug_class, "Log", 1);
            if (log == nullptr) {
                log_tide("tide op: Debug.Log(object) method not found");
                ctx->result = -1;
                break;
            }
            void* str = g_api.string_new(g_api.root_domain, req->message);
            void* args[1] = {str};
            void* exc = nullptr;
            g_api.runtime_invoke(log, nullptr, args, &exc);
            if (exc != nullptr) {
                log_tide("tide op: Debug.Log threw a Mono exception");
                log_mono_exception(exc);
                ctx->result = -2;
                break;
            }
            ctx->result = 0;
            log_tide("tide op: Debug.Log executed OK");
            break;
        }
        case TideOp_InvokeStatic: {
            auto* req = static_cast<InvokeStaticRequest*>(ctx->data);
            if (req->argc != 0) {
                log_tide("tide op: InvokeStatic with args not yet supported (argc=%d)", req->argc);
                ctx->result = -1;
                break;
            }
            void* asm_ = find_assembly(req->assembly);
            if (asm_ == nullptr) {
                log_tide("tide op: assembly '%s' not loaded", req->assembly);
                ctx->result = -1;
                break;
            }
            void* image = g_api.assembly_get_image(asm_);
            void* klass = g_api.class_from_name(image, req->ns, req->klass);
            if (klass == nullptr) {
                log_tide("tide op: class '%s.%s' not found", req->ns, req->klass);
                ctx->result = -1;
                break;
            }
            void* method = g_api.class_get_method_from_name(klass, req->method, 0);
            if (method == nullptr) {
                log_tide("tide op: method '%s()' not found", req->method);
                ctx->result = -1;
                break;
            }
            void* exc = nullptr;
            g_api.runtime_invoke(method, nullptr, nullptr, &exc);
            if (exc != nullptr) {
                log_tide("tide op: '%s' threw a Mono exception", req->method);
                log_mono_exception(exc);
                ctx->result = -2;
                break;
            }
            ctx->result = 0;
            log_tide("tide op: InvokeStatic %s.%s.%s OK", req->assembly, req->klass, req->method);
            break;
        }
        default:
            log_tide("tide op: unknown op %d", ctx->op);
            ctx->result = -1;
            break;
    }

    return ctx->result;
}

}  // namespace nami::tide

// ---------------------------------------------------------------------------
// Exports called by CoreCLR (Tide managed). All Mono work happens on the pump.
// ---------------------------------------------------------------------------

extern "C" __declspec(dllexport) int nami_tide_unity_log(const char* message) {
    using namespace nami::tide;

    UnityLogRequest req{};
    strncpy_s(req.message, message, sizeof(req.message) - 1);

    OpContext ctx{};
    ctx.op = TideOp_UnityLog;
    ctx.data = &req;
    ctx.result = -1;

    // Execute on the game's MAIN thread via the mono_runtime_invoke drain hook.
    const bool ok = run_on_main_thread(execute_op, &ctx);
    return ok ? ctx.result : -3;
}

// Invokes a parameterless static method on a game class:
//   nami_tide_invoke_static(assembly, ns, class, method)
extern "C" __declspec(dllexport) int nami_tide_invoke_static(const char* assembly,
                                                             const char* ns,
                                                             const char* klass,
                                                             const char* method) {
    using namespace nami::tide;

    InvokeStaticRequest req{};
    strncpy_s(req.assembly, assembly, sizeof(req.assembly) - 1);
    strncpy_s(req.ns, ns, sizeof(req.ns) - 1);
    strncpy_s(req.klass, klass, sizeof(req.klass) - 1);
    strncpy_s(req.method, method, sizeof(req.method) - 1);
    req.argc = 0;

    OpContext ctx{};
    ctx.op = TideOp_InvokeStatic;
    ctx.data = &req;
    ctx.result = -1;

    const bool ok = run_on_main_thread(execute_op, &ctx);
    return ok ? ctx.result : -3;
}
