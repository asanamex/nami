#pragma once

#include "nami_common.h"

namespace nami {

/// Launches the target Unity game suspended, injects the Nami loader DLL by
/// writing a LoadLibraryW stub into the remote process, and resumes it.
/// Returns Ok on success (injection accepted; loader runs async).
Status inject_into_game(const wchar_t* game_exe, const wchar_t* loader_dll_path,
                        const wchar_t* nami_root);

}  // namespace nami
