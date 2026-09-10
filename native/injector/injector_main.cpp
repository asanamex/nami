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
    std::fwprintf(stdout, L"[injector] launching suspended: %ls\n", game_exe);
    if (!CreateProcessW(game_exe, nullptr, nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr,
                        game_dir, &si, &pi)) {
        std::fwprintf(stderr, L"[injector] CreateProcessW(%ls) failed: %lu\n", game_exe,
                      GetLastError());
        return Status::Error;
    }
    std::fwprintf(stdout, L"[injector] game process started, pid=%lu\n", pi.dwProcessId);

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
    std::fwprintf(stdout, L"[injector] writing loader path (%zu bytes) into pid=%lu\n", path_bytes,
                  pi.dwProcessId);
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
    // The game main thread stays suspended until the loader signals hook-ready:
    // Unity otherwise reaches mono_jit_init before our patch lands (3MB DLL load +
    // thread scheduling vs an already-running main thread) and the jit detour -
    // the whole early-boot path - never fires. The wait is bounded; the game
    // always resumes.
    // Named to match the loader's signal (inex::signal_hook_ready opens
    // Local\NamiHookReady-<pid> with the *game* pid). An unnamed event here
    // can never be opened from the game process — every boot burned the full
    // 30 s timeout and resumed anyway.
    wchar_t ready_name[64]{};
    swprintf_s(ready_name, L"Local\\NamiHookReady-%lu", pi.dwProcessId);
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, ready_name);
    std::fwprintf(stdout, L"[injector] starting remote thread (LoadLibraryW) in pid=%lu\n",
                  pi.dwProcessId);
    const HANDLE remote_thread = CreateRemoteThread(pi.hProcess, nullptr, 0, load_library_w,
                                                    remote_path, 0, nullptr);
    if (remote_thread == nullptr) {
        std::fwprintf(stderr, L"[injector] CreateRemoteThread failed: %lu\n", GetLastError());
    } else {
        std::fwprintf(stdout, L"[injector] waiting for loader ready signal (up to 30s)...\n");
        bool hooked = false;
        if (ready != nullptr &&
            WaitForSingleObject(ready, 30000) == WAIT_OBJECT_0) {
            hooked = true;
            std::fwprintf(stdout, L"[injector] loader ready\n");
        } else {
            std::fwprintf(stderr, L"[injector] hook-ready wait timed out; resuming anyway\n");
        }
        CloseHandle(remote_thread);
        if (hooked) {
            // LoadLibrary has returned (boot runs after DllMain), so the path is spent.
            VirtualFreeEx(pi.hProcess, remote_path, 0, MEM_RELEASE);
        }
    }

    // Let the game run now that early interception is in place (or was skipped).
    std::fwprintf(stdout, L"[injector] resuming game thread\n");
    ResumeThread(pi.hThread);

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    if (remote_thread == nullptr) {
        return Status::Error;
    }

    std::fwprintf(stdout, L"[injector] injected loader into pid=%lu\n", pi.dwProcessId);
    return Status::Ok;
}

}  // namespace nami
