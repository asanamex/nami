#pragma once

#include "nami_common.h"

#include <functional>
#include <string>

namespace nami {

/// Manages the embedded .NET (CoreCLR) runtime loaded into the game process
/// through hostfxr. Nami brings its OWN modern runtime — this is the core
/// architectural difference from BepInEx-Mono, where plugins run on the game's
/// ancient embedded Mono.
///
/// Layout expected next to the native core (the "nami root"):
///   dotnet/host/fxr/<ver>/hostfxr.dll
///   Nami.Runtime.dll  (+ Nami.Core.dll, Nami.Sdk.dll)
class RuntimeHost {
  public:
    /// Initializes hostfxr and loads the Nami.Runtime assembly.
    Status initialize(const std::string& nami_root);
    /// Calls Nami.Runtime.Boot.Run(namiRoot, monoModule) and blocks forever.
    Status run_boot();

  private:
    std::string nami_root_;
    void* hostfxr_handle_ = nullptr;
    void* boot_delegate_ = nullptr;
};

}  // namespace nami
