// nami-inex legacy bootstrap: boot BepInEx 5.x inside the game's Mono.
//
// BepInEx 5.4's preloader reads four DOORSTOP_* environment variables (found by
// inspecting BepInEx.Preloader.dll) instead of requiring Doorstop's winhttp proxy
// or doorstop_config.ini, so Nami bootstraps it directly. The entry contract matches
// doorstop_config.ini's own comment: `static void Doorstop.Entrypoint.Start()`.
//
// TIMING (the whole game here): the preloader patches the one-shot entrypoint
// (Application..cctor) into an already-loaded CoreModule to no effect, so Start()
// must run BEFORE first managed execution — exactly Doorstop timing. Primary path:
// a mono_jit_init detour runs Start synchronously on the game main thread right
// after the runtime comes up. Fallback path (detour missed, e.g. Mono was already
// initialized): Tide's mono_runtime_invoke drain runs Start, and a chainloader kick
// (Initialize+Start, both idempotent) fires once a scene is live — late Start can
// never hit the one-shot patch, so the kick replicates what it would have called.
// Kick ordering vs a naturally-fired entrypoint is safe: both ends are guarded.

#include "inex_bootstrap.h"

#include "tide_pump.h"

#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace nami::inex {
namespace {

// Global arm state (set once by arm() on the loader thread, read by game threads).
std::string g_root;
std::string g_log_path;
volatile LONG g_started = 0;  // 0 = preloader Start not yet run, 1 = ran

void ilog(const std::string& log_path, const char* msg) {
    FILE* log = nullptr;
    // Per-call open/append/close: the loader, watcher, and game threads all log.
    if (fopen_s(&log, log_path.c_str(), "a") != 0 || log == nullptr) {
        return;
    }
    std::fprintf(log, "[inex] %s\n", msg);
    std::fclose(log);
}

// Minimal Mono embedding surface, resolved at runtime so the loader links
// against nothing Mono-specific.
struct MonoApi {
    HMODULE mono = nullptr;
    void* (*image_open_from_data_with_name)(char*, unsigned, int, int*, int, const char*) = nullptr;
    void* (*assembly_load_from_full)(void*, const char*, int*, int) = nullptr;
    void* (*class_from_name)(void*, const char*, const char*) = nullptr;
    void* (*method_desc_new)(const char*, int) = nullptr;
    void* (*method_desc_search_in_class)(void*, void*) = nullptr;
    void (*method_desc_free)(void*) = nullptr;
    void* (*runtime_invoke)(void*, void*, void**, void**) = nullptr;
};

template <typename T>
bool resolve(HMODULE mono, T& out, const char* name, const std::string& log_path,
             std::string& missing) {
    // FARPROC -> void* -> function pointer: avoids -Wcast-function-type.
    out = reinterpret_cast<T>(reinterpret_cast<void*>(GetProcAddress(mono, name)));
    if (out == nullptr) {
        missing = name;
        ilog(log_path, ("missing mono export: " + missing).c_str());
        return false;
    }
    return true;
}

bool resolve_api(MonoApi& api, const std::string& log_path) {
    HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
    if (mono == nullptr) {
        mono = GetModuleHandleW(L"mono.dll");
    }
    if (mono == nullptr) {
        return false;
    }
    api.mono = mono;
    std::string missing;
    return resolve(mono, api.image_open_from_data_with_name,
                   "mono_image_open_from_data_with_name", log_path, missing) &&
           resolve(mono, api.assembly_load_from_full, "mono_assembly_load_from_full", log_path,
                   missing) &&
           resolve(mono, api.class_from_name, "mono_class_from_name", log_path, missing) &&
           resolve(mono, api.method_desc_new, "mono_method_desc_new", log_path, missing) &&
           resolve(mono, api.method_desc_search_in_class, "mono_method_desc_search_in_class",
                   log_path, missing) &&
           resolve(mono, api.method_desc_free, "mono_method_desc_free", log_path, missing) &&
           resolve(mono, api.runtime_invoke, "mono_runtime_invoke", log_path, missing);
}

// Opens a managed DLL from disk into a Mono image. Bytes stay alive in kept_alive
// for as long as the image may be used (caller keeps it until process exit).
void* open_image(MonoApi& api, const std::string& path, std::vector<char>& kept_alive) {
    FILE* f = nullptr;
    if (fopen_s(&f, path.c_str(), "rb") != 0 || f == nullptr) {
        return nullptr;
    }
    std::fseek(f, 0, SEEK_END);
    const long size = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    kept_alive.assign(static_cast<size_t>(size > 0 ? size : 0), 0);
    const size_t got =
        kept_alive.empty() ? 0 : std::fread(kept_alive.data(), 1, kept_alive.size(), f);
    std::fclose(f);
    if (got != kept_alive.size() || kept_alive.empty()) {
        return nullptr;
    }
    int status = 0;
    void* image = api.image_open_from_data_with_name(
        kept_alive.data(), static_cast<unsigned>(kept_alive.size()), 1, &status, 0,
        path.c_str());
    return (image != nullptr && status == 0) ? image : nullptr;
}

// Invokes a static parameterless managed method by (image, namespace, class, method).
// Returns 0 clean / 1 missing / 2 threw.
int invoke_static(MonoApi& api, void* image, const char* klass_ns, const char* klass,
                  const char* desc_text, const std::string& log_path, const char* label) {
    void* target = api.class_from_name(image, klass_ns, klass);
    if (target == nullptr) {
        ilog(log_path, (std::string("class not found: ") + label).c_str());
        return 1;
    }
    void* desc = api.method_desc_new(desc_text, 1);
    void* method =
        desc == nullptr ? nullptr : api.method_desc_search_in_class(desc, target);
    if (desc != nullptr) {
        api.method_desc_free(desc);
    }
    if (method == nullptr) {
        ilog(log_path, (std::string("method not found: ") + label).c_str());
        return 1;
    }
    void* exc = nullptr;
    api.runtime_invoke(method, nullptr, nullptr, &exc);
    if (exc != nullptr) {
        ilog(log_path, (std::string("threw a managed exception: ") + label).c_str());
        return 2;
    }
    ilog(log_path, (std::string("invoked cleanly: ") + label).c_str());
    return 0;
}

// Runs Doorstop.Entrypoint.Start(). MUST run on the game main thread, ideally before
// first managed execution (jit-init detour); the drain fallback covers a miss.
// Exactly-once across all triggers (CAS on g_started).
int run_preloader_start() {
    if (InterlockedCompareExchange(&g_started, 1, 0) != 0) {
        return 0;
    }
    const std::string preloader = g_root + "\\inex\\BepInEx\\core\\BepInEx.Preloader.dll";
    MonoApi api;
    if (!resolve_api(api, g_log_path)) {
        ilog(g_log_path, "mono exports unavailable for preloader start");
        return 1;
    }
    static std::vector<char> bytes;  // process-lifetime: image references it
    void* image = open_image(api, preloader, bytes);
    if (image == nullptr) {
        ilog(g_log_path, "cannot open preloader image");
        return 1;
    }
    int status = 0;
    void* assembly = api.assembly_load_from_full(image, preloader.c_str(), &status, 0);
    if (assembly == nullptr || status != 0) {
        ilog(g_log_path, "mono_assembly_load failed (preloader)");
        return 1;
    }
    return invoke_static(api, image, "Doorstop", "Entrypoint", "Doorstop.Entrypoint:Start()",
                         g_log_path, "Doorstop.Entrypoint.Start");
}

// Late chainloader kick for a Start() that ran too late to hit the one-shot
// entrypoint patch. Replicates exactly what the patched entrypoint calls:
//   Chainloader.Initialize(null, false, null); Chainloader.Start();
// Both are idempotent (_initialized/_loaded guards). MUST run on the game main
// thread with a live scene (Initialize creates GameObjects).
int run_chainloader_kick() {
    const std::string bepinex = g_root + "\\inex\\BepInEx\\core\\BepInEx.dll";
    MonoApi api;
    if (!resolve_api(api, g_log_path)) {
        return 1;
    }
    static std::vector<char> bytes;  // process-lifetime
    void* image = open_image(api, bepinex, bytes);
    if (image == nullptr) {
        ilog(g_log_path, "cannot open BepInEx.dll image");
        return 1;
    }
    int status = 0;
    void* assembly = api.assembly_load_from_full(image, bepinex.c_str(), &status, 0);
    if (assembly == nullptr || status != 0) {
        ilog(g_log_path, "mono_assembly_load failed (BepInEx.dll)");
        return 1;
    }
    void* klass = api.class_from_name(image, "BepInEx.Bootstrap", "Chainloader");
    if (klass == nullptr) {
        ilog(g_log_path, "BepInEx.Bootstrap.Chainloader class not found");
        return 1;
    }
    void* init_desc = api.method_desc_new("BepInEx.Bootstrap.Chainloader:Initialize", 1);
    void* init = init_desc == nullptr
                     ? nullptr
                     : api.method_desc_search_in_class(init_desc, klass);
    if (init_desc != nullptr) {
        api.method_desc_free(init_desc);
    }
    if (init == nullptr) {
        ilog(g_log_path, "Chainloader.Initialize method not found");
        return 1;
    }
    // Initialize(string gameExePath = null, bool startConsole = false,
    //            ICollection<LogEventArgs> preloaderLogEvents = null).
    unsigned char no_console = 0;
    void* init_args[3]{nullptr, &no_console, nullptr};
    void* exc = nullptr;
    api.runtime_invoke(init, nullptr, init_args, &exc);
    if (exc != nullptr) {
        ilog(g_log_path, "Chainloader.Initialize threw a managed exception");
        return 2;
    }
    ilog(g_log_path, "Chainloader.Initialize returned");
    void* start_desc = api.method_desc_new("BepInEx.Bootstrap.Chainloader:Start", 1);
    void* start = start_desc == nullptr
                      ? nullptr
                      : api.method_desc_search_in_class(start_desc, klass);
    if (start_desc != nullptr) {
        api.method_desc_free(start_desc);
    }
    if (start == nullptr) {
        ilog(g_log_path, "Chainloader.Start method not found");
        return 1;
    }
    exc = nullptr;
    api.runtime_invoke(start, nullptr, nullptr, &exc);
    if (exc != nullptr) {
        ilog(g_log_path, "Chainloader.Start threw a managed exception");
        return 2;
    }
    ilog(g_log_path, "Chainloader.Start invoked cleanly");
    return 0;
}

struct DomainCtx {
    MonoApi* api = nullptr;
    void* domain = nullptr;
};

int drain_sample_domain(void* arg) {
    auto* ctx = static_cast<DomainCtx*>(arg);
    using domain_get_fn = void* (*)();
    auto get = reinterpret_cast<domain_get_fn>(
        reinterpret_cast<void*>(GetProcAddress(ctx->api->mono, "mono_domain_get")));
    ctx->domain = get == nullptr ? nullptr : get();
    return 0;
}

// The script domain is reloaded once during Unity boot ("Begin MonoManager
// ReloadAssembly"); Mono handles from before the reload dangle after it, and
// invoking through them kills the process with no managed exception. A visible
// window is NOT sufficient (first frames render pre-reload). So: sample
// mono_domain_get() on the main thread until the pointer is stable, then kick.
bool wait_for_domain_stable(MonoApi& api) {
    DomainCtx ctx;
    ctx.api = &api;
    void* prev = nullptr;
    int stable = 0;
    for (int i = 0; i < 120; i++) {
        ctx.domain = nullptr;
        tide::run_on_main_thread(drain_sample_domain, &ctx, 5000);
        if (ctx.domain != nullptr && ctx.domain == prev) {
            if (++stable >= 5) {
                return true;
            }
        } else {
            // New (or first) domain identity: any Start from a previous epoch died
            // with its domain — allow exactly one fresh Start for this epoch.
            if (ctx.domain != prev) {
                InterlockedExchange(&g_started, 0);
            }
            stable = 0;
            prev = ctx.domain;
        }
        Sleep(1000);
    }
    return false;
}

// Full late sequence, run ATOMICALLY in one drain call once the domain is stable:
// preloader Start (idempotent-ish: safe to redo post-reload since the old domain
// is gone; skipped if the jit path already won), then the chainloader kick
// (Initialize+Start carry _initialized/_loaded guards).
int drain_late_sequence(void* /*arg*/) {
    run_preloader_start();
    if (run_chainloader_kick() != 0) {
        return 1;
    }
    return 0;
}

// --- jit_init detour: Doorstop timing (before first managed execution) ---

using jit_init_version_fn = void* (*)(const char*, const char*);
using jit_init_fn = void* (*)(const char*);
jit_init_version_fn g_orig_jit_init_version = nullptr;
jit_init_fn g_orig_jit_init = nullptr;

void* inex_jit_init_version_detour(const char* domain_name, const char* version) {
    void* domain = g_orig_jit_init_version(domain_name, version);
    // On the game main thread, runtime fully up, zero managed code run yet.
    const int rc = run_preloader_start();
    ilog(g_log_path, (std::string("early preloader start rc=") + std::to_string(rc)).c_str());
    return domain;
}

void* inex_jit_init_detour(const char* domain_name) {
    void* domain = g_orig_jit_init(domain_name);
    const int rc = run_preloader_start();
    ilog(g_log_path, (std::string("early preloader start rc=") + std::to_string(rc)).c_str());
    return domain;
}

void install_jit_hook() {
    // Diagnostic: dump the target prologue bytes so a refusal is explainable offline.
    HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
    if (mono == nullptr) {
        mono = GetModuleHandleW(L"mono.dll");
    }
    if (mono != nullptr) {
        if (void* fn = reinterpret_cast<void*>(GetProcAddress(mono, "mono_jit_init_version"))) {
            unsigned char head[16]{};
            memcpy(head, fn, sizeof(head));
            char hex[64]{};
            for (int i = 0; i < 16; i++) {
                std::snprintf(hex + i * 3, 4, "%02X ", head[i]);
            }
            ilog(g_log_path, (std::string("jit_init_version prologue: ") + hex).c_str());
        } else {
            ilog(g_log_path, "mono_jit_init_version export missing");
        }
    }
    void* tramp = tide::install_native_detour(L"mono-2.0-bdwgc.dll", "mono_jit_init_version",
                                              reinterpret_cast<void*>(&inex_jit_init_version_detour));
    if (tramp != nullptr) {
        g_orig_jit_init_version =
            reinterpret_cast<jit_init_version_fn>(tramp);
        ilog(g_log_path, "jit hook installed (mono_jit_init_version)");
        return;
    }
    tramp = tide::install_native_detour(L"mono-2.0-bdwgc.dll", "mono_jit_init",
                                        reinterpret_cast<void*>(&inex_jit_init_detour));
    if (tramp != nullptr) {
        g_orig_jit_init = reinterpret_cast<jit_init_fn>(tramp);
        ilog(g_log_path, "jit hook installed (mono_jit_init)");
        return;
    }
    // mono.dll fallback (some Unity builds ship the runtime under this name).
    tramp = tide::install_native_detour(L"mono.dll", "mono_jit_init_version",
                                        reinterpret_cast<void*>(&inex_jit_init_version_detour));
    if (tramp != nullptr) {
        g_orig_jit_init_version =
            reinterpret_cast<jit_init_version_fn>(tramp);
        ilog(g_log_path, "jit hook installed (mono.dll/mono_jit_init_version)");
        return;
    }
    ilog(g_log_path, "jit hook install failed (exports already running?); drain fallback covers");
}

struct WinCtx {
    DWORD pid = 0;
    bool found = false;
};

BOOL CALLBACK enum_visible_window(HWND hwnd, LPARAM param) {
    auto* ctx = reinterpret_cast<WinCtx*>(param);
    DWORD wpid = 0;
    GetWindowThreadProcessId(hwnd, &wpid);
    if (wpid == ctx->pid && IsWindowVisible(hwnd) != FALSE) {
        ctx->found = true;
        return FALSE;
    }
    return TRUE;
}

// A visible game window means frames are rendering, i.e. a scene is live and
// GameObject creation is safe. Game-agnostic; no Mono calls needed to observe it.
bool wait_for_game_window(int timeout_ms) {
    WinCtx ctx;
    ctx.pid = GetCurrentProcessId();
    const int step = 500;
    for (int waited = 0; waited < timeout_ms; waited += step) {
        ctx.found = false;
        EnumWindows(enum_visible_window, reinterpret_cast<LPARAM>(&ctx));
        if (ctx.found) {
            return true;
        }
        Sleep(step);
    }
    return false;
}

// Background fallback/watchdog: wait for a live scene, then for script-domain
// stability, then run the FULL late sequence (Start + kick) atomically in one
// drain call, outside any nested invoke frame. Runs on its own thread; the loader
// thread never blocks on legacy boot.
DWORD WINAPI watcher_thread(LPVOID /*param*/) {
    if (wait_for_game_window(180000)) {
        ilog(g_log_path, "game window visible; waiting for domain stability");
    } else {
        ilog(g_log_path, "no game window in 180s; proceeding anyway");
    }
    MonoApi api;
    if (!resolve_api(api, g_log_path)) {
        ilog(g_log_path, "mono exports unavailable for stability wait; kicking anyway");
    } else if (wait_for_domain_stable(api)) {
        ilog(g_log_path, "domain stable; running late sequence");
    } else {
        ilog(g_log_path, "domain never stabilized in 120s; kicking anyway");
    }
    // Post-invoke: run OUTSIDE any nested mono_runtime_invoke frame. The sequence loads
    // plugin assemblies and runs their Awake, which may call frame-sensitive Unity
    // APIs (scene iteration aborts from re-entry).
    tide::run_on_main_thread(drain_late_sequence, nullptr, 120000, tide::RequestFlag_PostInvoke);
    return 0;
}

void set_doorstop_env(const std::string& root) {
    char exe[MAX_PATH]{};
    GetModuleFileNameA(nullptr, exe, MAX_PATH);
    const std::string exe_path(exe);
    const size_t slash = exe_path.find_last_of("\\/");
    const std::string dir = slash == std::string::npos ? "." : exe_path.substr(0, slash);
    std::string base = slash == std::string::npos ? exe_path : exe_path.substr(slash + 1);
    const size_t dot = base.rfind('.');
    if (dot != std::string::npos) {
        base.erase(dot);
    }
    const std::string managed = dir + "\\" + base + "_Data\\Managed";
    const std::string core_dir = root + "\\inex\\BepInEx\\core";
    SetEnvironmentVariableA("DOORSTOP_PROCESS_PATH", exe_path.c_str());
    SetEnvironmentVariableA("DOORSTOP_MANAGED_FOLDER_DIR", managed.c_str());
    SetEnvironmentVariableA("DOORSTOP_INVOKE_DLL_PATH",
                            (root + "\\inex\\BepInEx\\core\\BepInEx.Preloader.dll").c_str());
    SetEnvironmentVariableA("DOORSTOP_DLL_SEARCH_DIRS", core_dir.c_str());
    ilog(g_log_path, ("managed dir: " + managed).c_str());
}

}  // namespace

int arm(const std::string& nami_root_utf8) {
    const std::string preloader =
        nami_root_utf8 + "\\inex\\BepInEx\\core\\BepInEx.Preloader.dll";
    if (GetFileAttributesA(preloader.c_str()) == INVALID_FILE_ATTRIBUTES) {
        return 0;  // No legacy payload staged: pure Nami install, stay silent.
    }
    const std::string sentinel = nami_root_utf8 + "\\inex\\enabled";
    if (GetFileAttributesA(sentinel.c_str()) == INVALID_FILE_ATTRIBUTES) {
        return 1;  // Payload staged but not enabled: stay out of the game's way.
    }
    g_root = nami_root_utf8;
    g_log_path = nami_root_utf8 + "\\native\\nami-inex.log";
    set_doorstop_env(nami_root_utf8);
    install_jit_hook();
    HANDLE thread = CreateThread(nullptr, 0, watcher_thread, nullptr, 0, nullptr);
    if (thread == nullptr) {
        ilog(g_log_path, "watcher thread spawn failed; jit path only");
    } else {
        CloseHandle(thread);
    }
    return 2;
}

}  // namespace nami::inex
