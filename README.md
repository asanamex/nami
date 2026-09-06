# Nami

**Nami** is a mod loader for Unity games — Mono and IL2CPP — engineered from scratch to be
faster, lighter, and safer than the established loaders, with a clean-slate API and no
dependency on BepInEx, HarmonyX, MonoMod, or Mono.Cecil.

> **Status: M0 complete + Wave patching engine.** The native core hosts .NET 10 inside a real
> Unity game (Project Hardline); the chainloader isolates mods in ALCs with quarantine; and
> **Wave** — Nami's in-house patching engine — installs x64 inline detours with owner-scoped
> gate/observer chains (~45 ns/call overhead, exact byte restore, 15/15 tests). The
> experimental CoreCLR→Mono bridge is opt-in pending GC interop hardening.

## Getting started

- **[TUTORIAL.md](TUTORIAL.md)** — build Nami, stage it next to a game, write and run your first mod.
- **[docs/wave.md](docs/wave.md)** — Wave: Nami's patching engine (inline detours, gate/observer chains, overhead numbers).
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
  running. Mods can be hot-reloaded.
- **Measured.** A benchmark harness compares startup delta and memory overhead against
  BepInEx/MelonLoader on identical fixtures, gating every milestone.

## Repository layout

```
.github/     CI (managed + native jobs)
native/      C++17: injector (nami_boot), in-game loader (nami_loader), hostfxr hosting
src/
  Nami.Sdk/        Public plugin API (what mods reference)
  Nami.Core/       Chainloader: discovery, graph, ALCs, quarantine
  Nami.Runtime/    In-game managed bootstrap: Boot.Run + Mono bridge (opt-in)
  Nami.Wave/       Patching engine: x64 inline detours, gate/observer chains
  Nami.Cli/        nami command-line tool
  Nami.Projection/ Lazy game-type projection                [later milestone]
  Nami.Interop/    Offline reference-assembly dumper        [later milestone]
samples/       HelloNami example mod
tests/         Fixture plugins + unit/integration tests
bench/         Comparative benchmark harness vs other loaders
docs/          Architecture & roadmap
```

## Try it against a real game (Mono, Windows x64)

```
# 1. Build the native injector + loader
cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build

# 2. Stage a nami root next to the game (see docs/architecture.md for the layout):
#    <game>/nami/{dotnet/, Nami.Runtime.dll, Nami.Core.dll, Nami.Sdk.dll,
#                 native/nami_loader.dll, mods/*.dll, Nami.Runtime.runtimeconfig.json}

# 3. Inject (launches the game suspended, hosts .NET 10 inside it, loads mods)
native/build/nami_boot.exe "<game>\Game.exe" "<game>\nami\native\nami_loader.dll"

# 4. Watch the loader boot
type "<game>\nami\nami.log"
```

## Prerequisites

- .NET SDK 10.0+
- CMake 3.20+ and a C++17 compiler (MinGW / MSVC / Clang) — only needed for the `native/` tree

## Build & test

```
dotnet build Nami.slnx              # managed solution (SDK/Core/CLI/tests/fixtures)
dotnet test  tests/Nami.Tests       # unit + integration tests

cmake -S native -B native/build     # native core (optional for managed-only work)
cmake --build native/build
ctest --test-dir native/build

dotnet run --project bench/Nami.Bench -c Release   # headline loader benchmark
```

## CLI

```
nami version     print version
nami doctor      verify an install / report the environment
nami list        list installed mods and their state
```

*(install/run/profiles/log-tail/hot-reload/interop/bench arrive with later milestones.)*

## Milestones

See `docs/plan.md` for the full blueprint. Short version: **M0** scaffold & proof of life ·
**M1** core framework (load order, isolation, quarantine, config, logging) · **M2** patch
engine + Mono bridge · **M3** IL2CPP bridge + offline interop · **M4** hot reload, profiling,
templates · **M5** perf hardening + comparative bench gates.

## License

Proprietary / to be decided. Not licensed for redistribution yet.
