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
int measure_relocatable_prologue(const unsigned char* target, int min_bytes);

/// Copies the measured prologue into an executable trampoline ending in a jump
/// back to target+prologue_len; returns its entry (the "original"), or nullptr.
void* build_trampoline(unsigned char* target, int prologue_len);

/// Installs a 14-byte absolute-jump detour (`mov rax,imm64; jmp rax` + NOPs) over a
/// native export so <detour> runs instead. Returns the trampoline holding the original
/// bytes (call through it for the original behavior), or nullptr on any failure.
/// Thread-safe; at most one install per process per target is the caller's discipline.
void* install_native_detour(const wchar_t* module_name, const char* export_name, void* detour);

}  // namespace nami::tide
