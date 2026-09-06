#include "runtime_host.h"

#include <windows.h>

#include <filesystem>
#include <cstdio>
#include <cstring>

namespace fs = std::filesystem;

namespace nami {

namespace {

// --- hostfxr API surface (subset of hostfxr.h) ---
using hostfxr_initialize_for_runtime_config_fn = int(WINAPI*)(const wchar_t*, void*, void**);
using hostfxr_get_runtime_delegate_fn = int(WINAPI*)(void*, int, void**);
using hostfxr_close_fn = int(WINAPI*)(void*);

// From hostfxr.h: enum hostfxr_delegate_type {
//   hdt_com_activation=0, hdt_load_in_memory_assembly=1, hdt_winrt_activation=2,
//   hdt_com_register=3, hdt_com_unregister=4, hdt_load_assembly_and_get_function_pointer=5,
//   hdt_get_function_pointer=6, hdt_load_assembly=7, hdt_load_assembly_bytes=8 }
constexpr int hdt_load_assembly_and_get_function_pointer = 5;

// Entry point signature exposed by Nami.Runtime's ComponentEntry:
//   [UnmanagedCallersOnly] int Nami_ComponentEntryPoint(void* arg, int sizeBytes)
using component_entry_point_fn = int(WINAPI*)(void*, int);

// Helper returned by hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer).
// load_assembly_and_get_function_pointer_fn:
//   int (assembly_path, type_name, method_name, delegate_type_name, reserved, &delegate)
using load_asm_and_get_fp_fn = int(WINAPI*)(const wchar_t*, const wchar_t*, const wchar_t*,
                                            const wchar_t*, void*, void**);

// Argument blob layout must match Nami.Runtime.ComponentEntry.BootArgs.
struct BootArgs {
    wchar_t nami_root[260];  // MAX_PATH
    void* mono_module;
};

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring out(static_cast<size_t>(len), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), out.data(), len);
    return out;
}

// Logs a UTF-8 message to <nami root>/nami-host.log (append). Used only for
// diagnosing hostfxr failures before the managed runtime is up.
void host_log(const std::string& root, const std::string& msg) {
    const std::string path = root + "\\nami-host.log";
    FILE* f = nullptr;
    if (fopen_s(&f, path.c_str(), "a") == 0 && f != nullptr) {
        std::fprintf(f, "%s\n", msg.c_str());
        std::fclose(f);
    }
}

}  // namespace

Status RuntimeHost::initialize(const std::string& nami_root) {
    nami_root_ = nami_root;
    host_log(nami_root_, "[host] RuntimeHost.initialize");

    // Locate hostfxr.dll under <root>/dotnet/host/fxr/<version>/hostfxr.dll
    const fs::path fxr_dir = fs::path(nami_root_) / "dotnet" / "host" / "fxr";
    std::error_code ec;
    if (!fs::exists(fxr_dir, ec)) {
        host_log(nami_root_, "[host] fxr dir missing: " + fxr_dir.string());
        return Status::Error;
    }

    fs::path hostfxr_path;
    for (const auto& entry : fs::directory_iterator(fxr_dir, ec)) {
        if (entry.is_directory()) {
            const auto candidate = entry.path() / "hostfxr.dll";
            if (fs::exists(candidate, ec)) {
                hostfxr_path = candidate;
                break;
            }
        }
    }

    if (hostfxr_path.empty()) {
        host_log(nami_root_, "[host] no hostfxr.dll found under " + fxr_dir.string());
        return Status::Error;
    }

    host_log(nami_root_, "[host] hostfxr.dll at " + hostfxr_path.string());
    hostfxr_handle_ = LoadLibraryW(hostfxr_path.wstring().c_str());
    if (hostfxr_handle_ == nullptr) {
        host_log(nami_root_, "[host] LoadLibraryW(hostfxr) failed: " +
                                 std::to_string(GetLastError()));
        return Status::Error;
    }
    host_log(nami_root_, "[host] hostfxr loaded OK");
    return Status::Ok;
}

Status RuntimeHost::run_boot() {
    host_log(nami_root_, "[host] run_boot enter");
    if (hostfxr_handle_ == nullptr) {
        host_log(nami_root_, "[host] run_boot: no hostfxr handle");
        return Status::Error;
    }

    const HMODULE fxr = static_cast<HMODULE>(hostfxr_handle_);
    const auto init_fxr = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        GetProcAddress(fxr, "hostfxr_initialize_for_runtime_config"));
    const auto get_delegate = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        GetProcAddress(fxr, "hostfxr_get_runtime_delegate"));
    const auto close_fxr = reinterpret_cast<hostfxr_close_fn>(
        GetProcAddress(fxr, "hostfxr_close"));
    if (init_fxr == nullptr || get_delegate == nullptr || close_fxr == nullptr) {
        host_log(nami_root_, "[host] GetProcAddress failed for hostfxr exports");
        return Status::Error;
    }
    host_log(nami_root_, "[host] hostfxr exports resolved");

    // Nami.Runtime.runtimeconfig.json sits in the nami root.
    const fs::path runtimeconfig = fs::path(nami_root_) / "Nami.Runtime.runtimeconfig.json";
    if (!fs::exists(runtimeconfig)) {
        host_log(nami_root_, "[host] runtimeconfig missing");
        return Status::Error;
    }

    void* ctx = nullptr;
    const int rc_init = init_fxr(runtimeconfig.c_str(), nullptr, &ctx);
    host_log(nami_root_, "[host] hostfxr_initialize_for_runtime_config rc=" +
                             std::to_string(rc_init));
    if (rc_init != 0 || ctx == nullptr) {
        if (ctx != nullptr) {
            close_fxr(ctx);
        }
        return Status::Error;
    }

    void* delegate_ptr = nullptr;
    const int rc_delegate =
        get_delegate(ctx, hdt_load_assembly_and_get_function_pointer, &delegate_ptr);
    host_log(nami_root_, "[host] hostfxr_get_runtime_delegate(load_asm) rc=" +
                             std::to_string(rc_delegate));
    if (rc_delegate != 0 || delegate_ptr == nullptr) {
        close_fxr(ctx);
        return Status::Error;
    }

    const auto load_asm_and_get_fp = reinterpret_cast<load_asm_and_get_fp_fn>(delegate_ptr);

    // Resolve Nami.Runtime.ComponentEntry.EntryPoint ([UnmanagedCallersOnly]).
    // delegate_type_name must be the UNMANAGEDCALLERSONLY_METHOD sentinel: (const char_t*)-1,
    // NOT a literal string (a literal string is resolved as a delegate type name → TypeLoad).
    const std::wstring runtime_dll = widen((fs::path(nami_root_) / "Nami.Runtime.dll").string());
    constexpr wchar_t kTypeName[] = L"Nami.Runtime.ComponentEntry, Nami.Runtime";
    constexpr wchar_t kMethodName[] = L"EntryPoint";
    const auto kDelegateType = reinterpret_cast<const wchar_t*>(static_cast<intptr_t>(-1));

    void* entry_ptr = nullptr;
    const int rc_load = load_asm_and_get_fp(runtime_dll.c_str(), kTypeName, kMethodName,
                                            kDelegateType, nullptr, &entry_ptr);
    host_log(nami_root_, "[host] load_assembly_and_get_function_pointer rc=" +
                             std::to_string(rc_load));
    if (rc_load != 0 || entry_ptr == nullptr) {
        close_fxr(ctx);
        return Status::Error;
    }

    const auto component_entry = reinterpret_cast<component_entry_point_fn>(entry_ptr);

    BootArgs args{};
    const std::wstring root_wide = widen(nami_root_);
    wcsncpy_s(args.nami_root, root_wide.c_str(), root_wide.size());
    args.mono_module = static_cast<void*>(GetModuleHandleW(L"mono-2.0-bdwgc.dll"));

    host_log(nami_root_, "[host] invoking component entry");
    const int rc = component_entry(&args, static_cast<int>(sizeof(args)));
    host_log(nami_root_, "[host] component entry returned rc=" + std::to_string(rc));
    close_fxr(ctx);
    return rc == 0 ? Status::Ok : Status::Error;
}

}  // namespace nami
