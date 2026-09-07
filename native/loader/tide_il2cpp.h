#pragma once

#include "tide_abi.h"

namespace nami::il2cpp {

/// True when the process hosts IL2CPP (GameAssembly.dll present).
bool detect_il2cpp();

/// Installs the IL2CPP main-thread executor: subclasses the game's main window so ops
/// queued from any thread run on the game's main thread inside its window procedure
/// (the ONLY context verified safe for IL2CPP VM calls: main thread, no runtime_invoke
/// detour frame). Idempotent + thread-safe.
bool install_il2cpp_executor();

/// True when the calling thread is currently executing an IL2CPP op (re-entrancy guard).
bool il2cpp_on_main_thread();

/// Queues an op for the game main thread and blocks until it ran (timeout_ms <= 0 waits
/// forever). Returns true if the work ran.
bool run_il2cpp_op(int (*fn)(void*), void* arg, int timeout_ms = 0);

/// The typed object-op implementation (mirrors Mono tide_object_op against IL2CPP).
int il2cpp_object_op_impl(nami::tide::CallRequest* req);

}  // namespace nami::il2cpp
