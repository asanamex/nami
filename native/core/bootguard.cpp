// Boot-guard: crash containment + safe-mode for the native loader (see bootguard.h).

#include "bootguard.h"

#include <windows.h>

#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace nami::bootguard {

namespace {

constexpr const char* kPendingName = "boot-pending";
constexpr const char* kSafeModeName = "safe-mode";
constexpr const char* kCrashLogName = "nami-crash.log";
constexpr int kSafeBootsRemaining = 3;  // clean boots until safe mode auto-clears

const char* StageName(Stage s) {
    switch (s) {
        case Stage_InexArm: return "inex_arm";
        case Stage_WaitRuntime: return "wait_runtime";
        case Stage_HostCoreClr: return "host_coreclr";
        case Stage_ManagedBoot: return "managed_boot";
        case Stage_Complete: return "complete";
        default: return "none";
    }
}

// ---- in-memory state (crash handler fast path; no I/O to decide) ----

volatile LONG g_handler_installed = 0;
volatile LONG g_in_handler = 0;
std::string g_root_utf8;
void (*g_on_contained)() = nullptr;
volatile Stage g_stage = Stage_None;
thread_local bool g_thread_owned = false;

// ---- small file helpers (best-effort, safe enough for the fault path) ----

fs::path RootPath(const std::string& root_utf8) {
    // The root comes in as UTF-8; convert to the native wide form so non-ASCII
    // game paths work on Windows.
    if (root_utf8.empty()) {
        return {};
    }
    const int len = MultiByteToWideChar(CP_UTF8, 0, root_utf8.c_str(), -1, nullptr, 0);
    if (len <= 0) {
        return {};
    }
    std::wstring wide(static_cast<size_t>(len - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, root_utf8.c_str(), -1, wide.data(), len);
    return fs::path(wide);
}

bool ReadFirstLines(const fs::path& p, std::vector<std::string>& out, int max_lines) {
    std::ifstream in(p, std::ios::binary);
    if (!in) {
        return false;
    }
    std::string line;
    while (out.size() < static_cast<size_t>(max_lines) && std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        out.push_back(line);
    }
    return !out.empty();
}

void WriteFile(const fs::path& p, const std::string& contents) {
    std::ofstream out(p, std::ios::binary | std::ios::trunc);
    if (out) {
        out.write(contents.data(), static_cast<std::streamsize>(contents.size()));
    }
}

void AppendFile(const fs::path& p, const std::string& contents) {
    std::ofstream out(p, std::ios::binary | std::ios::app);
    if (out) {
        out.write(contents.data(), static_cast<std::streamsize>(contents.size()));
    }
}

std::string UtcTimestamp() {
    using namespace std::chrono;
    const auto now = system_clock::now();
    const auto t = system_clock::to_time_t(now);
    struct tm tmv{};
    localtime_s(&tmv, &t);
    char buf[32]{};
    std::strftime(buf, sizeof(buf), "%Y-%m-%dT%H:%M:%S", &tmv);
    return buf;
}

// ---- crash handler ----

LONG WINAPI CrashHandler(EXCEPTION_POINTERS* ep) {
    if (InterlockedExchange(&g_in_handler, 1) != 0) {
        return EXCEPTION_CONTINUE_SEARCH;  // re-entrant fault (e.g. in our own I/O): give up
    }

    const DWORD code = ep->ExceptionRecord->ExceptionCode;
    const bool owned = IsNamiThread();
    const Stage stage = static_cast<Stage>(g_stage);

    // Only hard faults interest us; benign exceptions (breakpoints, debugger) pass through.
    const bool hard_fault =
        code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_ILLEGAL_INSTRUCTION ||
        code == EXCEPTION_STACK_OVERFLOW || code == EXCEPTION_INT_DIVIDE_BY_ZERO ||
        code == EXCEPTION_PRIV_INSTRUCTION || code == EXCEPTION_ARRAY_BOUNDS_EXCEEDED ||
        (code & 0x80000000) != 0;

    if (hard_fault) {
        uint64_t address = 0;
        if (code == EXCEPTION_ACCESS_VIOLATION && ep->ExceptionRecord->NumberParameters >= 2) {
            address = static_cast<uint64_t>(ep->ExceptionRecord->ExceptionInformation[1]);
        }
        const uint64_t rip = ep->ContextRecord
            ? static_cast<uint64_t>(ep->ContextRecord->Rip)
            : 0;

        char reason[64]{};
        std::snprintf(reason, sizeof(reason), "0x%08lX", static_cast<unsigned long>(code));

        // Log + mark the next boot safe. Written for owned threads always, and for
        // any thread while the native boot phase is still in progress.
        if (owned || stage != Stage_Complete) {
            NoteCrash(g_root_utf8, StageName(stage), reason, rip, address, owned);
        }

        if (owned) {
            // Contain: release the injector's hook-ready wait, then kill ONLY this
            // thread. The game main thread is still suspended by the injector until
            // hook-ready — after this it resumes and the game boots unmodded.
            if (g_on_contained != nullptr) {
                g_on_contained();
            }
            TerminateThread(GetCurrentThread(), 0xBADF00D);
        }
    }

    InterlockedExchange(&g_in_handler, 0);
    return EXCEPTION_CONTINUE_SEARCH;  // uncontained faults die normally, with evidence
}

}  // namespace

bool SafeModeEnabled(const std::string& root_utf8) {
    const fs::path p = RootPath(root_utf8) / kSafeModeName;
    if (!fs::exists(p)) {
        return false;
    }
    std::vector<std::string> lines;
    if (!ReadFirstLines(p, lines, 2) || lines.size() < 2) {
        return false;
    }
    return std::atoi(lines[1].c_str()) > 0;
}

bool MarkBootStart(const std::string& root_utf8, Stage stage) {
    g_stage = stage;
    const fs::path p = RootPath(root_utf8) / kPendingName;
    WriteFile(p, std::string("stage=") + StageName(stage) + "\n");
    return fs::exists(p);
}

void MarkStage(const std::string& root_utf8, Stage stage) {
    g_stage = stage;
    const fs::path p = RootPath(root_utf8) / kPendingName;
    WriteFile(p, std::string("stage=") + StageName(stage) + "\n");
}

void MarkBootComplete(const std::string& root_utf8) {
    g_stage = Stage_Complete;
    std::error_code ec;
    fs::remove(RootPath(root_utf8) / kPendingName, ec);
}

int ConsumeSafeModeBoot(const std::string& root_utf8) {
    const fs::path p = RootPath(root_utf8) / kSafeModeName;
    int remaining = 0;
    std::vector<std::string> lines;
    if (ReadFirstLines(p, lines, 2) && lines.size() >= 2) {
        remaining = std::atoi(lines[1].c_str());
    }
    remaining--;
    if (remaining <= 0) {
        std::error_code ec;
        fs::remove(p, ec);
    } else {
        std::string contents = lines.empty() ? std::string("boot-guard safe mode\n") : lines[0] + "\n";
        contents += std::to_string(remaining) + "\n";
        WriteFile(p, contents);
    }
    return remaining + 1;  // previous count
}

void NoteCrash(const std::string& root_utf8, const char* stage, const char* reason,
               uint64_t rip, uint64_t address, bool owned_thread) {
    const fs::path root = RootPath(root_utf8);
    if (root.empty()) {
        return;
    }

    char line[512]{};
    std::snprintf(line, sizeof(line),
                  "[%s] stage=%s code=%s rip=0x%llX addr=0x%llX tid=%lu owned=%d\n",
                  UtcTimestamp().c_str(), stage != nullptr ? stage : "?",
                  reason != nullptr ? reason : "?", static_cast<unsigned long long>(rip),
                  static_cast<unsigned long long>(address),
                  static_cast<unsigned long>(GetCurrentThreadId()), owned_thread ? 1 : 0);
    AppendFile(root / kCrashLogName, line);

    // Refresh the safe-mode marker (keep the newest reason; reset the count so the
    // game gets the full recovery window after repeated crashes).
    const fs::path safe = root / kSafeModeName;
    WriteFile(safe, std::string("reason: ") + (reason != nullptr ? reason : "?") + "\n" +
                        std::to_string(kSafeBootsRemaining) + "\n");
}

void InstallCrashHandler(const std::string& root_utf8, void (*on_contained)()) {
    if (InterlockedExchange(&g_handler_installed, 1) != 0) {
        return;
    }
    g_root_utf8 = root_utf8;
    g_on_contained = on_contained;
    AddVectoredExceptionHandler(1 /* first chance */, CrashHandler);
}

void MarkThreadOwned(bool owned) {
    g_thread_owned = owned;
}

bool IsNamiThread() {
    return g_thread_owned;
}

}  // namespace nami::bootguard