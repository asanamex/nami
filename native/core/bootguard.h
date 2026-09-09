#pragma once

#include <cstdint>
#include <string>

namespace nami::bootguard {

// ---------------------------------------------------------------------------
// Boot-guard safe mode: contain native failures of the loader so the game always
// boots, and auto-recover afterwards.
//
// Marker files (in the nami root):
//   boot-pending   written at boot start (with the current stage), deleted once the
//                  managed runtime is up and ticking. Its presence means "a Nami boot
//                  is in progress" - a crash while it exists marks the NEXT boot safe.
//   safe-mode      written by the crash handler: reason + boots_remaining. While it
//                  exists, the loader skips every native stage (inex arm, runtime
//                  wait, CoreCLR hosting) and the game boots unmodded. Each clean
//                  boot consumes one count; at 0 the marker is deleted and Nami is
//                  fully back. Delete the file manually for an immediate full boot.
//   nami-crash.log append-only crash records (stage, code, rip, address, thread).
// ---------------------------------------------------------------------------

enum Stage : int {
    Stage_None = 0,
    Stage_InexArm,
    Stage_WaitRuntime,
    Stage_HostCoreClr,
    Stage_ManagedBoot,
    Stage_Complete,  // managed runtime is up; native boot phase is over
};

// True when <root>/safe-mode exists with boots_remaining > 0.
bool SafeModeEnabled(const std::string& root_utf8);

// Writes/refreshes <root>/boot-pending with the current stage. The stage is also
// kept in memory for the crash handler's fast path. Returns false on I/O failure.
bool MarkBootStart(const std::string& root_utf8, Stage stage);
void MarkStage(const std::string& root_utf8, Stage stage);

// Deletes <root>/boot-pending. Called by the managed runtime once the update loop is
// ticking (see Nami.Runtime.Boot.Run) - the native boot phase is over from then on.
void MarkBootComplete(const std::string& root_utf8);

// Consumes one safe-mode boot: reads boots_remaining, decrements it, deletes the
// marker at 0 (so THIS boot runs fully normal). Returns the previous count.
int ConsumeSafeModeBoot(const std::string& root_utf8);

// Appends a crash record to <root>/nami-crash.log and (re)writes <root>/safe-mode
// with the given reason. Best-effort file I/O; safe to call from the crash handler.
void NoteCrash(const std::string& root_utf8, const char* stage, const char* reason,
               uint64_t rip, uint64_t address, bool owned_thread);

// Installs the process-wide vectored exception handler. `on_contained` is invoked
// right before a Nami-owned thread is terminated so the injector's hook-ready wait
// can be released (the game resumes immediately instead of stalling 30 s).
// Idempotent; call once from the loader boot thread before any risky work.
void InstallCrashHandler(const std::string& root_utf8, void (*on_contained)());

// Thread ownership: faults on threads marked as Nami-owned are CONTAINED (the
// loader thread dies, the game lives). Faults on other threads are logged (and mark
// the next boot safe while boot-pending exists) but not contained - the process
// dies normally with diagnostics on disk.
void MarkThreadOwned(bool owned);
bool IsNamiThread();

}  // namespace nami::bootguard