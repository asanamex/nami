#pragma once

#include <string>

namespace nami::inex {

// Signals the injector's per-pid hook-ready event (Local\NamiHookReady-<pid>).
// Fired exactly once per arm() in EVERY path - hook installed, refused, or lane
// skipped - so the injector's pre-resume wait can never wedge game boot.
// Also callable directly for paths that skip arm() (IL2CPP, no root).
void signal_hook_ready();

// Arms the legacy lane (nami-inex) for Mono titles. Non-blocking: returns immediately
// after setup; all game-thread work happens on the game's own threads.
//
// Call EARLY (before the game main thread is resumed): install_jit_hook only wins
// its race against mono_jit_init when the main thread is still suspended.
//
// When <nami_root>/inex/BepInEx/core/BepInEx.Preloader.dll AND <nami_root>/inex/enabled
// both exist: sets the DOORSTOP_* environment BepInEx 5.4's preloader reads, installs a
// mono_jit_init detour so Doorstop.Entrypoint.Start runs at Doorstop timing (before any
// managed code, on the game main thread), and spawns a watcher thread that covers the
// fallback path (drain-based Start if the detour missed) plus the chainloader kick once
// a scene is live (late Start misses the one-shot entrypoint patch - see the .cpp).
//
// Return codes: 0 = no payload staged (pure Nami install, silent), 1 = payload staged
// but not enabled (sentinel missing), 2 = armed. Never throws. Mono titles only;
// the caller gates IL2CPP out (BepInEx 6 needs its own CoreCLR lane - later).
int arm(const std::string& nami_root_utf8);

}  // namespace nami::inex
