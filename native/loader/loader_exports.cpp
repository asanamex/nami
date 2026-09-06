#include "loader.h"

#include <windows.h>

#include <filesystem>
#include <string>

namespace fs = std::filesystem;

namespace {

// Derives the nami root from this DLL's own location: the loader lives at
// <nami root>/native/nami_loader.dll, so the root is two levels up.
std::wstring derive_nami_root() {
    wchar_t self[MAX_PATH]{};
    const DWORD len = GetModuleFileNameW(GetModuleHandleW(L"nami_loader.dll"), self, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) {
        return {};
    }

    fs::path p(self);
    return p.parent_path().parent_path().wstring();
}

DWORD WINAPI boot_thread_proc(LPVOID) {
    const std::wstring root = derive_nami_root();
    if (!root.empty()) {
        nami::loader_main(root.c_str());
    }
    return 0;
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE hmod, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        // Classic doorstop pattern: CreateThread from DllMain is acceptable when the
        // thread does not synchronize with others and the DLL is never unloaded.
        // The thread starts only after DllMain returns (loader lock released).
        DisableThreadLibraryCalls(hmod);
        const HANDLE h = CreateThread(nullptr, 0, boot_thread_proc, nullptr, 0, nullptr);
        if (h != nullptr) {
            CloseHandle(h);
        }
    }
    return TRUE;
}
