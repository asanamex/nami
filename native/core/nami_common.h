#pragma once

#include <cstdint>
#include <string>

namespace nami {

/// Result codes used across the native core.
enum class Status {
    Ok = 0,
    Error = -1,
    Unsupported = -2,
};

/// Version reported by the loader; kept in sync with the managed side manually.
constexpr uint32_t kVersionMajor = 0;
constexpr uint32_t kVersionMinor = 1;
constexpr uint32_t kVersionPatch = 0;

inline std::string version_string() {
    return std::to_string(kVersionMajor) + "." + std::to_string(kVersionMinor) + "." +
           std::to_string(kVersionPatch);
}

}  // namespace nami
