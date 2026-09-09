#pragma once

#include "tide_abi.h"

namespace nami::il2cpp {

/// True when the process hosts IL2CPP (GameAssembly.dll present).
bool detect_il2cpp();

/// Installs the IL2CPP main-thread executor (gated on GameAssembly presence).
/// Prefer install_window_executor for backend-agnostic use.
bool install_il2cpp_executor();

/// Installs the window-proc main-thread executor (no backend gate): subclasses the
/// game's main window so queued work runs on the main thread inside its window
/// procedure (frame boundary - no runtime_invoke on the stack). Shared by the IL2CPP
/// backend and Mono scene-iteration ops. Idempotent + thread-safe. Needs a visible
/// game window (false before the window exists / on headless builds).
bool install_window_executor();

/// True when the calling thread is currently executing an IL2CPP op (re-entrancy guard).
bool il2cpp_on_main_thread();

/// Queues an op for the game main thread and blocks until it ran (timeout_ms <= 0 waits
/// forever). Returns true if the work ran.
bool run_il2cpp_op(int (*fn)(void*), void* arg, int timeout_ms = 0);

/// The typed object-op implementation (mirrors Mono tide_object_op against IL2CPP).
int il2cpp_object_op_impl(nami::tide::CallRequest* req);

}  // namespace nami::il2cpp
