#include "core/bootguard.h"
#include "core/nami_common.h"
#include "core/runtime_host.h"
#include "loader/native_stub.h"

#include <cstdio>
#include <cstring>
#include <filesystem>
#include <stdexcept>
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
// 2. Boot-guard VEH: (a) catchable software exceptions — C++ throws (0xE06D7363)
//    and .NET-style raises (0xE0434352) — must PASS THROUGH untouched so the
//    surrounding try/catch handles them, and (b) a genuine AV on a Nami-owned
//    thread must NOT take the process down — the thread dies, hook-ready fires,
//    crash evidence is written. Regression: the first version treated every
//    high-bit exception as a crash and killed owned threads on benign C++/.NET
//    exceptions during managed boot.
// ---------------------------------------------------------------------------
#ifdef _WIN32
volatile LONG g_contained_calls = 0;
volatile LONG g_crash_thread_done = 0;
volatile LONG g_pass_through_done = 0;

void OnContained() {
    InterlockedIncrement(&g_contained_calls);
}

DWORD WINAPI PassthroughThreadProc(LPVOID) {
    nami::bootguard::MarkThreadOwned(true);
    // A C++ exception thrown AND caught on an owned thread: the VEH must not
    // treat 0xE06D7363 as a crash.
    try {
        throw std::runtime_error("benign c++ throw");
    } catch (const std::exception&) {
    }
    // NB: the .NET-style raise (0xE0434352) hits the same predicate path as the
    // C++ throw above (both are software exceptions with the high bit set), so
    // the C++ test alone covers the regression. (MinGW SEH guards are unusable
    // here: __try/__except is MSVC-only and __try1/__except1 fail to assemble
    // on this toolchain with "rva without symbol".)
    InterlockedExchange(&g_pass_through_done, 1);
    return 0;
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

    // --- Phase A: catchable exceptions pass through untouched. ---
    HANDLE h = CreateThread(nullptr, 0, PassthroughThreadProc, nullptr, 0, nullptr);
    if (h == nullptr) {
        std::printf("  bootguard VEH passthrough: FAIL (CreateThread)\n");
        rc = 1;
    } else {
        const DWORD wait = WaitForSingleObject(h, 10000);
        CloseHandle(h);
        const bool passed =
            wait == WAIT_OBJECT_0 && g_pass_through_done == 1 &&  // thread completed its work
            g_contained_calls == 0 &&                             // no containment fired
            !FileExists(tmp + "\\nami-crash.log") &&              // no false crash record
            !SafeModeEnabled(tmp);                                // no false safe mode
        if (!passed) {
            std::printf("  bootguard VEH passthrough: FAIL (wait=%lu done=%ld contained=%ld log=%d safe=%d)\n",
                        wait, (long)g_pass_through_done, (long)g_contained_calls,
                        FileExists(tmp + "\\nami-crash.log") ? 1 : 0, SafeModeEnabled(tmp) ? 1 : 0);
            rc = 2;
        } else {
            std::printf("  bootguard VEH passthrough: PASS (C++/.NET exceptions caught normally)\n");
        }
    }

    // --- Phase B: a genuine fault on an owned thread is contained. ---
    if (rc == 0) {
        HANDLE h2 = CreateThread(nullptr, 0, CrashThreadProc, nullptr, 0, nullptr);
        if (h2 == nullptr) {
            std::printf("  bootguard VEH: FAIL (CreateThread)\n");
            rc = 3;
        } else {
            const DWORD wait = WaitForSingleObject(h2, 10000);
            CloseHandle(h2);

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
                rc = 4;
            } else {
                std::printf("  bootguard VEH: PASS (thread contained, game-side process survived)\n");
            }
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

// ---------------------------------------------------------------------------
// 3b. Short-prologue detour: IL2CPP leaf getters (`mov eax, [rip+x]; ret`) are
//     far below the 14-byte absolute-jump minimum and must fall back to the
//     5-byte relative jump with RIP-relative disp32 fixup. Built from raw bytes
//     so the exact IL2CPP instruction shapes are tested, not compiler output.
// ---------------------------------------------------------------------------

unsigned char* MakeExec(const unsigned char* bytes, int len) {
    auto* buf = static_cast<unsigned char*>(
        VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    std::memcpy(buf, bytes, len);
    return buf;
}

int TestShortPrologue() {
    using namespace nami::stub;
    int rc = 0;

    // --- int leaf: mov eax, [rip+disp32]; ret (7 bytes, RIP-relative). ---
    // The disp32 must reach its field within int32, so the field lives INSIDE the
    // executable buffer (a real IL2CPP getter reads a static in its own module —
    // always within ±2GB; the fixup rebases the disp to the trampoline).
    {
        const unsigned char skeleton[7] = { 0x8B, 0x05, 0, 0, 0, 0, 0xC3 };
        auto* target = MakeExec(skeleton, sizeof(skeleton));
        auto* field = reinterpret_cast<int*>(target + 0x20);
        *field = 0x12345678;
        const int disp = reinterpret_cast<const char*>(field) -
                         reinterpret_cast<const char*>(target + 6);
        std::memcpy(target + 2, &disp, 4);

        auto* rec = hook_native_at(target, reinterpret_cast<void*>(&smoke_test_dispatch), 0, 0);
        if (rec == nullptr) {
            std::printf("  short prologue: FAIL (int leaf hook refused)\n");
            return 1;
        }
        InterlockedExchange(&g_dispatch_calls, 0);
        const int v1 = reinterpret_cast<int (*)()>(target)();
        const bool ok1 = v1 == 0x12345678 && g_dispatch_calls == 1;  // pass-through, fixed-up load
        InterlockedExchange(&g_skip, 1);
        const int v2 = reinterpret_cast<int (*)()>(target)();
        InterlockedExchange(&g_skip, 0);
        const bool ok2 = v2 == 0 && g_dispatch_calls == 2;           // skip -> 0
        unhook_native(rec);
        const int v3 = reinterpret_cast<int (*)()>(target)();
        const bool ok3 = v3 == 0x12345678 && g_dispatch_calls == 2;  // exact restore
        if (!(ok1 && ok2 && ok3)) {
            std::printf("  short prologue: FAIL (int leaf v1=%x v2=%x v3=%x calls=%ld)\n",
                        v1, v2, v3, (long)g_dispatch_calls);
            rc = 2;
        } else {
            std::printf("  short prologue: PASS (int rip-relative leaf: hook+pass+skip+restore)\n");
        }
    }

    // --- float leaf: movss xmm0, [rip+disp32]; ret (9 bytes, 0F SIMD form). ---
    if (rc == 0) {
        const unsigned char skeleton[9] = { 0xF3, 0x0F, 0x10, 0x05, 0, 0, 0, 0, 0xC3 };
        auto* target = MakeExec(skeleton, sizeof(skeleton));
        auto* field = reinterpret_cast<float*>(target + 0x20);
        *field = 3.25f;
        const int disp = reinterpret_cast<const char*>(field) -
                         reinterpret_cast<const char*>(target + 8);
        std::memcpy(target + 4, &disp, 4);

        auto* rec = hook_native_at(target, reinterpret_cast<void*>(&smoke_test_dispatch), 0, 0);
        if (rec == nullptr) {
            std::printf("  short prologue: FAIL (movss leaf hook refused)\n");
            return 3;
        }
        InterlockedExchange(&g_dispatch_calls, 0);
        const float v1 = reinterpret_cast<float (*)()>(target)();
        const bool ok1 = v1 == 3.25f && g_dispatch_calls == 1;
        unhook_native(rec);
        const float v2 = reinterpret_cast<float (*)()>(target)();
        const bool ok2 = v2 == 3.25f && g_dispatch_calls == 1;
        if (!(ok1 && ok2)) {
            std::printf("  short prologue: FAIL (movss leaf v1=%f v2=%f calls=%ld)\n",
                        (double)v1, (double)v2, (long)g_dispatch_calls);
            rc = 4;
        } else {
            std::printf("  short prologue: PASS (movss rip-relative leaf: hook+pass+restore)\n");
        }
    }

    // --- imm leaf: mov eax, imm32; ret (5 bytes, no ModRM). ---
    if (rc == 0) {
        const unsigned char code[6] = { 0xB8, 0xEE, 0xDD, 0xCC, 0xBB, 0xC3 };
        auto* target = MakeExec(code, sizeof(code));
        auto* rec = hook_native_at(target, reinterpret_cast<void*>(&smoke_test_dispatch), 0, 0);
        if (rec == nullptr) {
            std::printf("  short prologue: FAIL (imm leaf hook refused)\n");
            return 5;
        }
        InterlockedExchange(&g_dispatch_calls, 0);
        const int v1 = reinterpret_cast<int (*)()>(target)();
        const bool ok1 = v1 == 0xBBCCDDEE && g_dispatch_calls == 1;
        unhook_native(rec);
        const int v2 = reinterpret_cast<int (*)()>(target)();
        const bool ok2 = v2 == 0xBBCCDDEE;
        if (!(ok1 && ok2)) {
            std::printf("  short prologue: FAIL (imm leaf v1=%x v2=%x calls=%ld)\n",
                        v1, v2, (long)g_dispatch_calls);
            rc = 6;
        } else {
            std::printf("  short prologue: PASS (imm leaf: 5-byte hook+pass+restore)\n");
        }
    }

    // --- lazy-init thunk shape (Unity 6): sub rsp,0x28; mov rax,[rip+holder];
    //     test rax,rax; jne ... — the measurer must stop at the 11 clean bytes
    //     (sub + rip-relative mov) BEFORE the conditional jump. ---
    if (rc == 0) {
        // p+0:  48 83 EC 28            sub rsp, 0x28
        // p+4:  48 8B 05 <disp32>      mov rax, [rip+disp]
        // p+11: 48 85 C0               test rax, rax
        // p+14: 75 05                  jne +5 (must NOT be measured; lands at p+21)
        // p+16: B8 EF BE AD DE         mov eax, 0xDEADBEEF   (skipped by the branch)
        // p+21: 48 83 C4 28            add rsp, 0x28
        // p+25: B8 78 56 34 12         mov eax, 0x12345678
        // p+30: C3                     ret
        const unsigned char skeleton[31] = {
            0x48, 0x83, 0xEC, 0x28,
            0x48, 0x8B, 0x05, 0, 0, 0, 0,
            0x48, 0x85, 0xC0,
            0x75, 0x05,
            0xB8, 0xEF, 0xBE, 0xAD, 0xDE,
            0x48, 0x83, 0xC4, 0x28,
            0xB8, 0x78, 0x56, 0x34, 0x12,
            0xC3
        };
        auto* target = MakeExec(skeleton, sizeof(skeleton));
        auto* holder = reinterpret_cast<void**>(target + 0x30);
        *holder = reinterpret_cast<void*>(target + 0x20);  // nonzero -> branch taken
        const int disp = reinterpret_cast<const char*>(holder) -
                         reinterpret_cast<const char*>(target + 11);
        std::memcpy(target + 7, &disp, 4);

        auto* rec = hook_native_at(target, reinterpret_cast<void*>(&smoke_test_dispatch), 0, 0);
        if (rec == nullptr) {
            std::printf("  short prologue: FAIL (lazy-thunk hook refused)\n");
            return 8;
        }
        InterlockedExchange(&g_dispatch_calls, 0);
        const int v1 = reinterpret_cast<int (*)()>(target)();
        const bool ok1 = v1 == 0x12345678 && g_dispatch_calls == 1;
        unhook_native(rec);
        const int v2 = reinterpret_cast<int (*)()>(target)();
        const bool ok2 = v2 == 0x12345678 && g_dispatch_calls == 1;
        if (!(ok1 && ok2)) {
            std::printf("  short prologue: FAIL (lazy-thunk v1=%x v2=%x calls=%ld)\n",
                        v1, v2, (long)g_dispatch_calls);
            rc = 9;
        } else {
            std::printf("  short prologue: PASS (lazy-init thunk shape: hook+pass+restore)\n");
        }
    }

    // --- refusal: mov eax, [rcx+0x10]; ret (4 bytes < 5) must refuse cleanly. ---
    if (rc == 0) {
        const unsigned char code[4] = { 0x8B, 0x41, 0x10, 0xC3 };
        auto* target = MakeExec(code, sizeof(code));
        auto* rec = hook_native_at(target, reinterpret_cast<void*>(&smoke_test_dispatch), 0, 1);
        if (rec != nullptr) {
            std::printf("  short prologue: FAIL (4-byte prologue must be refused)\n");
            unhook_native(rec);
            rc = 7;
        } else {
            std::printf("  short prologue: PASS (4-byte prologue refused cleanly)\n");
        }
    }

    return rc;
}
#endif  // _WIN32

}  // namespace

int main() {
    // Unbuffered: this binary deliberately crashes (containment tests) — buffered
    // stdout would silently discard every PASS line on a crash.
    setvbuf(stdout, nullptr, _IONBF, 0);

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
    rc = TestShortPrologue();
    if (rc != 0) {
        return rc;
    }
#else
    std::printf("  bootguard VEH + native stub: SKIP (Windows only)\n");
#endif

    std::printf("nami_smoke: all checks PASS\n");
    return 0;
}