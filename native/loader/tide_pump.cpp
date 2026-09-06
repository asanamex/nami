#include "tide_pump.h"

#include <windows.h>

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
// queued and executed INLINE on the game's main thread by hooking mono_runtime_invoke —
// a function the game's main thread calls constantly. The caller (CoreCLR) blocks on a
// per-request event until the main thread has run the op.
// ---------------------------------------------------------------------------

namespace nami::tide {

namespace {

struct Request {
    TideWorkFn fn;
    void* arg;
    HANDLE done;
    Request* next;
};

struct QueueState {
    CRITICAL_SECTION lock;
    Request* head;
    Request* tail;
    volatile LONG work_pending;  // fast-path: set when a request is queued
};

QueueState g_queue{};
bool g_queue_initialized = false;
bool g_drain_installed = false;

using mono_runtime_invoke_fn = void* (*)(void*, void*, void**, void**);
mono_runtime_invoke_fn g_original_runtime_invoke = nullptr;
unsigned char* g_trampoline = nullptr;  // holds the original prologue + jump back

// The detour installed over mono_runtime_invoke: drain Tide work, then run the original.
void* __stdcall runtime_invoke_detour(void* method, void* obj, void** args, void** exc) {
    // Fast path: nothing queued → skip the lock entirely (this runs on the game's hottest
    // path — every managed invocation).
    if (InterlockedCompareExchange(&g_queue.work_pending, 0, 0) != 0) {
        drain_queue();
    }

    return g_original_runtime_invoke(method, obj, args, exc);
}

// ---------------------------------------------------------------------------
// Minimal x64 instruction-length decoder for trampoline building. Only needs to measure
// instructions until the prologue covers >= 14 bytes; refuses (returns 0) on anything it
// cannot measure safely (branches, RIP-relative, VEX/EVEX, 3-byte escapes).
// ---------------------------------------------------------------------------

// Returns the total length of whole instructions from `p` until >= min_bytes, or 0 if any
// instruction is unmeasurable/unsafe to relocate. Scans at most 32 bytes.
int measure_relocatable_prologue(const unsigned char* p, int min_bytes) {
    int off = 0;
    while (off < min_bytes) {
        const unsigned char* q = p + off;
        int i = 0;
        bool rex = false;
        int rex_val = 0;

        // Consume legacy + REX prefixes.
        while (i < 15) {
            unsigned char b = q[i];
            if (b == 0xF0 || b == 0xF2 || b == 0xF3 || b == 0x2E || b == 0x36 || b == 0x3E ||
                b == 0x26 || b == 0x64 || b == 0x65 || b == 0x66 || b == 0x67) {
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

        const unsigned char op = q[i];
        // Refuse: VEX/EVEX/XOP, relative branches, ret, int3, ud2.
        if (op == 0xC4 || op == 0xC5 || op == 0x62 || op == 0x63 || op == 0x8F || op == 0xE8 ||
            op == 0xE9 || op == 0xEB || op == 0xC3 || op == 0xCC || (op == 0x0F && i + 1 < 15)) {
            return 0;
        }

        // One-byte opcode with no ModRM.
        bool no_modrm = false;
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

        int len = i + 1;
        if (!no_modrm) {
            if (len >= 15) {
                return 0;
            }
            unsigned char modrm = q[len];
            int mod = modrm >> 6;
            int rm = modrm & 7;
            len++;
            if (rm == 4 && mod != 3) {
                // SIB
                if (len >= 15) {
                    return 0;
                }
                unsigned char sib = q[len];
                len++;
                if (mod == 0 && (sib & 7) == 5) {
                    len += 4;  // disp32
                }
            }
            if (mod == 0 && rm == 5) {
                return 0;  // RIP-relative: not relocatable by simple copy
            }
            if (mod == 1) {
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
        if (!no_modrm) {
            unsigned char modrm = q[i + 1];
            int reg = (modrm >> 3) & 7;
            if ((op == 0x80 || op == 0x82) && reg <= 4) len += 1;
            else if (op == 0x83 && reg <= 4) len += 1;
            else if (op == 0x81 && reg <= 4) len += (rex && (rex_val & 0x08)) ? 8 : 4;
            else if (op == 0xC6 && reg == 0) len += 1;
            else if (op == 0xC7 && reg == 0) len += (rex && (rex_val & 0x08)) ? 8 : 4;
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

// Builds a trampoline: copies the measured original prologue and appends a jump back to
// target+prologue_len. Returns the trampoline entry (the "original" to call).
void* build_trampoline(unsigned char* target, int prologue_len) {
    auto* tramp = static_cast<unsigned char*>(
        VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
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

}  // namespace

// Executes all queued Tide work on the calling thread. Called from the detour, i.e. on the
// game's main thread. Returns the number of requests executed.
int drain_queue() {
    int executed = 0;
    for (;;) {
        EnterCriticalSection(&g_queue.lock);
        Request* req = g_queue.head;
        if (req != nullptr) {
            g_queue.head = req->next;
            if (g_queue.head == nullptr) {
                g_queue.tail = nullptr;
            }
        }
        if (g_queue.head == nullptr) {
            InterlockedExchange(&g_queue.work_pending, 0);
        }
        LeaveCriticalSection(&g_queue.lock);

        if (req == nullptr) {
            break;
        }

        if (req->fn != nullptr) {
            req->fn(req->arg);
        }

        if (req->done != nullptr) {
            SetEvent(req->done);
        }

        delete req;
        executed++;
    }

    return executed;
}

// Installs the mono_runtime_invoke hook so the game main thread drains Tide work.
bool install_main_thread_drain() {
    if (g_drain_installed) {
        return true;
    }

    InitializeCriticalSection(&g_queue.lock);
    g_queue_initialized = true;

    const HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
    if (mono == nullptr) {
        return false;
    }

    void* target = reinterpret_cast<void*>(GetProcAddress(mono, "mono_runtime_invoke"));
    if (target == nullptr) {
        return false;
    }

    auto* p = static_cast<unsigned char*>(target);

    // Measure the relocatable prologue (whole instructions covering >= 14 bytes).
    const int prologue_len = measure_relocatable_prologue(p, 14);
    if (prologue_len <= 0) {
        return false;
    }

    // Trampoline: original prologue bytes + jump back to target+prologue_len. The "original"
    // the detour calls is the trampoline, NOT the patched address (which would recurse).
    g_trampoline = static_cast<unsigned char*>(build_trampoline(p, prologue_len));
    if (g_trampoline == nullptr) {
        return false;
    }

    g_original_runtime_invoke = reinterpret_cast<mono_runtime_invoke_fn>(g_trampoline);

    // Write a 14-byte absolute jump to the detour: mov rax, imm64; jmp rax.
    DWORD old_protect = 0;
    if (!VirtualProtect(p, 14, PAGE_EXECUTE_READWRITE, &old_protect)) {
        return false;
    }

    p[0] = 0x48; p[1] = 0xB8;
    *reinterpret_cast<uint64_t*>(p + 2) = reinterpret_cast<uint64_t>(&runtime_invoke_detour);
    p[10] = 0xFF; p[11] = 0xE0;
    p[12] = 0x90; p[13] = 0x90;

    VirtualProtect(p, 14, old_protect, &old_protect);
    g_drain_installed = true;
    return true;
}

// Queues work for the game main thread and blocks until it has run (timeout_ms <= 0 = wait
// forever). The request is heap-allocated and freed by the drain after signaling.
bool run_on_main_thread(TideWorkFn fn, void* arg, int timeout_ms) {
    if (!g_drain_installed) {
        if (!install_main_thread_drain()) {
            return false;
        }
    }

    auto* req = new (std::nothrow) Request{};
    if (req == nullptr) {
        return false;
    }

    req->fn = fn;
    req->arg = arg;
    req->done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    req->next = nullptr;

    EnterCriticalSection(&g_queue.lock);
    if (g_queue.tail != nullptr) {
        g_queue.tail->next = req;
        g_queue.tail = req;
    } else {
        g_queue.head = req;
        g_queue.tail = req;
    }
    InterlockedExchange(&g_queue.work_pending, 1);
    LeaveCriticalSection(&g_queue.lock);

    // The hook is on mono_runtime_invoke: the game main thread calls it constantly, so the
    // drain runs soon. Wait for completion.
    const DWORD wait = timeout_ms <= 0 ? INFINITE : static_cast<DWORD>(timeout_ms);
    const DWORD result = WaitForSingleObject(req->done, wait);
    CloseHandle(req->done);

    return result == WAIT_OBJECT_0;
}

}  // namespace nami::tide
