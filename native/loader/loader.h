#pragma once

#include "nami_common.h"

namespace nami {

/// Entry point of the native loader. Called by the remote-injected stub thread
/// AFTER the loader DLL is loaded (so the loader lock is free).
/// Blocks for the game's Mono runtime to come up, then hosts the Nami CoreCLR
/// runtime (RuntimeHost) which calls back into managed Nami.Runtime.Boot.Run.
/// This function never returns.
void loader_main(const wchar_t* nami_root);

/// Polls for mono-2.0-bdwgc.dll / mono.dll to be loaded in this process
/// (i.e. Unity's Mono runtime initialized), up to ~30s.
bool wait_for_mono(int timeout_ms = 30'000);

}  // namespace nami

/// Exported for the remote stub: runs the loader on the calling (remote) thread.
extern "C" __declspec(dllexport) void nami_loader_start();
