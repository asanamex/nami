#pragma once

namespace nami::tide {

/// A unit of Mono work executed by the Tide main-thread drain. Returns 0 on success.
/// The function MUST perform all Mono embedding calls itself (resolve exports, invoke, ...).
using TideWorkFn = int (*)(void* arg);

/// When set on a request, the work runs on the game main thread AFTER the current
/// mono_runtime_invoke returns (outside the nested invoke frame). Required for Unity APIs
/// whose native side is not re-entrant from within a nested runtime_invoke (scene
/// iteration: Object.FindObjectOfType etc.).
enum RequestFlags : int {
    RequestFlag_None = 0,
    RequestFlag_PostInvoke = 1,   // run after the current mono_runtime_invoke returns
};

/// Executes all queued Tide work on the CALLING thread. Called from the mono_runtime_invoke
/// hook, i.e. on the game's main thread. Returns the number of requests executed.
int drain_queue();

/// Installs the mono_runtime_invoke hook so the game main thread drains Tide work.
bool install_main_thread_drain();

/// Queues <paramref name="fn"/> to run on the game's MAIN thread (via the drain hook) and
/// blocks until it completes. Returns true if the work ran (timeout_ms <= 0 waits forever).
/// Re-entrant calls (already on the game main thread, inside a drain) run inline.
/// When <paramref name="flags"/> has RequestFlag_PostInvoke, the work runs AFTER the current
/// mono_runtime_invoke returns (outside the nested frame) — the safe context for Unity
/// scene-iteration APIs.
bool run_on_main_thread(TideWorkFn fn, void* arg, int timeout_ms = 0,
                        int flags = RequestFlag_None);

/// True when the calling thread is currently inside the Tide drain — i.e. the game main
/// thread is running queued work. Lets callers avoid queueing-and-waiting (deadlock).
bool IsTideOnMainThread();

// ---------------------------------------------------------------------------
// Shared detour toolkit (used by the inex legacy lane for its own targets).
// ---------------------------------------------------------------------------

/// Measures whole x64 instructions from `target` until >= min_bytes; 0 = unsafe.
/// With allow_relative_call, near CALLs (E8 rel32) are measured (len 5) and their
/// offsets recorded into call_offsets (up to max_calls) for trampoline fixup;
/// without it (Tide default) any relative control flow refuses, as before.
int measure_relocatable_prologue(const unsigned char* target, int min_bytes,
                                 bool allow_relative_call = false, int* call_offsets = nullptr,
                                 int max_calls = 0);

/// Copies the measured prologue into an executable trampoline ending in a jump
/// back to target+prologue_len; returns its entry (the "original"), or nullptr.
void* build_trampoline(unsigned char* target, int prologue_len);

/// Installs a detour over a native export so <detour> runs instead. Default form is the
/// 14-byte absolute jump (`mov rax,imm64; jmp rax` + NOPs), which needs 14 clean prologue
/// bytes. With prefer_near_jump and only 5+ clean bytes available, a 5-byte relative jump
/// (`E9 rel32`) is used instead, provided both the detour and a near trampoline are within
/// ±2GB of the target. Returns the trampoline holding the original bytes (call through it
/// for the original behavior), or nullptr on any failure.
/// Thread-safe; at most one install per process per target is the caller's discipline.
/// With allow_relative_call, near CALLs in the prologue are relocated with adjusted
/// rel32 targets (needed for MSVC-built exports like mono_jit_init_version); without
/// it any relative control flow refuses, exactly as before.
void* install_native_detour(const wchar_t* module_name, const char* export_name, void* detour,
                            bool allow_relative_call = false, bool prefer_near_jump = false);

/// Non-blocking variant: returns nullptr immediately when the toolkit lock is
/// contended instead of waiting. For loader-lock contexts (LdrDllNotification
/// callbacks) where blocking could deadlock against a thread that holds the
/// toolkit lock while waiting on the loader lock. A nullptr here means
/// "contended OR failed" — the caller retries from a normal thread later.
void* try_install_native_detour(const wchar_t* module_name, const char* export_name,
                                void* detour, bool allow_relative_call = false,
                                bool prefer_near_jump = false);

}  // namespace nami::tide
