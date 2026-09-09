// IL2CPP method patching (see tide_il2cpp_patch.h).

#include "tide_il2cpp_patch.h"

#include "native_stub.h"
#include "tide_il2cpp.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <new>
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
    void* (*class_get_method_from_name)(void*, const char*, int) = nullptr;
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
    g_api.class_get_method_from_name = reinterpret_cast<decltype(g_api.class_get_method_from_name)>(
        load("il2cpp_class_get_method_from_name"));

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

}  // namespace

}  // namespace nami::il2cpp::patch

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
    nami::stub::unhook_native(rec);
    log_tide("il2cpp patch: unhooked id=%llu", static_cast<unsigned long long>(hook_id));
    return 0;
}