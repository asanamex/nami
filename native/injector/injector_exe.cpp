#include "injector.h"

#include <windows.h>

#include <cstdio>

// Usage: nami_boot <game.exe> <loader.dll> [--root <dir>]
// Launches the game suspended, injects the loader, resumes.
int wmain(int argc, wchar_t** argv) {
    if (argc < 3) {
        std::fwprintf(stderr,
                      L"usage: nami_boot <game.exe> <loader.dll>\n"
                      L"  launches the game suspended, injects the Nami loader, resumes\n");
        return 2;
    }

    const wchar_t* game = argv[1];
    const wchar_t* loader = argv[2];
    const wchar_t* root = nullptr;
    for (int i = 3; i + 1 < argc; ++i) {
        if (wcscmp(argv[i], L"--root") == 0) {
            root = argv[i + 1];
        }
    }

    const auto st = nami::inject_into_game(game, loader, root ? root : L"");
    return st == nami::Status::Ok ? 0 : 1;
}
