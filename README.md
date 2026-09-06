# Nami

**Nami** is a mod loader for Unity games — Mono and IL2CPP — engineered from scratch to be
faster, lighter, and safer than the established loaders, with a clean-slate API and no
dependency on BepInEx, HarmonyX, MonoMod, or Mono.Cecil.

> **Status: load + patch + bridge + typed game access all working in real games.** The native
> core hosts .NET 10 inside four Unity Mono games — Project Hardline (2022.3.27f1),
> Parasocial (2022.3.5f1), ROUNDS (2022.3.34f1), and The Gaspy Color War (Unity 6,
> 6000.5.4f1); Wave patches methods
> with in-house x64 detours and Harmony-style IL-copy prefix/postfix patching (any signature);
> **Tide** lets mods call into the game — typed static/instance field access, typed method
> calls, and live object creation/calls, all executed on the game's main thread and verified
> stable in-game. Enable with `"enableMonoBridge": true` in `nami.json`. Remaining for
> "pick-up modding": packaging/templates/install tooling and a few documented edges (see docs).

## Getting started

- **[TUTORIAL.md](TUTORIAL.md)** — build Nami, stage it next to a game, write and run your first mod.
- **[docs/tide.md](docs/tide.md)** — Tide: the Nami↔game bridge (mods call into Unity Mono from .NET 10).
- **[docs/wave.md](docs/wave.md)** — Wave: Nami's patching engine (inline detours, Harmony-style IL-copy patching, overhead numbers).
- **[docs/technical-difference.md](docs/technical-difference.md)** — point-by-point technical comparison with BepInEx (runtime, isolation, IL2CPP, patching, more).
- **[docs/vs-bepinex.md](docs/vs-bepinex.md)** — the short, honest "why different / what's missing" read.
- **[docs/architecture.md](docs/architecture.md)** — how the loader, hosting and bridges fit together.

## Why Nami exists

- **One modern .NET runtime** (net10.0 LTS) embedded on *both* Mono and IL2CPP games — plugins
  are never stuck on a game's ancient bundled runtime.
- **Lazy type projection** instead of eagerly preloading hundreds of interop assemblies —
  game types materialize only when a mod touches them (kills the 100–400 MB / multi-second
  overhead of the classic IL2CPP interop preload).
- **No player-side generation.** Interop/reference dumping is an offline dev tool, never a
  first-launch cost.
- **Per-mod isolation & crash quarantine.** A throwing mod disables itself; the game keeps
  running. Mods can be hot-reloaded (collectible ALCs are the foundation; the reload tooling
  is a later milestone).
- **Measured.** `bench/` tracks loader and patching overhead (chainloader load/update,
  Wave hook cost); comparative gates vs BepInEx/MelonLoader on identical fixtures land with
  the M5 milestone.

## Repository layout

```
.github/     CI (managed + native jobs)
native/      C++17: injector (nami_boot), in-game loader (nami_loader), hostfxr hosting,
             Tide main-thread drain + object ops
src/
  Nami.Sdk/        Public plugin API (what mods reference)
  Nami.Core/       Chainloader: discovery, graph, ALCs, quarantine
  Nami.Runtime/    In-game managed bootstrap: Boot.Run
  Nami.Tide/       Typed game access: Tide, GameClass, GameObject, TideValue
  Nami.Wave/       Patching engine: x64 detours + Harmony-style IL-copy prefix/postfix
  Nami.Cli/        nami command-line tool (install/launch/create/doctor/list)
  Nami.Interop/    Offline reference-assembly dumper                [later milestone]
tools/
  launch-shim/     launchNami.exe source (embedded into Nami.Cli for `nami create`)
samples/       HelloNami (log-only) + TideProbe (typed game access proof)
tests/         Unit/integration tests (Core, Wave, Cli, Tide) + plugin fixtures
bench/         Loader + patching benchmarks (comparative gates vs other loaders: M5)
docs/          Architecture, Tide, Wave, roadmap
```

## Try it against a real game (Mono, Windows x64)

```
# 1. Build the native injector + loader
cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build

# 2. Stage a nami root next to the game (see docs/architecture.md for the layout):
#    <game>/nami/{dotnet/, Nami.Runtime.dll, Nami.Core.dll, Nami.Sdk.dll,
#                 Nami.Tide.dll, native/nami_boot.exe + nami_loader.dll, mods/*.dll,
#                 Nami.Runtime.runtimeconfig.json}

# 3. Inject (launches the game suspended, hosts .NET 10 inside it, loads mods)
native/build/nami_boot.exe "<game>\Game.exe" "<game>\nami\native\nami_loader.dll"

# ...or, once Nami.Cli is built, let the CLI do it (the nami root from step 2 must exist;
# gameDir is the trailing argument):
nami launch set Game.exe "<game>"     # remember the game exe (add --steam-id <appid>)
nami launch "<game>"                  # runs the game with Nami (auto-detects exe when unset)
nami create "<game>"                  # leaves launchNami.exe in <game>/nami for double-click runs

# 4. Watch the loader boot
type "<game>\nami\nami.log"
```

## Prerequisites

- .NET SDK 10.0+
- CMake 3.20+ and a C++17 compiler (MinGW / MSVC / Clang) — only needed for the `native/` tree

## Build & test

```
dotnet build Nami.slnx              # managed solution (SDK/Core/CLI/tests/fixtures)
dotnet test  Nami.slnx              # all test projects (Core, Wave, Cli, Tide)

cmake -S native -B native/build     # native core (optional for managed-only work)
cmake --build native/build
ctest --test-dir native/build

dotnet run --project bench/Nami.Bench -c Release   # headline loader benchmark
```

## CLI

```
nami version                        print version
nami install [gameDir]              stage a Nami root next to a game (roadmap stub)
nami launch set <game.exe> [--steam-id <appid>] [--force] [gameDir]
                                    remember which executable is the game
nami launch [offline|steam] [gameDir]
                                    run the game with Nami injected (offline, default);
                                    steam relays to a clean Steam session after exit
nami create [offline|steam] [gameDir]
                                    write launchNami.exe + run-with-nami.bat into the nami root
nami doctor [gameDir]               verify an install / report the environment
nami list [gameDir]                 list installed mods (id/version/name + dependencies)
```

*(`launch` auto-detects the game as the largest `.exe` when none is set. install-with-
bundled-runtime, profiles/log-tail/hot-reload/interop/bench arrive with later milestones.)*

## Milestones

See `docs/plan.md` for the full blueprint. Short version: **M0** scaffold & proof of life ·
**M1** core framework (load order, isolation, quarantine, config, logging) · **M2** Tide
bridge + typed game access (M2.5: Wave patching engine) · **M3** dev experience
(packaging/templates/install) · **M4** IL2CPP bridge + offline interop · **M5** depth
(hot reload, profiling, comparative bench gates).

## License

Proprietary / to be decided. Not licensed for redistribution yet.
