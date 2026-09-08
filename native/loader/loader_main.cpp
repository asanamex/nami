#include "loader.h"

#include "../core/bootguard.h"
#include "../core/runtime_host.h"
#include "inex_bootstrap.h"
#include "tide_il2cpp.h"

#include <windows.h>

#include <cstdio>

namespace nami {

bool wait_for_runtime(int timeout_ms) {
    const auto start = GetTickCount64();
    while (GetTickCount64() - start < static_cast<ULONGLONG>(timeout_ms)) {
        if (GetModuleHandleW(L"mono-2.0-bdwgc.dll") != nullptr ||
            GetModuleHandleW(L"mono.dll") != nullptr ||
            GetModuleHandleW(L"GameAssembly.dll") != nullptr) {
            return true;
        }
        // Tight poll: mono_jit_init follows the module load by milliseconds, and the
        // inex lane wants its detour installed before that call happens.
        Sleep(10);
    }
    return false;
}

void loader_main(const wchar_t* nami_root) {
    // Write the marker next to the loader DLL (not CWD-dependent).
    wchar_t marker_path[MAX_PATH]{};
    const HMODULE self = GetModuleHandleW(L"nami_loader.dll");
    if (self != nullptr) {
        GetModuleFileNameW(self, marker_path, MAX_PATH);
        wchar_t* slash = wcsrchr(marker_path, L'\\');
        if (slash != nullptr) {
            const size_t remaining = MAX_PATH - static_cast<size_t>(slash + 1 - marker_path);
            wcscpy_s(slash + 1, remaining, L"nami-loader.log");
        }
    }

    FILE* marker = nullptr;
    if (marker_path[0] != L'\0' && _wfopen_s(&marker, marker_path, L"a") == 0 && marker != nullptr) {
        std::fwprintf(marker, L"[loader] injected into pid=%lu at %llu ms\n",
                      GetCurrentProcessId(), GetTickCount64());
        std::fflush(marker);
    }

    // Convert the wide root path to UTF-8 for the narrow RuntimeHost API.
    // (Needed before the early arm() below.)
    std::string root;
    if (nami_root != nullptr) {
        const int len = WideCharToMultiByte(CP_UTF8, 0, nami_root, -1, nullptr, 0, nullptr, nullptr);
        if (len > 0) {
            root.resize(static_cast<size_t>(len - 1));
            WideCharToMultiByte(CP_UTF8, 0, nami_root, -1, root.data(), len, nullptr, nullptr);
        }
    }

    // Boot-guard (crash containment + safe mode): install the crash handler FIRST so
    // every stage below is covered. Faults on this (Nami-owned) thread are contained —
    // hook-ready is signaled and the thread dies, so the game boots unmodded instead
    // of the whole process faulting. The handler also logs crashes to nami-crash.log
    // and marks the next boot safe while boot-pending exists.
    bootguard::InstallCrashHandler(root, &inex::signal_hook_ready);
    bootguard::MarkThreadOwned(true);

    if (bootguard::SafeModeEnabled(root)) {
        // A previous boot crashed mid-boot. Consume one recovery boot: skip EVERY
        // native stage (inex arm, runtime wait, CoreCLR hosting) so the game boots
        // clean and unmodded. The marker auto-clears after N clean boots (or delete
        // <root>/safe-mode manually for an immediate full boot).
        const int remaining = bootguard::ConsumeSafeModeBoot(root);
        if (marker) {
            std::fwprintf(marker,
                          L"[loader] SAFE MODE (boot-guard): skipping inex + CoreCLR; "
                          L"game runs unmodded (%d clean boot(s) remaining)\n",
                          remaining - 1);
            std::fflush(marker);
        }
        inex::signal_hook_ready();
        bootguard::MarkBootComplete(root);
        if (marker) {
            std::fclose(marker);
        }
        return;
    }

    bootguard::MarkBootStart(root, bootguard::Stage_InexArm);

    // Legacy lane (nami-inex) FIRST, while the game main thread is still suspended
    // (the injector holds it until we signal hook-ready): the mono_jit_init detour
    // only wins its race when installed before the main thread runs. Both modules
    // are import-loaded on stock Unity builds, so presence is visible immediately;
    // a dynamically-loaded Mono (never observed) simply misses the detour and the
    // drain/watcher fallback covers it, exactly as before. IL2CPP titles skip this
    // (BepInEx 6 needs its own CoreCLR lane — later). arm() signals hook-ready in
    // every path; the skip branch below must too, or the injector stalls 30s.
    int inex = -1;
    if (!root.empty() && !il2cpp::detect_il2cpp()) {
        inex = inex::arm(root);
        if (marker) {
            std::fwprintf(marker, L"[loader] legacy inex: %ls\n",
                           inex == 2 ? L"armed" : inex == 1 ? L"payload staged, not enabled"
                                                            : L"absent");
            std::fflush(marker);
        }
    } else {
        inex::signal_hook_ready();
    }

    bootguard::MarkStage(root, bootguard::Stage_WaitRuntime);

    // Wait for the game's managed runtime to load: Mono DLLs on Mono titles, GameAssembly.dll
    // on IL2CPP titles. Import-loaded runtimes are already present (see above); this
    // wait only matters for exotic dynamic loads. The loader is injected at process start.
    const bool runtime_seen = wait_for_runtime(60000);
    const bool is_il2cpp = runtime_seen && il2cpp::detect_il2cpp();
    if (!runtime_seen) {
        if (marker) {
            std::fwprintf(marker, L"[loader] neither mono nor GameAssembly appeared; aborting\n");
            std::fflush(marker);
            std::fclose(marker);
        }
        return;
    }

    if (marker) {
        std::fwprintf(marker, L"[loader] runtime detected (%ls); hosting CoreCLR from %ls\n",
                      is_il2cpp ? L"IL2CPP (GameAssembly.dll)" : L"Mono", nami_root);
        std::fflush(marker);
    }

    bootguard::MarkStage(root, bootguard::Stage_HostCoreClr);

    RuntimeHost host;
    if (root.empty() || host.initialize(root) != Status::Ok) {
        if (marker) {
            std::fwprintf(marker, L"[loader] RuntimeHost.initialize failed (root=%ls)\n", nami_root);
            std::fflush(marker);
        }
    } else {
        // The managed runtime (Nami.Runtime.Boot.Run) clears boot-pending once its
        // update loop is ticking; until then any fault here marks the next boot safe.
        bootguard::MarkStage(root, bootguard::Stage_ManagedBoot);
        host.run_boot();
    }

    if (marker) {
        std::fclose(marker);
    }
}

}  // namespace nami