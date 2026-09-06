#include "loader.h"

#include "../core/runtime_host.h"

#include <windows.h>

#include <cstdio>

namespace nami {

bool wait_for_mono(int timeout_ms) {
    const auto start = GetTickCount64();
    while (GetTickCount64() - start < static_cast<ULONGLONG>(timeout_ms)) {
        if (GetModuleHandleW(L"mono-2.0-bdwgc.dll") != nullptr ||
            GetModuleHandleW(L"mono.dll") != nullptr) {
            return true;
        }
        Sleep(100);
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

    if (!wait_for_mono()) {
        if (marker) {
            std::fwprintf(marker, L"[loader] mono never appeared; aborting\n");
            std::fflush(marker);
            std::fclose(marker);
        }
        return;
    }

    if (marker) {
        std::fwprintf(marker, L"[loader] mono detected; hosting CoreCLR from %ls\n", nami_root);
        std::fflush(marker);
    }

    // Convert the wide root path to UTF-8 for the narrow RuntimeHost API.
    std::string root;
    if (nami_root != nullptr) {
        const int len = WideCharToMultiByte(CP_UTF8, 0, nami_root, -1, nullptr, 0, nullptr, nullptr);
        if (len > 0) {
            root.resize(static_cast<size_t>(len - 1));
            WideCharToMultiByte(CP_UTF8, 0, nami_root, -1, root.data(), len, nullptr, nullptr);
        }
    }

    RuntimeHost host;
    if (root.empty() || host.initialize(root) != Status::Ok) {
        if (marker) {
            std::fwprintf(marker, L"[loader] RuntimeHost.initialize failed (root=%ls)\n", nami_root);
            std::fflush(marker);
        }
    } else {
        host.run_boot();
    }

    if (marker) {
        std::fclose(marker);
    }
}

}  // namespace nami
