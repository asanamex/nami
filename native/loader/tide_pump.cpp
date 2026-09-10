#include "tide_pump.h"

#include <windows.h>

#include <climits>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <new>

// ---------------------------------------------------------------------------
// Tide main-thread executor.
//
// Unity Mono games run all managed calls on the game's main thread. Foreign threads
// (CreateThread) crash Mono's Boehm GC on their first allocating call (verified at a fixed
// mono-2.0-bdwgc.dll offset regardless of attach method). The robust design: Tide work is
// queued and executed INLINE on the game's main thread by hooking mono_runtime_invoke -
// a function the game's main thread calls constantly. The caller (CoreCLR) blocks on a
// per-request event until the main thread has run the op.
// ---------------------------------------------------------------------------

namespace nami::tide {

// Drains the post-invoke queue (work that must run OUTSIDE the nested runtime_invoke frame).
// Defined below; called by the detour (inside the anonymous namespace).
void drain_post_queue();

namespace {

struct Request {
    TideWorkFn fn;
    void* arg;
    HANDLE done;
    int flags;         // RequestFlags
    Request* next;
};

// Two queues: `pre` work runs BEFORE the original mono_runtime_invoke (the nested drain,
// safe for plain managed calls); `post` work runs AFTER it returns (outside the nested
// frame, safe for Unity scene-iteration internal calls). Both run on the game main thread.
struct QueueState {
    CRITICAL_SECTION lock;
    Request* head;   // pre queue
    Request* tail;
    Request* post_head;  // post queue (run after the current runtime_invoke returns)
    Request* post_tail;
    volatile LONG work_pending;  // fast-path: set when any request is queued
};

QueueState g_queue{};
bool g_queue_initialized = false;
bool g_drain_installed = false;
SRWLOCK g_install_lock = SRWLOCK_INIT;  // guards install_main_thread_drain

// Set while the drain runs on the game main thread (used to detect re-entrant Tide calls
// from the main thread and run them inline instead of deadlocking).
bool g_on_main_thread_drain = false;

using mono_runtime_invoke_fn = void* (*)(void*, void*, void**, void**);
mono_runtime_invoke_fn g_original_runtime_invoke = nullptr;
unsigned char* g_trampoline = nullptr;  // holds the original prologue + jump back

// The detour installed over mono_runtime_invoke: drain Tide work, run the original, then
// drain the post queue (scene-iteration work) outside the nested frame.
void* __stdcall runtime_invoke_detour(void* method, void* obj, void** args, void** exc) {
    // Fast path: nothing queued → skip the lock entirely (this runs on the game's hottest
    // path - every managed invocation).
    if (InterlockedCompareExchange(&g_queue.work_pending, 0, 0) != 0) {
        drain_queue();
    }

    void* result = g_original_runtime_invoke(method, obj, args, exc);

    if (InterlockedCompareExchange(&g_queue.work_pending, 0, 0) != 0) {
        drain_post_queue();
    }

    return result;
}

}  // namespace

// ---------------------------------------------------------------------------
// Shared detour toolkit: measure_relocatable_prologue + build_trampoline live in
// nami::tide (not the anonymous namespace) so the inex legacy lane can detour its
// own targets. Both cover whole instructions until >= min_bytes and refuse
// (return 0/nullptr) on anything unsafe to relocate; scans at most 32 bytes.
// ---------------------------------------------------------------------------

// 0F-prefixed two-byte opcodes that carry a ModRM byte. These cover the SIMD
// move/arith families (movss/movsd/movaps/movdqa/xorps/...), cmov, movzx/movsx,
// imul and the multi-byte NOP. Any other 0F opcode refuses (conservative).
bool Is0FWithModRm(unsigned char op2) {
    switch (op2) {
        case 0x10: case 0x11: case 0x28: case 0x29: case 0x2A: case 0x2B:
        case 0x2C: case 0x2D: case 0x2E: case 0x2F: case 0x40: case 0x41:
        case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47:
        case 0x48: case 0x49: case 0x4A: case 0x4B: case 0x4C: case 0x4D:
        case 0x4E: case 0x4F: case 0x57: case 0x58: case 0x59: case 0x5A:
        case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F: case 0x6F:
        case 0x7F: case 0xAF: case 0xB6: case 0xB7: case 0xBE: case 0xBF:
        case 0x1F:
            return true;
        default:
            return false;
    }
}

// Returns the total length of whole instructions from `p` until >= min_bytes, or 0 if any
// instruction is unmeasurable/unsafe to relocate. Scans at most 32 bytes.
int measure_relocatable_prologue(const unsigned char* p, int min_bytes, bool allow_relative_call,
                                 int* call_offsets, int max_calls, bool allow_rip_relative,
                                 int* rip_offsets, int max_rips) {
    int off = 0;
    int calls = 0;
    int rips = 0;
    while (off < min_bytes) {
        const unsigned char* q = p + off;
        int i = 0;
        bool rex = false;
        int rex_val = 0;
        bool opsize = false;

        // Consume legacy + REX prefixes.
        while (i < 15) {
            unsigned char b = q[i];
            if (b == 0xF0 || b == 0xF2 || b == 0xF3 || b == 0x2E || b == 0x36 || b == 0x3E ||
                b == 0x26 || b == 0x64 || b == 0x65 || b == 0x66 || b == 0x67) {
                if (b == 0x66) {
                    opsize = true;
                }
                i++;
            } else if (b >= 0x40 && b <= 0x4F) {
                rex = true;
                rex_val = b;
                i++;
            } else {
                break;
            }
        }

        if (i >= 15) {
            return 0;
        }

        unsigned char op = q[i];
        bool two_byte = false;
        unsigned char op2 = 0;
        if (op == 0x0F && i + 1 < 15) {
            op2 = q[i + 1];
            if (!Is0FWithModRm(op2)) {
                return 0;  // unknown two-byte opcode: refuse
            }
            two_byte = true;
            i++;  // the second opcode byte is part of the opcode, not a prefix
            op = op2;
        }

        if (op == 0xE8 && allow_relative_call && !two_byte) {
            // Tolerated near CALL (inex jit hooks only; Tide still refuses): fixed
            // 5-byte form, rel32 fixed up in the trampoline by the installer from the
            // recorded offset. The return address lands mid-trampoline and execution
            // continues into the copied prologue - sound unless the callee inspects
            // its return address (true for init-style exports).
            if (calls >= max_calls) {
                return 0;
            }
            if (call_offsets != nullptr) {
                call_offsets[calls] = off;
            }
            calls++;
            off += 5;
            if (off > 32) {
                return 0;
            }
            continue;
        }
        // Refuse: VEX/EVEX/XOP, relative branches (E8/E9/EB plus short Jcc
        // 70-7F, whose verbatim copy would retarget them), ret, int3, ud2.
        // Near CALL is optionally tolerated (see allow_relative_call).
        if (op >= 0x70 && op <= 0x7F) {
            return 0;
        }
        if (op == 0xC4 || op == 0xC5 || op == 0x62 || op == 0x63 || op == 0x8F || op == 0xE8 ||
            op == 0xE9 || op == 0xEB || op == 0xC3 || op == 0xCC) {
            return 0;
        }

        // One-byte opcode with no ModRM.
        bool no_modrm = false;
        if (!two_byte) {
            switch (op) {
                case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
                case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                case 0x68: case 0x6A: case 0x90: case 0x98: case 0x99: case 0x9C: case 0x9D:
                case 0xF8: case 0xF9: case 0xFC: case 0xFD: case 0xC9:
                    no_modrm = true;
                    break;
                default:
                    if (op >= 0xB0 && op <= 0xBF) {
                        no_modrm = true;  // mov reg, imm
                    }
                    break;
            }
        }

        int len = i + 1;
        if (!no_modrm) {
            if (len >= 15) {
                return 0;
            }
            unsigned char modrm = q[len];
            int mod = modrm >> 6;
            int rm = modrm & 7;
            len++;
            bool rip_rel = false;
            if (rm == 4 && mod != 3) {
                // SIB
                if (len >= 15) {
                    return 0;
                }
                unsigned char sib = q[len];
                len++;
                if (mod == 0 && (sib & 7) == 5) {
                    rip_rel = true;  // SIB-encoded RIP-relative
                }
            }
            if (mod == 0 && rm == 5) {
                rip_rel = true;  // RIP-relative
            }
            if (rip_rel) {
                // disp32 at `len`; the instruction ends at len+4 ONLY when no
                // immediate bytes follow. Group-1 ALU (80-83) and MOV r/m,imm
                // (C6/C7) carry trailing immediates after the disp, so the true
                // instruction end is past disp+4 while fixup_relocations rebases
                // against disp+4 (one byte off for e.g. CMP DWORD [RIP+..],0).
                // Relocating such an instruction corrupts its target (observed:
                // mono_jit_init_version's CMP mis-rebased, later AV in
                // mono_class_vtable). Refuse instead; callers fall back to the
                // 5-byte near-jump form or another path.
                if (!two_byte && (op == 0x80 || op == 0x81 || op == 0x82 ||
                                  op == 0x83 || op == 0xC6 || op == 0xC7)) {
                    return 0;
                }
                // Relocatable only with explicit disp32 fixup - record the slot
                // for the installer.
                if (!allow_rip_relative || rips >= max_rips) {
                    return 0;
                }
                if (rip_offsets != nullptr) {
                    rip_offsets[rips] = off + len;
                }
                rips++;
                len += 4;
            } else if (mod == 1) {
                len += 1;
            } else if (mod == 2) {
                len += 4;
            }
        } else if (op >= 0xB8 && op <= 0xBF) {
            len += (rex && (rex_val & 0x08)) ? 8 : 4;  // mov r64/r32, imm
        } else if (op == 0x68) {
            len += 4;
        } else if (op == 0x6A) {
            len += 1;
        }

        // Group-1 ALU with imm (80-83) and mov r/m,imm (C6/C7): imm follows modrm.
        // All eight /r sub-opcodes (ADD/OR/ADC/SBB/AND/SUB/XOR/CMP) take an immediate -
        // earlier code only counted 0-4, mis-measuring e.g. SUB RSP,imm.
        if (!no_modrm && !two_byte) {
            unsigned char modrm = q[i + 1];
            if ((op == 0x80 || op == 0x82 || op == 0x83)) len += 1;
            else if (op == 0x81) len += opsize ? 2 : 4;
            else if (op == 0xC6 && ((modrm >> 3) & 7) == 0) len += 1;
            else if (op == 0xC7 && ((modrm >> 3) & 7) == 0)
                len += (rex && (rex_val & 0x08)) ? 8 : 4;
        }

        if (len >= 15 || len <= 0) {
            return 0;
        }

        off += len;
        if (off > 32) {
            return 0;
        }
    }

    return off;
}

void* alloc_near(unsigned char* target, int size);  // defined below (used by build_trampoline)

// Builds a trampoline: copies the measured original prologue and appends a jump back to
// target+prologue_len. Returns the trampoline entry (the "original" to call).
// Allocated NEAR the target: relocated RIP-relative operands (disp32 fixup) must keep
// their targets within int32 reach of the trampoline - a far VirtualAlloc(NULL) would
// make the fixup overflow and the install refuse. The appended jump back is absolute
// (mov rax,imm64; jmp rax), so no range constraint applies to it.
void* build_trampoline(unsigned char* target, int prologue_len) {
    auto* tramp = static_cast<unsigned char*>(alloc_near(target, prologue_len + 12));
    if (tramp == nullptr) {
        return nullptr;
    }

    for (int i = 0; i < prologue_len; i++) {
        tramp[i] = target[i];
    }

    // jmp [target+prologue_len]
    tramp[prologue_len] = 0x48; tramp[prologue_len + 1] = 0xB8;
    *reinterpret_cast<uint64_t*>(tramp + prologue_len + 2) =
        reinterpret_cast<uint64_t>(target + prologue_len);
    tramp[prologue_len + 10] = 0xFF; tramp[prologue_len + 11] = 0xE0;
    return tramp;
}

// Runs a single request's work and signals its done event. Deletes the request.
void run_request(Request* req) {
    if (req->fn != nullptr) {
        req->fn(req->arg);
    }
    if (req->done != nullptr) {
        SetEvent(req->done);
    }
    delete req;
}

// Pops and runs everything currently in the given queue head pointer (under the lock).
// Returns the number of requests executed.
int drain_list(Request** head, Request** tail) {
    int executed = 0;
    for (;;) {
        EnterCriticalSection(&g_queue.lock);
        Request* req = *head;
        if (req != nullptr) {
            *head = req->next;
            if (*head == nullptr) {
                *tail = nullptr;
            }
        }
        LeaveCriticalSection(&g_queue.lock);

        if (req == nullptr) {
            break;
        }
        run_request(req);
        executed++;
    }
    return executed;
}

// Executes all queued (pre-invoke) Tide work on the calling thread. Called from the detour,
// i.e. on the game's main thread BEFORE the original mono_runtime_invoke. Returns the number
// of requests executed.
int drain_queue() {
    // Mark the drain active so a re-entrant Tide call (mod code on the game main thread
    // calling back into Tide) can detect it and run inline instead of queueing+waiting
    // (which would deadlock the main thread on itself).
    g_on_main_thread_drain = true;
    int executed = drain_list(&g_queue.head, &g_queue.tail);
    g_on_main_thread_drain = false;
    return executed;
}

// Executes the post-invoke queue: work that must run on the game main thread but OUTSIDE the
// nested mono_runtime_invoke frame (Unity scene-iteration APIs). Called by the detour after
// the original mono_runtime_invoke returns. Returns the number of requests executed.
void drain_post_queue() {
    g_on_main_thread_drain = true;
    drain_list(&g_queue.post_head, &g_queue.post_tail);
    // If both queues are now empty, clear the pending flag.
    EnterCriticalSection(&g_queue.lock);
    if (g_queue.head == nullptr && g_queue.post_head == nullptr) {
        InterlockedExchange(&g_queue.work_pending, 0);
    }
    LeaveCriticalSection(&g_queue.lock);
    g_on_main_thread_drain = false;
}

// True when the calling thread is currently executing inside the Tide drain - i.e. the
// game main thread is running queued work right now. Used to detect re-entrant calls.
bool IsTideOnMainThread() {
    return g_on_main_thread_drain;
}

// Installs the mono_runtime_invoke hook so the game main thread drains Tide work.
// Thread-safe: an SRW lock makes concurrent first calls safe (only one install wins; the
// rest observe g_drain_installed). Also initializes the queue critical section once.
bool install_main_thread_drain() {
    if (g_drain_installed) {
        return true;
    }

    AcquireSRWLockExclusive(&g_install_lock);
    if (g_drain_installed) {
        ReleaseSRWLockExclusive(&g_install_lock);
        return true;
    }

    if (!g_queue_initialized) {
        InitializeCriticalSection(&g_queue.lock);
        g_queue_initialized = true;
    }

    const HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
    if (mono == nullptr) {
        ReleaseSRWLockExclusive(&g_install_lock);
        return false;
    }

    void* target = reinterpret_cast<void*>(GetProcAddress(mono, "mono_runtime_invoke"));
    if (target == nullptr) {
        ReleaseSRWLockExclusive(&g_install_lock);
        return false;
    }

    auto* p = static_cast<unsigned char*>(target);

    // Measure the relocatable prologue (whole instructions covering >= 14 bytes).
    const int prologue_len = measure_relocatable_prologue(p, 14);
    if (prologue_len <= 0) {
        ReleaseSRWLockExclusive(&g_install_lock);
        return false;
    }

    // Trampoline: original prologue bytes + jump back to target+prologue_len. The "original"
    // the detour calls is the trampoline, NOT the patched address (which would recurse).
    g_trampoline = static_cast<unsigned char*>(build_trampoline(p, prologue_len));
    if (g_trampoline == nullptr) {
        ReleaseSRWLockExclusive(&g_install_lock);
        return false;
    }

    g_original_runtime_invoke = reinterpret_cast<mono_runtime_invoke_fn>(g_trampoline);

    // Write a 14-byte absolute jump to the detour: mov rax, imm64; jmp rax.
    DWORD old_protect = 0;
    if (!VirtualProtect(p, 14, PAGE_EXECUTE_READWRITE, &old_protect)) {
        ReleaseSRWLockExclusive(&g_install_lock);
        return false;
    }

    p[0] = 0x48; p[1] = 0xB8;
    *reinterpret_cast<uint64_t*>(p + 2) = reinterpret_cast<uint64_t>(&runtime_invoke_detour);
    p[10] = 0xFF; p[11] = 0xE0;
    p[12] = 0x90; p[13] = 0x90;

    VirtualProtect(p, 14, old_protect, &old_protect);
    g_drain_installed = true;
    ReleaseSRWLockExclusive(&g_install_lock);
    return true;
}

// Queue a request. Returns false on allocation/event failure (the request is not queued).
// Must be called by a NON-main thread (callers that are already on the game main thread must
// run the work inline instead - see run_on_main_thread).
bool enqueue_request(Request* req) {
    req->done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (req->done == nullptr) {
        return false;
    }
    req->next = nullptr;

    EnterCriticalSection(&g_queue.lock);
    if (req->flags & RequestFlag_PostInvoke) {
        if (g_queue.post_tail != nullptr) {
            g_queue.post_tail->next = req;
            g_queue.post_tail = req;
        } else {
            g_queue.post_head = req;
            g_queue.post_tail = req;
        }
    } else {
        if (g_queue.tail != nullptr) {
            g_queue.tail->next = req;
            g_queue.tail = req;
        } else {
            g_queue.head = req;
            g_queue.tail = req;
        }
    }
    InterlockedExchange(&g_queue.work_pending, 1);
    LeaveCriticalSection(&g_queue.lock);
    return true;
}

// Queues work for the game main thread and blocks until it has run (timeout_ms <= 0 = wait
// forever). The request is heap-allocated and freed by the drain after signaling.
bool run_on_main_thread(TideWorkFn fn, void* arg, int timeout_ms, int flags) {
    // Reentrancy guard: if the CALLER is already the game main thread (i.e. we are inside a
    // drain - a mod hook running on the game thread calls Tide), queueing + waiting would
    // deadlock (the drain can't run while we block it). Run the work inline instead.
    if (IsTideOnMainThread()) {
        return fn(arg) == 0;
    }

    if (!g_drain_installed && !install_main_thread_drain()) {
        return false;
    }

    auto* req = new (std::nothrow) Request{};
    if (req == nullptr) {
        return false;
    }

    req->fn = fn;
    req->arg = arg;
    req->done = nullptr;
    req->next = nullptr;
    req->flags = flags;

    if (!enqueue_request(req)) {
        delete req;
        return false;
    }

    // The hook is on mono_runtime_invoke: the game main thread calls it constantly, so the
    // drain runs soon. Wait for completion.
    const DWORD wait = timeout_ms <= 0 ? INFINITE : static_cast<DWORD>(timeout_ms);
    const DWORD result = WaitForSingleObject(req->done, wait);
    CloseHandle(req->done);

    return result == WAIT_OBJECT_0;
}

namespace detour_toolkit_detail {
SRWLOCK g_detour_lock = SRWLOCK_INIT;
}  // namespace detour_toolkit_detail

// Rebase recorded rel32/disp32 slots from the original address to the trampoline
// copy. Near-call slots point at an E8 opcode: new_rel = (orig_next_rip + orig_rel)
// - tramp_next_rip. RIP-relative slots point at the disp32 (the last 4 bytes of
// the instruction): new_disp = (orig_next_rip + orig_disp) - tramp_next_rip.
// Slots are zero-filled; a disp32 can never start at offset 0, so zero slots are
// always "no entry". Returns false when a rebased target would not fit (caller
// fails the install - never corrupt).
bool fixup_relocations(unsigned char* target, unsigned char* trampoline,
                       const int* call_offsets, const int* rip_offsets) {
    for (int k = 0; k < 4; k++) {
        const int off = call_offsets[k];
        if (off <= 0 || target[off] != 0xE8) {
            continue;
        }
        const int orig_rel = *reinterpret_cast<int*>(target + off + 1);
        auto* orig_next = target + off + 5;
        auto* tramp_next = trampoline + off + 5;
        const long long fixed = static_cast<long long>(orig_next - tramp_next) + orig_rel;
        if (fixed < INT_MIN || fixed > INT_MAX) {
            return false;
        }
        *reinterpret_cast<int*>(trampoline + off + 1) = static_cast<int>(fixed);
    }
    for (int k = 0; k < 4; k++) {
        const int off = rip_offsets[k];
        if (off <= 0) {
            continue;
        }
        const int orig_disp = *reinterpret_cast<int*>(target + off);
        auto* orig_next = target + off + 4;
        auto* tramp_next = trampoline + off + 4;
        const long long fixed = static_cast<long long>(orig_next - tramp_next) + orig_disp;
        if (fixed < INT_MIN || fixed > INT_MAX) {
            return false;
        }
        *reinterpret_cast<int*>(trampoline + off) = static_cast<int>(fixed);
    }
    return true;
}

// Allocates executable memory within ±2GB of target (for rel32 jumps and for
// relocated RIP-relative operands to keep their targets reachable) by walking the
// address space down first, then up, in 64KB steps.
void* alloc_near(unsigned char* target, int size) {
    const auto base = reinterpret_cast<uintptr_t>(target);
    constexpr uintptr_t step = 65536;
    constexpr uintptr_t range = 0x7F000000ULL;  // rel32 reach: ±2GB minus margin
    // Walk the FULL ±2GB window (32512 steps per direction), not a 256MB sliver:
    // game processes pack DLLs densely around GameAssembly.dll, and the old 4096
    // tries both directions often found no free page within ±256MB.
    constexpr int max_tries = static_cast<int>(range / step) + 1;
    for (int dir = -1; dir <= 1; dir += 2) {
        for (int n = 1; n <= max_tries; n++) {
            const long long off = static_cast<long long>(n) * step * dir;
            const long long hint = static_cast<long long>(base) + off;
            if (hint <= 0x10000) {
                break;  // Ran off the bottom of the address space.
            }
            // Must also be within ±2GB of the patch site for the jump back.
            const long long dist =
                hint > static_cast<long long>(base) ? hint - base : base - hint;
            if (dist >= static_cast<long long>(range)) {
                continue;
            }
            void* p = VirtualAlloc(reinterpret_cast<void*>(hint), size,
                                   MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (p == nullptr) {
                continue;
            }
            const long long got =
                static_cast<long long>(reinterpret_cast<uintptr_t>(p)) - static_cast<long long>(base);
            if (got < -static_cast<long long>(range) || got >= static_cast<long long>(range)) {
                VirtualFree(p, 0, MEM_RELEASE);
                continue;
            }
            return p;
        }
    }
    return nullptr;
}

void* install_native_detour_impl(const wchar_t* module_name, const char* export_name,
                                   void* detour, bool allow_relative_call,
                                   bool prefer_near_jump, bool blocking) {
    if (blocking) {
        AcquireSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
    } else if (!TryAcquireSRWLockExclusive(&detour_toolkit_detail::g_detour_lock)) {
        return nullptr;  // Contended (e.g. Ldr-callback context): caller retries later.
    }

    const HMODULE module = GetModuleHandleW(module_name);
    if (module == nullptr) {
        ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
        return nullptr;
    }

    auto* target = static_cast<unsigned char*>(
        reinterpret_cast<void*>(GetProcAddress(module, export_name)));
    if (target == nullptr) {
        ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
        return nullptr;
    }

    int call_offsets[4]{};
    int rip_offsets[4]{};
    const int prologue_len = measure_relocatable_prologue(
        target, 14, allow_relative_call, call_offsets, 4, /*allow_rip_relative=*/true,
        rip_offsets, 4);
    int patch_len = 0;
    bool rel_jump = false;
    if (prologue_len > 0) {
        patch_len = prologue_len;
    } else if (prefer_near_jump) {
        // Absolute form needs 14 clean bytes; retry for the 5-byte relative form.
        patch_len = measure_relocatable_prologue(
            target, 5, allow_relative_call, call_offsets, 4, /*allow_rip_relative=*/true,
            rip_offsets, 4);
        rel_jump = patch_len > 0;
    }
    if (patch_len <= 0) {
        ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
        return nullptr;
    }

    unsigned char* trampoline = nullptr;
    if (rel_jump) {
        // Both jumps are rel32: the trampoline must sit within ±2GB of the target,
        // and the detour must be within ±2GB of the patch site.
        const long long reach =
            static_cast<unsigned char*>(detour) > target
                ? static_cast<unsigned char*>(detour) - (target + 5)
                : (target + 5) - static_cast<unsigned char*>(detour);
        if (reach >= 0x7F000000LL) {
            ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
            return nullptr;
        }
        trampoline = static_cast<unsigned char*>(alloc_near(target, patch_len + 8));
        if (trampoline == nullptr) {
            ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
            return nullptr;
        }
        for (int i = 0; i < patch_len; i++) {
            trampoline[i] = target[i];
        }
        if (!fixup_relocations(target, trampoline, call_offsets, rip_offsets)) {
            ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
            return nullptr;
        }
        // jmp rel32 back to target+patch_len.
        trampoline[patch_len] = 0xE9;
        const long long back = (target + patch_len) - (trampoline + patch_len + 5);
        *reinterpret_cast<int*>(trampoline + patch_len + 1) = static_cast<int>(back);
    } else {
        void* tramp = build_trampoline(target, patch_len);
        if (tramp == nullptr) {
            ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
            return nullptr;
        }
        trampoline = static_cast<unsigned char*>(tramp);
        if (!fixup_relocations(target, trampoline, call_offsets, rip_offsets)) {
            ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
            return nullptr;
        }
    }

    if (rel_jump) {
        // 5-byte relative jump to the detour (range checked above).
        const int patch_size = 5;
        DWORD old_protect = 0;
        if (!VirtualProtect(target, patch_size, PAGE_EXECUTE_READWRITE, &old_protect)) {
            ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
            return nullptr;
        }

        target[0] = 0xE9;
        const long long rel = static_cast<unsigned char*>(detour) - (target + 5);
        *reinterpret_cast<int*>(target + 1) = static_cast<int>(rel);

        VirtualProtect(target, patch_size, old_protect, &old_protect);
        ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
        return trampoline;
    }

    DWORD old_protect = 0;
    if (!VirtualProtect(target, 14, PAGE_EXECUTE_READWRITE, &old_protect)) {
        ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
        return nullptr;
    }

    target[0] = 0x48;
    target[1] = 0xB8;
    *reinterpret_cast<uint64_t*>(target + 2) = reinterpret_cast<uint64_t>(detour);
    target[10] = 0xFF;
    target[11] = 0xE0;
    target[12] = 0x90;
    target[13] = 0x90;

    VirtualProtect(target, 14, old_protect, &old_protect);
    ReleaseSRWLockExclusive(&detour_toolkit_detail::g_detour_lock);
    return trampoline;
}

void* install_native_detour(const wchar_t* module_name, const char* export_name, void* detour,
                            bool allow_relative_call, bool prefer_near_jump) {
    return install_native_detour_impl(module_name, export_name, detour, allow_relative_call,
                                      prefer_near_jump, true);
}

void* try_install_native_detour(const wchar_t* module_name, const char* export_name,
                                void* detour, bool allow_relative_call,
                                bool prefer_near_jump) {
    return install_native_detour_impl(module_name, export_name, detour, allow_relative_call,
                                      prefer_near_jump, false);
}

}  // namespace nami::tide
