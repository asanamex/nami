#include "injector.h"

#include <windows.h>

#include <cstdio>

namespace nami {

namespace {

void* resolve_kernel32_proc(const char* name) {
    const HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    return reinterpret_cast<void*>(GetProcAddress(k32, name));
}

}  // namespace

Status inject_into_game(const wchar_t* game_exe, const wchar_t* loader_dll_path,
                        const wchar_t* /*nami_root*/) {
    // --- 1. Launch the game suspended in its own directory. ---
    wchar_t game_dir[MAX_PATH]{};
    wcsncpy_s(game_dir, MAX_PATH, game_exe, wcslen(game_exe));
    wchar_t* slash = wcsrchr(game_dir, L'\\');
    if (slash != nullptr) {
        *slash = L'\0';
    }

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(game_exe, nullptr, nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr,
                        game_dir, &si, &pi)) {
        std::fwprintf(stderr, L"[injector] CreateProcessW(%s) failed: %lu\n", game_exe,
                      GetLastError());
        return Status::Error;
    }

    // --- 2. Classic LoadLibraryW injection (no code stub). ---
    // kernel32 (and thus LoadLibraryW) is loaded at the SAME address in every process on
    // modern Windows (uniform ASLR per boot), so we can pass its address directly to
    // CreateRemoteThread. The only payload is the DLL path string.
    const auto load_library_w = reinterpret_cast<LPTHREAD_START_ROUTINE>(
        resolve_kernel32_proc("LoadLibraryW"));
    if (load_library_w == nullptr) {
        TerminateProcess(pi.hProcess, 1);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return Status::Error;
    }

    const size_t path_bytes = (wcslen(loader_dll_path) + 1) * sizeof(wchar_t);
    void* remote_path = VirtualAllocEx(pi.hProcess, nullptr, path_bytes, MEM_COMMIT | MEM_RESERVE,
                                       PAGE_READWRITE);
    if (remote_path == nullptr) {
        TerminateProcess(pi.hProcess, 1);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return Status::Error;
    }

    SIZE_T written = 0;
    if (!WriteProcessMemory(pi.hProcess, remote_path, loader_dll_path, path_bytes, &written)) {
        VirtualFreeEx(pi.hProcess, remote_path, 0, MEM_RELEASE);
        TerminateProcess(pi.hProcess, 1);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return Status::Error;
    }

    // --- 3. Remote thread runs LoadLibraryW(remote_path); DllMain spawns the boot thread. ---
    const HANDLE remote_thread = CreateRemoteThread(pi.hProcess, nullptr, 0, load_library_w,
                                                    remote_path, 0, nullptr);

    // Let the game run now that the loader is in flight.
    ResumeThread(pi.hThread);

    CloseHandle(remote_thread);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    if (remote_thread == nullptr) {
        std::fwprintf(stderr, L"[injector] CreateRemoteThread failed: %lu\n", GetLastError());
        return Status::Error;
    }

    std::fwprintf(stdout, L"[injector] injected loader into pid=%lu\n", pi.dwProcessId);
    return Status::Ok;
}

}  // namespace nami
