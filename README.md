# Nami

**Nami** is a mod loader for Unity games (Mono and IL2CPP, Windows x64), written from
scratch. Nami core has no build dependency on BepInEx, HarmonyX, MonoMod, or Mono.Cecil;
an optional lane (`nami inex`) boots real BepInEx 5.x for legacy mods (see below).

> **Status: beta.** Loading, patching, the game bridge, and typed game access work in
> real games on Mono and IL2CPP (four Mono titles across 2022.3 and Unity 6, plus one
> Unity 6 IL2CPP title). The verified matrix is still small - help testing on more
> titles is welcome (see below).

## Getting started

- **[TUTORIAL.md](TUTORIAL.md)** - build Nami, stage it next to a game, write and run your first mod.
- **[docs/tide.md](docs/tide.md)** - Tide: the Nami-to-game bridge (mods call into Unity Mono or IL2CPP from .NET 10).
- **[docs/wave.md](docs/wave.md)** - Wave: the patching engine (inline detours, IL-copy patching, overhead numbers).
- **[docs/technical-difference.md](docs/technical-difference.md)** - point-by-point technical comparison with BepInEx.
- **[docs/vs-bepinex.md](docs/vs-bepinex.md)** - short read on how Nami differs and what is missing.
- **[docs/architecture.md](docs/architecture.md)** - how the loader, hosting, and bridges fit together.

## What Nami does

- **One modern .NET runtime** (net10.0) hosted inside the game process on both Mono and
  IL2CPP titles, so plugins don't run on the game's bundled runtime.
- **Dev-time type projection** instead of preloading interop assemblies: `nami interop
  generate` emits typed accessors from `global-metadata.dat`; classes resolve lazily
  (once per process), member lookups are memoized in the native loader, and grouped ops
  go through one main-thread round trip (`TideBatch`).
- **Interop dumping is offline dev tooling**, never a first-launch cost.
- **Per-mod isolation and crash quarantine.** A throwing mod disables itself; the game
  keeps running. A **boot-guard** contains native loader crashes so the game still boots
  (auto-recovering safe mode; see docs/architecture.md). Mods **hot-reload live**: rebuild
  or drop a top-level DLL into `nami/mods` and it swaps into a new generation without
  restarting the game (unloadable ALCs + a file watcher + mod files loaded without
  file locks).
- **Built-in per-mod profiler.** Every plugin gets tick timings (avg/p95/max) and a periodic
  summary in the log - available to mods in-process via `Context.Profiler`.
- **Measured.** `bench/` tracks loader and patching overhead with regression gates
  (`NAMI_GATE_*` budgets, nonzero exit on breach).

## Repository layout

```
.github/     CI (managed + native jobs)
native/      C++17: injector (nami_boot), in-game loader (nami_loader), hostfxr hosting
             (core/), Tide main-thread drain + object ops, nami-inex legacy bootstrap
             (loader/inex_bootstrap.h/.cpp), smoke/ toolchain self-test
src/
  Nami.Sdk/        Public plugin API (what mods reference)
  Nami.Core/       Chainloader: discovery, graph, ALCs, quarantine, hot reload (generations +
                   file watcher), per-mod profiler (Profiling/ModProfiler.cs)
  Nami.Runtime/    In-game managed bootstrap: Boot.Run
  Nami.Tide/       Typed game access: Tide, GameClass, GameObject, TideValue, TideBatch
  Nami.Wave/       Patching engine: x64 detours + Harmony-style IL-copy prefix/postfix
  Nami.Cli/        nami command-line tool (install/pack/launch/create/run/doctor/list/interop/inex/nmod/version/help)
  Nami.Interop/    Offline IL2CPP interop: global-metadata.dat reader (v24-38, single-byte XOR transparent) +
                   typed projection generator (`nami interop`)
tools/
  launch-shim/     launchNami.exe source (embedded into Nami.Cli for `nami create`)
  templates/       `dotnet new nami-mod` template content
artifacts/         local NuGet feed (packages/); a `dotnet/` runtime tree here if present,
                   else the runtime is bundled from the local .NET 10 install
samples/       HelloNami (log-only) + TideProbe (Mono game access proof) +
               TideProbeIl2Cpp (IL2CPP game access proof) + TideProbeIl2CppPatch (IL2CPP
               method-patching proof, incl. full-path HookFull phases A-F and a
               TideBatch sequential-vs-batched benchmark stage G)
tests/         Unit/integration tests (Core, Wave, Cli, Tide) + plugin fixtures
bench/         Loader (Nami.Bench - not in the solution; run via project path) + patching
               (Wave.Bench, incl. HarmonyX head-to-head) benchmarks with regression gates
docs/          Architecture, Tide, Wave, BepInEx comparisons
```

`dotnet build Nami.slnx` builds src + tests + fixtures + the slnx-listed samples
(HelloNami, TideProbe, TideProbeIl2Cpp) + Wave.Bench + launch-shim. TideProbeIl2CppPatch
exists on disk but is not in the solution.

## Try it against a real game (Mono, Windows x64)

```
# 1. Build Nami (managed + native)
dotnet build Nami.slnx
cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build

# 2. Let the CLI stage a nami root next to the game and remember the game exe
nami install "<game>"                     # <game>/nami with bundled dotnet/, mods/, nami.json
nami launch set "<game>\Game.exe" "<game>"  # remember the game exe (add --steam-id <appid>)

# 3. Launch with Nami injected (launches the game suspended, hosts .NET 10 inside it,
#    loads mods from nami/mods). gameDir is the trailing argument:
nami launch "<game>"                      # runs the game with Nami (auto-detects exe when unset)
nami create "<game>"                      # leaves launchNami.exe in <game>/nami for double-click runs

# 4. Watch the loader boot
type "<game>\nami\nami.log"
```

## Prerequisites

- .NET SDK 10.0+
- CMake 3.20+, Ninja (for `-G Ninja` below), and a C++17 compiler (MinGW-w64 -
  MSVC/Clang are untested with these link flags) - only needed for the `native/` tree

## Build & test

```
dotnet build Nami.slnx              # everything in the solution (see layout note above)
dotnet test  Nami.slnx              # all test projects (Nami.Tests, Nami.Wave.Tests, Nami.Cli.Tests, Nami.Tide.Tests)

cmake -S native -B native/build     # native core (optional for managed-only work)
cmake --build native/build
ctest --test-dir native/build

dotnet run --project bench/Nami.Bench -c Release   # headline loader benchmark
```

## CLI

```
nami version                        print version
nami install [gameDir] [--from <zip|url>]
                                    install a Nami root (from build outputs, or from a
                                    self-contained installer artifact via --from)
nami pack [out.zip] [--artifacts <root>]
                                    build the self-contained installer artifact (managed +
                                    native + bundled .NET runtime, hash-verified manifest)
nami launch set <game.exe> [--steam-id <appid>] [--force] [gameDir]
                                    remember which executable is the game
nami launch [offline|steam] [gameDir]
                                    run the game with Nami injected (offline, default);
                                    steam relays to a clean Steam session after exit
nami create [offline|steam] [gameDir]
                                    write launchNami.exe + run-with-nami.bat into the nami root
nami run <mod.csproj> [gameDir]     build a mod, stage it into nami/mods, launch the game
nami doctor [gameDir]               basic sanity check of a Nami install
nami list [gameDir]                 list installed mods (id/version/name + dependencies)
nami interop images|dump|generate|header [args...] [gameDir]
                                    offline IL2CPP typed-projection tooling (dev-time)
nami inex install|enable|disable|status [args...] [gameDir]
                                    legacy BepInEx 5.x lane (boots in game Mono, no proxy)
nami nmod info|install [args...] [gameDir]
                                    .nmod package distribution (manifest info / install)
nami help                           show help
```

`launch` auto-detects the game as the largest `.exe` when none is set.

## Writing a mod

```
# From a repo checkout, after building:
dotnet new install tools/templates/nami-mod     # install the mod template
dotnet new nami-mod -n MyFirstMod               # scaffold a mod (references Nami.Sdk/Tide)

# Produce the NuGet packages into the local feed (NuGet.config points at artifacts/packages;
# or point the mod's NuGet source at a published Nami.Sdk/Nami.Tide instead):
dotnet pack src/Nami.Sdk -o artifacts/packages
dotnet pack src/Nami.Tide -o artifacts/packages

# Stage a root, tell Nami which exe is the game, then build+stage+launch the mod:
nami install "<game>"
nami launch set "<game>\Game.exe" "<game>"
nami run "MyFirstMod\MyFirstMod.csproj" "<game>"   # builds, copies into nami/mods, launches
```

For a real game you must be able to launch `Game.exe`; `nami run` requires the staged root and
the configured game exe from the two steps above.

## Status

Working: mod loading with per-mod isolation and quarantine, live hot reload, method
patching (prefix/postfix/transpilers on Mono, hooks on IL2CPP), typed game access
(Mono and IL2CPP backends), dev tooling (`dotnet new nami-mod`, `nami install`/`run`/
`pack`, offline interop projection), the legacy BepInEx 5.x lane (Mono), the
self-contained installer artifact, and bench regression gates.
Not yet: BepInEx 6 / IL2CPP legacy lane, legacy-pack distribution, non-Windows platforms.

## License

Proprietary / to be decided. Not licensed for redistribution yet.

## Beta status and support

Nami is a beta testing project: it works end-to-end on a small set of verified titles,
but edge cases on untested games and Unity versions are expected. Testing help is what
moves it forward - run it against your titles, report what breaks (game engine version,
what failed, relevant lines from `nami/nami.log`), and fixes land fastest with a
reproduction. Modders: the template + `nami run` loop in TUTORIAL.md is the quickest way
to shake things down.
