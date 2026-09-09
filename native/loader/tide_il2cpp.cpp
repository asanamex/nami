// ---------------------------------------------------------------------------
// Tide IL2CPP executor: runs typed ops on the game's MAIN thread for IL2CPP titles.
//
// Empirical constraints (verified on Unity 6000.0 and 2020.3 IL2CPP titles):
//   - NO il2cpp export fires per-frame (runtime_invoke/class_init/object_new/... are all
//     quiet in a live game) - a Mono-style runtime_invoke drain starves.
//   - NO VM API is safe from a worker thread, even attached (domain_assembly_open
//     derefs main-thread TLS and AVs).
//   - NO VM API is safe inside a runtime_invoke DETOUR frame either (class_from_name
//     faults at [image+0x30] derefs while any detoured invoke is on the stack).
//   - The safe context is the game's MAIN THREAD inside its WINDOW PROCEDURE (no
//     runtime_invoke on the stack): full class resolution + runtime_invoke work there.
//
// Design: ops are queued; the game's main window is subclassed with a Nami window proc;
// a posted message wakes the main thread's message pump, which drains the queue inside
// the window proc - verified working (Debug.Log invoked from the drain, game stable).
// ---------------------------------------------------------------------------

#include "tide_il2cpp.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <new>

namespace nami::il2cpp {

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

struct Request {
    int (*fn)(void*);
    void* arg;
    HANDLE done;
    Request* next;
};

CRITICAL_SECTION g_lock{};
bool g_lock_init = false;
Request* g_head = nullptr;
Request* g_tail = nullptr;
volatile LONG g_pending = 0;
volatile LONG g_running = 0;  // set while the drain runs on the main thread

// Main-window subclass state.
HWND g_hwnd = nullptr;
WNDPROC g_original_proc = nullptr;
const UINT WM_NAMI_DRAIN = WM_APP + 0x400;
bool g_installed = false;
SRWLOCK g_install_lock = SRWLOCK_INIT;

bool g_is_il2cpp = false;

// The drain itself: runs queued ops on the CALLING (main) thread.
void drain_queue() {
    if (InterlockedCompareExchange(&g_pending, 0, 0) == 0) {
        return;
    }
    InterlockedExchange(&g_running, 1);
    for (;;) {
        EnterCriticalSection(&g_lock);
        Request* req = g_head;
        if (req != nullptr) {
            g_head = req->next;
            if (g_head == nullptr) {
                g_tail = nullptr;
            }
        }
        LeaveCriticalSection(&g_lock);
        if (req == nullptr) {
            break;
        }
        req->fn(req->arg);
        if (req->done != nullptr) {
            SetEvent(req->done);
        }
        delete req;
    }
    EnterCriticalSection(&g_lock);
    if (g_head == nullptr) {
        InterlockedExchange(&g_pending, 0);
    }
    LeaveCriticalSection(&g_lock);
    InterlockedExchange(&g_running, 0);
}

// The Nami window proc: drains queued ops inside the game's main-thread message pump.
LRESULT CALLBACK nami_window_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WM_NAMI_DRAIN) {
        drain_queue();
        return 0;
    }
    return CallWindowProcW(g_original_proc, hwnd, msg, wp, lp);
}

BOOL CALLBACK find_main_window(HWND hwnd, LPARAM lp) {
    auto* pid = reinterpret_cast<DWORD*>(lp);
    DWORD wpid = 0;
    GetWindowThreadProcessId(hwnd, &wpid);
    if (wpid == *pid && IsWindowVisible(hwnd)) {
        g_hwnd = hwnd;
        return FALSE;
    }
    return TRUE;
}

}  // namespace

bool detect_il2cpp() {
    if (g_is_il2cpp) {
        return true;
    }
    g_is_il2cpp = GetModuleHandleW(L"GameAssembly.dll") != nullptr;
    return g_is_il2cpp;
}

bool install_il2cpp_executor() {
    if (!detect_il2cpp()) {
        return false;
    }
    return install_window_executor();
}

// Subclasses the game's main window so queued work runs on the main thread inside
// the window procedure (frame boundary - no runtime_invoke on the stack). Pure Win32;
// shared by the IL2CPP backend AND Mono scene-iteration ops (FindObject), which abort
// inside any nested invoke frame. Idempotent + thread-safe.
bool install_window_executor() {
    AcquireSRWLockExclusive(&g_install_lock);
    if (g_installed) {
        ReleaseSRWLockExclusive(&g_install_lock);
        return true;
    }

    if (!g_lock_init) {
        InitializeCriticalSection(&g_lock);
        g_lock_init = true;
    }

    const DWORD pid = GetCurrentProcessId();
    EnumWindows(find_main_window, reinterpret_cast<LPARAM>(&pid));
    if (g_hwnd == nullptr) {
        log_tide("il2cpp: no main window found; executor unavailable");
        ReleaseSRWLockExclusive(&g_install_lock);
        return false;
    }

    g_original_proc = reinterpret_cast<WNDPROC>(
        reinterpret_cast<LONG_PTR>(GetWindowLongPtrW(g_hwnd, GWLP_WNDPROC)));
    if (g_original_proc == nullptr) {
        ReleaseSRWLockExclusive(&g_install_lock);
        return false;
    }
    SetWindowLongPtrW(g_hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&nami_window_proc));

    g_installed = true;
    log_tide("il2cpp: main-thread executor installed (window %p)", (void*)g_hwnd);
    ReleaseSRWLockExclusive(&g_install_lock);
    return true;
}

bool il2cpp_on_main_thread() {
    return InterlockedCompareExchange(&g_running, 0, 0) != 0;
}

bool run_il2cpp_op(int (*fn)(void*), void* arg, int timeout_ms) {
    // Re-entrant: the caller IS the main-thread drain - run inline.
    if (il2cpp_on_main_thread()) {
        return fn(arg) == 0;
    }
    if (!g_installed && !install_window_executor()) {
        return false;
    }

    auto* req = new (std::nothrow) Request{};
    if (req == nullptr) {
        return false;
    }
    req->fn = fn;
    req->arg = arg;
    req->done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (req->done == nullptr) {
        delete req;
        return false;
    }
    req->next = nullptr;

    EnterCriticalSection(&g_lock);
    if (g_tail != nullptr) {
        g_tail->next = req;
        g_tail = req;
    } else {
        g_head = req;
        g_tail = req;
    }
    InterlockedExchange(&g_pending, 1);
    LeaveCriticalSection(&g_lock);

    // Wake the main thread's message pump: PostMessage (non-blocking) + wait on the
    // request's event. The subclassed proc drains on the main thread.
    PostMessageW(g_hwnd, WM_NAMI_DRAIN, 0, 0);

    const DWORD wait = timeout_ms <= 0 ? INFINITE : static_cast<DWORD>(timeout_ms);
    const DWORD result = WaitForSingleObject(req->done, wait);
    CloseHandle(req->done);
    return result == WAIT_OBJECT_0;
}

}  // namespace nami::il2cpp

// Exports consumed by the managed Tide layer (Nami.Tide) and the loader boot path.
extern "C" __declspec(dllexport) int nami_il2cpp_available() {
    return nami::il2cpp::detect_il2cpp() ? 1 : 0;
}

// Installs the IL2CPP main-thread executor (window-proc drain). Called by the loader boot
// thread once the game window exists; safe to call any time after that.
extern "C" __declspec(dllexport) int nami_il2cpp_install() {
    return nami::il2cpp::install_il2cpp_executor() ? 1 : 0;
}

// Free a string buffer returned by an IL2CPP op (malloc'd UTF-8).
extern "C" __declspec(dllexport) void nami_il2cpp_free(void* ptr) {
    if (ptr != nullptr) {
        free(ptr);
    }
}
