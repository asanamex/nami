# Nami

**Nami** is a mod loader for Unity games — Mono and IL2CPP — engineered from scratch to be
faster, lighter, and safer than the established loaders, with a clean-slate API. Nami core
has no build dependency on BepInEx, HarmonyX, MonoMod, or Mono.Cecil; the optional
nami-inex lane boots real BepInEx 5.x for legacy mods (see below).

> **Status: load + patch + bridge + typed game access all working in real games, on Mono AND
> IL2CPP.** The native
> core hosts .NET 10 inside five verified Unity games — four Mono: Project Hardline
> (2022.3.27f1), Parasocial (2022.3.5f1), ROUNDS (2022.3.34f1), The Gaspy Color War (Unity 6,
> 6000.5.4f1); one IL2CPP: D1AL-ogue (Unity 6, 6000.0.61); Wave patches methods
> with in-house x64 detours and Harmony-style IL-copy prefix/postfix patching (closed methods);
> **Tide** lets mods call into the game — typed static/instance field access, typed method
> calls, and live object creation/calls, all executed on the game's main thread and verified
> stable in-game, on both backends (Mono and IL2CPP auto-detected). The flag
> `"enableMonoBridge": true` in `nami.json` gates the boot self-test on both backends;
> mod-issued Tide calls route to the auto-detected backend whenever the loader is present. **M3 dev experience is
> in**: `Nami.Sdk`/`Nami.Tide` NuGet packages, the `dotnet new nami-mod` template, and
> `nami install`/`nami run` — a modder goes from template to a running mod without hand
> staging. **M6 legacy lane shipped (Mono):** `nami inex install|enable|disable|status`
> stages and boots unmodified BepInEx 5.x mods from `nami/inex/` (verified: Hardline Logger
> 1.0.0 + Gaspy Menu 3.0.0 on Project Hardline). Remaining: the self-contained downloadable
> installer, the BepInEx 6 / IL2CPP lane, and a few documented edges (see docs).

## Getting started

- **[TUTORIAL.md](TUTORIAL.md)** — build Nami, stage it next to a game, write and run your first mod.
- **[docs/tide.md](docs/tide.md)** — Tide: the Nami↔game bridge (mods call into Unity Mono or IL2CPP from .NET 10).
- **[docs/wave.md](docs/wave.md)** — Wave: Nami's patching engine (inline detours, Harmony-style IL-copy patching, overhead numbers).
- **[docs/technical-difference.md](docs/technical-difference.md)** — point-by-point technical comparison with BepInEx (runtime, isolation, IL2CPP, patching, more).
- **[docs/vs-bepinex.md](docs/vs-bepinex.md)** — the short, honest "why different / what's missing" read.
- **[docs/architecture.md](docs/architecture.md)** — how the loader, hosting and bridges fit together.

## Why Nami exists

- **One modern .NET runtime** (net10.0 LTS) embedded on *both* Mono and IL2CPP games — plugins
  are never stuck on a game's ancient bundled runtime.
- **Lazy type projection** (design intent) instead of eagerly preloading hundreds of interop
  assemblies — today that means the dev-time `nami interop generate` source emitter plus
  runtime name-based resolution through Tide (no 100–400 MB / multi-second interop preload
  on the player's machine).
- **No player-side generation.** Interop/reference dumping is an offline dev tool, never a
  first-launch cost.
- **Per-mod isolation & crash quarantine.** A throwing mod disables itself; the game keeps
  running. Mods **hot-reload live**: rebuild or drop a top-level DLL into `nami/mods` and it
  swaps into a new generation without restarting the game (unloadable ALCs + a file watcher +
  mod files loaded without file locks).
- **Built-in per-mod profiler.** Every plugin gets tick timings (avg/p95/max) and a periodic
  summary in the log — available to mods in-process via `Context.Profiler`.
- **Measured.** `bench/` tracks loader and patching overhead (chainloader load/update,
  Wave hook cost, Wave-M2 vs HarmonyX head-to-head) with regression gates
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
  Nami.Tide/       Typed game access: Tide, GameClass, GameObject, TideValue
  Nami.Wave/       Patching engine: x64 detours + Harmony-style IL-copy prefix/postfix
  Nami.Cli/        nami command-line tool (install/launch/create/run/doctor/list/interop/inex/version/help)
  Nami.Interop/    Offline IL2CPP interop: plaintext global-metadata.dat reader (v24-31) +
                   typed projection generator (`nami interop`)
tools/
  launch-shim/     launchNami.exe source (embedded into Nami.Cli for `nami create`)
  templates/       `dotnet new nami-mod` template content
artifacts/         local NuGet feed (packages/); `artifacts/dotnet/` if present, else the
                   runtime is bundled from your local .NET 10 install
samples/       HelloNami (log-only) + TideProbe (Mono game access proof) +
               TideProbeIl2Cpp (IL2CPP game access proof)
tests/         Unit/integration tests (Core, Wave, Cli, Tide) + plugin fixtures
bench/         Loader (Nami.Bench — not in the solution; run via project path) + patching
               (Wave.Bench, incl. HarmonyX head-to-head) benchmarks with regression gates
docs/          Architecture, Tide, Wave, roadmap (plan), BepInEx comparisons
```

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
- CMake 3.20+, Ninja (for `-G Ninja` below), and a C++17 compiler (MinGW-w64 —
  MSVC/Clang are untested with these link flags) — only needed for the `native/` tree

## Build & test

```
dotnet build Nami.slnx              # everything: src + tests + fixtures + samples + Wave.Bench + launch-shim
dotnet test  Nami.slnx              # all test projects (Nami.Tests, Nami.Wave.Tests, Nami.Cli.Tests, Nami.Tide.Tests)

cmake -S native -B native/build     # native core (optional for managed-only work)
cmake --build native/build
ctest --test-dir native/build

dotnet run --project bench/Nami.Bench -c Release   # headline loader benchmark
```

## CLI

```
nami version                        print version
nami install [gameDir]              stage a Nami root next to a game (from build outputs)
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
nami help                           show help
```

*(`launch` auto-detects the game as the largest `.exe` when none is set. The self-contained
downloadable "Nami-Install" product and log-tail arrive with later milestones; hot reload,
the per-mod profiler, the offline interop projection, the bench gates and the nami-inex
Mono lane are shipped — see TUTORIAL.md.)*

## Writing a mod (M3 dev experience)

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

## Milestones

See `docs/plan.md` for the full blueprint. Short version: **M0** scaffold & proof of life ·
**M1** core framework (load order, isolation, quarantine, config, logging) · **M1.5** Wave
patching engine (M2: Harmony-style IL-copy) · **M2** Tide bridge + typed game access ·
**M3 done** dev experience (NuGet packages, `dotnet new nami-mod`, `nami install`/`run`) ·
**M4 done** IL2CPP bridge (runtime backend shipped & verified; offline interop projection
shipped: v24-31 parsing + `nami interop` typed projection) · **M5 done** depth — hot reload,
per-mod profiler, Tide-op wiring, comparative bench gates · **M6 Mono shipped** legacy lane
(nami-inex: unmodified BepInEx 5.x mods via `nami inex`; BepInEx 6 / IL2CPP lane later).

## License

Proprietary / to be decided. Not licensed for redistribution yet.
