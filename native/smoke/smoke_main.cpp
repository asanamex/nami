#include "core/bootguard.h"
#include "core/nami_common.h"
#include "core/runtime_host.h"
#include "loader/native_stub.h"

#include <cstdio>
#include <filesystem>
#include <string>

#ifdef _WIN32
#include <windows.h>
#endif

namespace fs = std::filesystem;

namespace {

std::string TempDir(const char* tag) {
    char tmp[MAX_PATH]{};
#ifdef _WIN32
    GetTempPathA(MAX_PATH, tmp);
#endif
    std::string base = tmp;
    if (base.empty()) {
        base = ".";
    }
    const std::string dir = base + "\\nami-smoke-" + tag;
    std::error_code ec;
    fs::remove_all(dir, ec);
    fs::create_directories(dir, ec);
    return dir;
}

bool FileExists(const std::string& path) {
    return fs::exists(path);
}

// ---------------------------------------------------------------------------
// 1. Marker / safe-mode state machine (pure file logic, no crashes).
// ---------------------------------------------------------------------------
int TestBootGuardFiles() {
    using namespace nami::bootguard;
    const std::string tmp = TempDir("files");
    int rc = 0;

    auto fail = [&](int code, const char* what) {
        std::printf("  bootguard files: FAIL (%s)\n", what);
        rc = code;
    };

    // Boot start writes boot-pending with the stage.
    if (!MarkBootStart(tmp, Stage_InexArm)) {
        fail(1, "MarkBootStart");
    } else if (!FileExists(tmp + "\\boot-pending")) {
        fail(2, "boot-pending not written");
    }

    // A crash during boot writes the crash log + safe-mode marker.
    if (rc == 0) {
        NoteCrash(tmp, "host_coreclr", "0xC0000005", 0x1234, 0x5678, true);
        if (!FileExists(tmp + "\\nami-crash.log")) {
            fail(3, "nami-crash.log not written");
        } else if (!SafeModeEnabled(tmp)) {
            fail(4, "safe-mode not enabled after crash");
        }
    }

    // Consuming: decrements, auto-clears at 0.
    if (rc == 0) {
        if (ConsumeSafeModeBoot(tmp) != 3) {
            fail(5, "first consume did not return 3");
        } else if (!SafeModeEnabled(tmp)) {
            fail(6, "safe-mode cleared too early");
        }
        ConsumeSafeModeBoot(tmp);
        ConsumeSafeModeBoot(tmp);
        if (SafeModeEnabled(tmp)) {
            fail(7, "safe-mode not cleared after 3 boots");
        } else if (FileExists(tmp + "\\safe-mode")) {
            fail(8, "safe-mode file not deleted");
        }
    }

    // MarkBootComplete removes boot-pending.
    if (rc == 0) {
        MarkBootStart(tmp, Stage_ManagedBoot);
        MarkBootComplete(tmp);
        if (FileExists(tmp + "\\boot-pending")) {
            fail(9, "boot-pending not cleared by MarkBootComplete");
        }
    }

    if (rc == 0) {
        std::printf("  bootguard files: PASS\n");
    }
    std::error_code ec;
    fs::remove_all(tmp, ec);
    return rc;
}

// ---------------------------------------------------------------------------
// 2. Fault containment: an AV on a Nami-owned thread must NOT take the process
//    down — the thread dies, hook-ready is signaled, crash evidence is written.
// ---------------------------------------------------------------------------
#ifdef _WIN32
volatile LONG g_contained_calls = 0;
volatile LONG g_crash_thread_done = 0;

void OnContained() {
    InterlockedIncrement(&g_contained_calls);
}

DWORD WINAPI CrashThreadProc(LPVOID) {
    nami::bootguard::MarkThreadOwned(true);
    // Deliberate access violation on this thread only.
    *static_cast<volatile int*>(nullptr) = 0xBAD;
    // Unreachable if containment worked (the thread is terminated by the handler).
    InterlockedExchange(&g_crash_thread_done, 1);
    return 0;
}

int TestFaultContainment() {
    using namespace nami::bootguard;
    const std::string tmp = TempDir("veh");
    int rc = 0;

    InstallCrashHandler(tmp, &OnContained);

    HANDLE h = CreateThread(nullptr, 0, CrashThreadProc, nullptr, 0, nullptr);
    if (h == nullptr) {
        std::printf("  bootguard VEH: FAIL (CreateThread)\n");
        rc = 1;
    } else {
        const DWORD wait = WaitForSingleObject(h, 10000);
        CloseHandle(h);

        const bool contained =
            wait == WAIT_OBJECT_0 &&                 // thread terminated (by the handler)
            g_contained_calls == 1 &&                // hook-ready callback fired
            g_crash_thread_done == 0 &&              // and the faulting thread never resumed
            FileExists(tmp + "\\nami-crash.log") &&  // evidence written
            SafeModeEnabled(tmp);                    // next boot is safe
        if (!contained) {
            std::printf("  bootguard VEH: FAIL (wait=%lu contained=%ld resumed=%ld log=%d safe=%d)\n",
                        wait, (long)g_contained_calls, (long)g_crash_thread_done,
                        FileExists(tmp + "\\nami-crash.log") ? 1 : 0, SafeModeEnabled(tmp) ? 1 : 0);
            rc = 2;
        } else {
            std::printf("  bootguard VEH: PASS (thread contained, game-side process survived)\n");
        }
    }

    std::error_code ec;
    fs::remove_all(tmp, ec);
    return rc;
}
#endif  // _WIN32

// ---------------------------------------------------------------------------
// 3. Dispatch-stub detour: hook a real function in this module, observe its
//    arguments, skip it, and restore it exactly. This exercises the exact stub +
//    trampoline + restore machinery IL2CPP patching uses (the il2cpp resolution
//    half is game-only and documented as such).
// ---------------------------------------------------------------------------
#ifdef _WIN32

// Kept at -O0 so the prologue is a fat, relocatable frame (push rbp; mov rbp,rsp;
// sub rsp,N) — Release-optimized leaf functions can be 4-5 bytes and would be
// refused by the 14-byte absolute detour, which is the documented v1 policy.
__declspec(noinline) __attribute__((optimize("O0"))) __declspec(dllexport)
int smoke_add(int a, int b) {
    volatile int acc = a;
    for (int i = 0; i < 4; i++) {
        acc += b + i;
    }
    return acc;
}

volatile LONG g_dispatch_calls = 0;
volatile LONG g_skip = 0;
uint64_t g_seen_args[4] = {};

// The dispatch ABI the stub uses: (user_handle, args[4], arg_count) -> skip?1:0.
extern "C" int __cdecl smoke_test_dispatch(uint64_t handle, uint64_t* args, int arg_count) {
    InterlockedIncrement(&g_dispatch_calls);
    if (arg_count >= 1) {
        g_seen_args[0] = args[0];
    }
    if (arg_count >= 2) {
        g_seen_args[1] = args[1];
    }
    (void)handle;
    return g_skip ? 1 : 0;
}

int TestNativeStub() {
    using namespace nami::stub;
    int rc = 0;

    auto* rec = hook_native_at(reinterpret_cast<void*>(&smoke_add),
                               reinterpret_cast<void*>(&smoke_test_dispatch),
                               0xABCD, 2);
    if (rec == nullptr) {
        std::printf("  native stub: FAIL (hook refused)\n");
        return 1;
    }

    // Pass-through: original runs with its arguments, dispatch observed them.
    // smoke_add(a, b) = a + (b+0) + (b+1) + (b+2) + (b+3)  =>  smoke_add(2,3) = 20.
    const int r1 = smoke_add(2, 3);
    if (r1 != 2 + 3 + 4 + 5 + 6 || g_dispatch_calls != 1 ||
        g_seen_args[0] != 2 || g_seen_args[1] != 3) {
        std::printf("  native stub: FAIL (pass-through: r=%d calls=%ld a0=%llu a1=%llu)\n",
                    r1, (long)g_dispatch_calls, (unsigned long long)g_seen_args[0],
                    (unsigned long long)g_seen_args[1]);
        rc = 2;
    }

    // Skip: dispatch returns nonzero -> stub returns 0, original never runs.
    if (rc == 0) {
        InterlockedExchange(&g_skip, 1);
        const int r2 = smoke_add(10, 20);
        InterlockedExchange(&g_skip, 0);
        if (r2 != 0 || g_dispatch_calls != 2) {
            std::printf("  native stub: FAIL (skip: r=%d calls=%ld)\n", r2, (long)g_dispatch_calls);
            rc = 3;
        }
    }

    // Exact restore: unhook, original behavior returns, dispatch no longer fires.
    if (rc == 0) {
        unhook_native(rec);
        const int r3 = smoke_add(7, 8);  // 7 + 8+9+10+11 = 45
        if (r3 != 7 + 8 + 9 + 10 + 11 || g_dispatch_calls != 2) {
            std::printf("  native stub: FAIL (restore: r=%d calls=%ld)\n", r3, (long)g_dispatch_calls);
            rc = 4;
        }
    }

    std::printf("  native stub: %s\n", rc == 0 ? "PASS" : "FAIL");
    return rc;
}
#endif  // _WIN32

}  // namespace

int main() {
    const bool version_ok = nami::version_string() == "0.1.0";
    std::printf("nami_smoke: %s (version=%s)\n", version_ok ? "PASS" : "FAIL",
                nami::version_string().c_str());
    if (!version_ok) {
        return 1;
    }

    int rc = TestBootGuardFiles();
    if (rc != 0) {
        return rc;
    }

#ifdef _WIN32
    rc = TestFaultContainment();
    if (rc != 0) {
        return rc;
    }
    rc = TestNativeStub();
    if (rc != 0) {
        return rc;
    }
#else
    std::printf("  bootguard VEH + native stub: SKIP (Windows only)\n");
#endif

    std::printf("nami_smoke: all checks PASS\n");
    return 0;
}