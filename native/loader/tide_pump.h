#pragma once

namespace nami::tide {

/// A unit of Mono work executed by the Tide main-thread drain. Returns 0 on success.
/// The function MUST perform all Mono embedding calls itself (resolve exports, invoke, ...).
using TideWorkFn = int (*)(void* arg);

/// Executes all queued Tide work on the CALLING thread. Called from the mono_runtime_invoke
/// hook, i.e. on the game's main thread. Returns the number of requests executed.
int drain_queue();

/// Installs the mono_runtime_invoke hook so the game main thread drains Tide work.
bool install_main_thread_drain();

/// Queues <paramref name="fn"/> to run on the game's MAIN thread (via the drain hook) and
/// blocks until it completes. Returns true if the work ran (timeout_ms <= 0 waits forever).
/// Re-entrant calls (already on the game main thread, inside a drain) run inline.
bool run_on_main_thread(TideWorkFn fn, void* arg, int timeout_ms = 0);

/// True when the calling thread is currently inside the Tide drain — i.e. the game main
/// thread is running queued work. Lets callers avoid queueing-and-waiting (deadlock).
bool IsTideOnMainThread();

}  // namespace nami::tide
