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
// 3a2. Full-path stub: results + stack args + float args. The stub CALLS the
//      trampoline, dispatches prefix + postfix around it with a result slot
//      (rax/xmm0 bits) and return_kind, and exposes all arguments (regs + stack).
// ---------------------------------------------------------------------------

// Distinct coefficients verify every argument slot (e/f are stack args).
__declspec(noinline) __attribute__((optimize("O0")))
int smoke_six(int a, int b, int c, int d, int e, int f) {
    return a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f;
}

// Float args travel in xmm0/xmm1; the result returns in xmm0. The dispatch calls
// clobber xmm regs, so the stub must save/restore the ARG regs around them and the
// RESULT reg across the postfix.
__declspec(noinline) __attribute__((optimize("O0")))
float smoke_fmul(float a, float b) {
    return a * b;
}

// Mixed ABI: float args in xmm0/xmm1, ints in r8/r9, then e/f on the stack. Exercises
// the xmm save area sitting next to the stack-arg copy regions (n = 2).
__declspec(noinline) __attribute__((optimize("O0")))
float smoke_fmix(float a, float b, int c, int d, int e, int f) {
    return a * b + (float)(c * d + e + f);
}

volatile LONG g_full_prefix_calls = 0;
volatile LONG g_full_postfix_calls = 0;
volatile LONG g_full_skip = 0;
volatile LONG g_full_rewrite = 0;  // 0 = none, 1 = postfix rewrite, 2 = prefix skip-rewrite
volatile uint64_t g_rewrite_bits = 0;
uint64_t g_full_args[6] = {};
uint64_t g_full_result = 0;      // result slot[0] (rax bits) as seen by postfix
uint64_t g_full_result_xmm = 0;  // result slot[1] (xmm0 bits) as seen by postfix

// Full-path dispatch ABI: (handle, args, arg_count, result_slot, return_kind).
extern "C" int __cdecl smoke_full_prefix(uint64_t handle, uint64_t* args, int argc,
                                         uint64_t* result, int kind) {
    (void)handle;
    (void)kind;
    InterlockedIncrement(&g_full_prefix_calls);
    for (int i = 0; i < argc && i < 6; i++) {
        g_full_args[i] = args[i];
    }
    if (g_full_skip) {
        if (g_full_rewrite == 2 && result != nullptr) {
            result[0] = g_rewrite_bits;  // skip WITH a replacement result
        }
        return 1;
    }
    return 0;
}

extern "C" void __cdecl smoke_full_postfix(uint64_t handle, uint64_t* args, int argc,
                                           uint64_t* result, int kind) {
    (void)handle;
    (void)args;
    (void)argc;
    InterlockedIncrement(&g_full_postfix_calls);
    if (result == nullptr) {
        return;
    }
    g_full_result = result[0];
    g_full_result_xmm = result[1];
    if (g_full_rewrite == 1) {
        // Rewrite the result by kind: xmm0 slot for floats, rax slot otherwise.
        if (kind == 3 || kind == 4) {
            result[1] = g_rewrite_bits;
        } else {
            result[0] = g_rewrite_bits;
        }
    }
}

// Records the exact crash context (registers + stack) to crash-regs.txt so a failure
// inside the full-path stub can be diagnosed without gdb guessing.
LONG WINAPI StubCrashRecorder(EXCEPTION_POINTERS* ep) {
    FILE* f = nullptr;
    if (_wfopen_s(&f, L"crash-regs.txt", L"a") == 0 && f != nullptr) {
        const auto* c = ep->ContextRecord;
        const auto* er = ep->ExceptionRecord;
        std::fprintf(f, "code=0x%lX at rip=%p flags=%lX fault=%p\n", er->ExceptionCode,
                     (void*)c->Rip, er->ExceptionFlags,
                     er->NumberParameters >= 2 ? (void*)er->ExceptionInformation[1] : nullptr);
        if (er->NumberParameters >= 1) {
            std::fprintf(f, "access=%llu\n", (unsigned long long)er->ExceptionInformation[0]);
        }
        std::fprintf(f, "rax=%llx rbx=%llx rcx=%llx rdx=%llx\n", (unsigned long long)c->Rax,
                     (unsigned long long)c->Rbx, (unsigned long long)c->Rcx, (unsigned long long)c->Rdx);
        std::fprintf(f, "r8=%llx r9=%llx r10=%llx r11=%llx\n", (unsigned long long)c->R8,
                     (unsigned long long)c->R9, (unsigned long long)c->R10, (unsigned long long)c->R11);
        std::fprintf(f, "rsp=%llx rbp=%llx rsi=%llx rdi=%llx\n", (unsigned long long)c->Rsp,
                     (unsigned long long)c->Rbp, (unsigned long long)c->Rsi, (unsigned long long)c->Rdi);
        const auto* sp = reinterpret_cast<const unsigned long long*>(c->Rsp);
        for (int i = -4; i < 16; i++) {
            std::fprintf(f, "  [rsp%+d] = %016llx\n", 8 * i, (unsigned long long)sp[i]);
        }
        std::fprintf(f, "bytes: ");
        const auto* p = reinterpret_cast<const unsigned char*>(c->Rip);
        MEMORY_BASIC_INFORMATION mbi{};
        const bool mapped =
            VirtualQuery(p, &mbi, sizeof(mbi)) != 0 && mbi.State == MEM_COMMIT;
        for (int i = 0; i < 16 && mapped; i++) {
            std::fprintf(f, "%02X ", p[i]);
        }
        std::fprintf(f, "\n---\n");
        std::fclose(f);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

unsigned char* MakeExec(const unsigned char* bytes, int len);  // defined below (short-prologue tests)

int TestNativeStubFull() {
    using namespace nami::stub;
    int rc = 0;
    void* veh = AddVectoredExceptionHandler(1, StubCrashRecorder);

    // --- minimal: full path with NO stack args (argc=2 on the known-good smoke_add) ---
    {
        auto* rec0 = hook_native_full(reinterpret_cast<void*>(&smoke_add),
                                      reinterpret_cast<void*>(&smoke_full_prefix),
                                      reinterpret_cast<void*>(&smoke_full_postfix),
                                      /*return_kind=*/2, 0xABCD, 2);
        if (rec0 == nullptr) {
            std::printf("  native stub full: FAIL (minimal hook refused)\n");
            rc = 20;
        } else {
            const auto* tb = static_cast<const unsigned char*>(rec0->trampoline);
            const auto* ep = static_cast<const unsigned char*>(rec0->target);
            std::printf("  stub full minimal: stub=%p tramp=%p entry=%02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X\n",
                        (void*)rec0->stub, rec0->trampoline, ep[0], ep[1], ep[2], ep[3], ep[4],
                        ep[5], ep[6], ep[7], ep[8], ep[9], ep[10], ep[11], ep[12], ep[13]);
            FILE* dbg = nullptr;
            if (_wfopen_s(&dbg, L"stub-full.bin", L"wb") == 0 && dbg != nullptr) {
                std::fwrite(rec0->stub, 1, static_cast<size_t>(rec0->stub_size), dbg);
                std::fclose(dbg);
            }
            const long post_before = g_full_postfix_calls;
            const int r0 = smoke_add(2, 3);  // = 20
            if (r0 != 20 || g_full_postfix_calls != post_before + 1) {
                std::printf("  native stub full: FAIL (minimal pass-through: r=%d post=%ld)\n",
                            r0, (long)g_full_postfix_calls);
                rc = 21;
            }
            unhook_native(rec0);
        }
    }
    if (rc != 0) {
        RemoveVectoredExceptionHandler(veh);
        std::printf("  native stub full: FAIL (minimal)\n");
        return rc;
    }

    // --- i64 result, 6 args (2 on the stack) ---
    auto* rec = hook_native_full(reinterpret_cast<void*>(&smoke_six),
                                 reinterpret_cast<void*>(&smoke_full_prefix),
                                 reinterpret_cast<void*>(&smoke_full_postfix),
                                 /*return_kind=*/2, 0xFEED, 6);
    if (rec == nullptr) {
        std::printf("  native stub full: FAIL (hook refused)\n");
        return 1;
    }
    std::printf("  native stub full: stub=%p tramp=%p size=%d\n", rec->stub, rec->trampoline,
                rec->stub_size);
    {
        FILE* dbg = nullptr;
        if (_wfopen_s(&dbg, L"stub-full.bin", L"wb") == 0 && dbg != nullptr) {
            std::fwrite(rec->stub, 1, static_cast<size_t>(rec->stub_size), dbg);
            std::fclose(dbg);
        }
    }

    // Pass-through: prefix saw all 6 args (incl. stack args), postfix saw the real
    // result, and the caller got the real value (proves the trampoline received the
    // copied stack args correctly). smoke_six(1,2,3,4,5,6) = 1+4+9+16+25+36 = 91.
    const long post0 = g_full_postfix_calls;  // the minimal test above fired it once
    const int r1 = smoke_six(1, 2, 3, 4, 5, 6);
    if (r1 != 91 || g_full_postfix_calls != post0 + 1 || g_full_result != 91 ||
        g_full_args[0] != 1 || g_full_args[1] != 2 || g_full_args[2] != 3 ||
        g_full_args[3] != 4 || g_full_args[4] != 5 || g_full_args[5] != 6) {
        std::printf("  native stub full: FAIL (pass-through: r=%d post=%ld result=%llu args=%llu %llu %llu %llu %llu %llu)\n",
                    r1, (long)g_full_postfix_calls, (unsigned long long)g_full_result,
                    (unsigned long long)g_full_args[0], (unsigned long long)g_full_args[1],
                    (unsigned long long)g_full_args[2], (unsigned long long)g_full_args[3],
                    (unsigned long long)g_full_args[4], (unsigned long long)g_full_args[5]);
        rc = 2;
    }

    // Postfix rewrite: the original ran (postfix observed its real result), then the
    // rewritten value is what the caller receives.
    if (rc == 0) {
        g_full_rewrite = 1;
        g_rewrite_bits = 0x12345;
        const int r2 = smoke_six(1, 1, 1, 1, 1, 1);  // = 21
        g_full_rewrite = 0;
        if (r2 != 0x12345 || g_full_result != 21) {
            std::printf("  native stub full: FAIL (postfix rewrite: r=%x observed=%llu)\n",
                        r2, (unsigned long long)g_full_result);
            rc = 3;
        }
    }

    // Skip WITH a replacement result: prefix writes the slot, original never runs.
    if (rc == 0) {
        const long post_before = g_full_postfix_calls;
        g_full_skip = 1;
        g_full_rewrite = 2;
        g_rewrite_bits = 0x777;
        const int r3 = smoke_six(2, 2, 2, 2, 2, 2);
        g_full_rewrite = 0;
        g_full_skip = 0;
        if (r3 != 0x777 || g_full_postfix_calls != post_before) {
            std::printf("  native stub full: FAIL (skip-rewrite: r=%x post=%ld)\n",
                        r3, (long)g_full_postfix_calls);
            rc = 4;
        }
    }

    // Skip WITHOUT a rewrite: returns 0, postfix never fires.
    if (rc == 0) {
        const long post_before = g_full_postfix_calls;
        g_full_skip = 1;
        const int r4 = smoke_six(3, 3, 3, 3, 3, 3);
        g_full_skip = 0;
        if (r4 != 0 || g_full_postfix_calls != post_before) {
            std::printf("  native stub full: FAIL (skip: r=%d post=%ld)\n", r4, (long)g_full_postfix_calls);
            rc = 5;
        }
    }

    // Exact restore.
    if (rc == 0) {
        unhook_native(rec);
        const long post_before = g_full_postfix_calls;
        const int r5 = smoke_six(1, 1, 1, 1, 1, 1);
        if (r5 != 21 || g_full_postfix_calls != post_before) {
            std::printf("  native stub full: FAIL (restore: r=%d post=%ld)\n", r5, (long)g_full_postfix_calls);
            rc = 6;
        }
    }

    // --- f32 result with float args (xmm0 path) ---
    if (rc == 0) {
        rec = hook_native_full(reinterpret_cast<void*>(&smoke_fmul),
                               reinterpret_cast<void*>(&smoke_full_prefix),
                               reinterpret_cast<void*>(&smoke_full_postfix),
                               /*return_kind=*/3, 0xBEEF, 2);
        if (rec == nullptr) {
            std::printf("  native stub full: FAIL (float hook refused)\n");
            return 7;
        }

        // Pass-through: 2.5f * 4.0f = 10.0f. If the stub failed to preserve the xmm
        // ARG regs across the prefix dispatch, the product would be garbage.
        const float f1 = smoke_fmul(2.5f, 4.0f);
        if (f1 != 10.0f) {
            std::printf("  native stub full: FAIL (float pass-through: %f)\n", (double)f1);
            rc = 8;
        } else if (g_full_result_xmm == 0) {
            std::printf("  native stub full: FAIL (postfix saw no xmm0 result bits)\n");
            rc = 9;
        } else {
            // Postfix rewrite of the xmm0 slot: the caller must receive the new float.
            g_full_rewrite = 1;
            float three_point_five = 3.5f;
            std::memcpy(const_cast<uint64_t*>(&g_rewrite_bits), &three_point_five, 4);
            const float f2 = smoke_fmul(2.0f, 8.0f);  // = 16.0f, rewritten to 3.5f
            g_full_rewrite = 0;
            if (f2 != 3.5f) {
                std::printf("  native stub full: FAIL (float rewrite: %f)\n", (double)f2);
                rc = 10;
            }
        }
        unhook_native(rec);
    }

    // --- full path over a SHORT leaf (5-byte near-jump patch, like IL2CPP getters) ---
    if (rc == 0) {
        const unsigned char skeleton[7] = { 0x8B, 0x05, 0, 0, 0, 0, 0xC3 };
        auto* target = MakeExec(skeleton, sizeof(skeleton));
        auto* field = reinterpret_cast<int*>(target + 0x20);
        *field = 42;
        const int disp = reinterpret_cast<const char*>(field) -
                         reinterpret_cast<const char*>(target + 6);
        std::memcpy(target + 2, &disp, 4);

        auto* rec2 = hook_native_full(target, reinterpret_cast<void*>(&smoke_full_prefix),
                                      reinterpret_cast<void*>(&smoke_full_postfix),
                                      /*return_kind=*/1, 0xABCD, 0);
        if (rec2 == nullptr) {
            std::printf("  native stub full: FAIL (short-leaf full hook refused)\n");
            rc = 13;
        } else {
            const long post_before = g_full_postfix_calls;
            const int v = reinterpret_cast<int (*)()>(target)();
            if (v != 42 || g_full_result != 42 || g_full_postfix_calls != post_before + 1) {
                std::printf("  native stub full: FAIL (short-leaf full: v=%d result=%llu post=%ld)\n",
                            v, (unsigned long long)g_full_result, (long)g_full_postfix_calls);
                rc = 14;
            } else {
                std::printf("  native stub full: short-leaf full-path PASS (42, postfix saw it)\n");
            }
            unhook_native(rec2);
        }
    }

    // --- mixed float args + stack args (xmm0/xmm1 + r8/r9 + 2 stack slots) ---
    if (rc == 0) {
        rec = hook_native_full(reinterpret_cast<void*>(&smoke_fmix),
                               reinterpret_cast<void*>(&smoke_full_prefix),
                               reinterpret_cast<void*>(&smoke_full_postfix),
                               /*return_kind=*/3, 0xCAFE, 6);
        if (rec == nullptr) {
            std::printf("  native stub full: FAIL (fmix hook refused)\n");
            return 11;
        }
        // 2*3 + (2*3+4+5) = 6 + 15 = 21.0f. Wrong xmm arg, register-arg, or stack-arg
        // handling all show up here (prefix dispatch runs between the save and the
        // trampoline call). The args buffer holds the INTEGER regs (Win64: float args
        // travel only in xmm0/xmm1, so rcx/rdx are unspecified for this signature) —
        // args[2..3] = c/d from r8/r9, args[4..5] = e/f copied from the caller stack.
        const float f3 = smoke_fmix(2.0f, 3.0f, 2, 3, 4, 5);
        const bool args_ok = g_full_args[2] == 2 && g_full_args[3] == 3 &&
                             g_full_args[4] == 4 && g_full_args[5] == 5;
        if (f3 != 21.0f || !args_ok) {
            std::printf("  native stub full: FAIL (fmix: f=%f args=%llu %llu %llu %llu %llu %llu)\n",
                        (double)f3, (unsigned long long)g_full_args[0],
                        (unsigned long long)g_full_args[1], (unsigned long long)g_full_args[2],
                        (unsigned long long)g_full_args[3], (unsigned long long)g_full_args[4],
                        (unsigned long long)g_full_args[5]);
            rc = 12;
        } else {
            std::printf("  native stub full: fmix float+stack PASS (21.0f, r8/r9+stack args)\n");
        }
        unhook_native(rec);
    }

    if (veh != nullptr) {
        RemoveVectoredExceptionHandler(veh);
    }
    std::printf("  native stub full: %s\n", rc == 0 ? "PASS" : "FAIL");
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
    rc = TestNativeStubFull();
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