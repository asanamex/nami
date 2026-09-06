# Nami vs BepInEx — Technical Differences

A point-by-point technical comparison between Nami (this repository, current state) and BepInEx
(the reference Unity mod loader). It is written to be accurate and current as of mid-2026:
BepInEx refers to the **5.x stable (Mono-era)** and **6.0.0-be.\* bleeding-edge** builds;
Nami refers to the architecture implemented and verified in this repo.

Where BepInEx is ahead, that is stated plainly. The goal is a decision document, not a
marketing sheet.

---

## 1. Loading & injection

| Aspect | BepInEx | Nami |
|---|---|---|
| Injection mechanism | **Unity Doorstop**: a proxy DLL dropped into the game root (typically `winhttp.dll`) that Windows loads because the game imports it; Doorstop then boots the preloader before Unity's runtime initializes. | **Launcher injection**: `nami_boot.exe` starts the game suspended, writes the loader DLL path into the process, creates a remote thread that runs `LoadLibraryW`, then resumes the game. No proxy file in the game root. |
| Files placed in the game directory | `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, a `BepInEx/` tree; games whose assemblies get preloader-patched also gain `.bak` copies (observed in the wild: `Assembly-CSharp.dll.bak`). | One self-contained `nami/` directory next to the executable. Nothing is written into the game's own folders and **no game assembly is ever modified or backed up**. |
| Uninstall | Remove Doorstop files + `BepInEx/`; must verify no assembly was left patched. | Delete the `nami/` folder. |
| When the loader runs | At process start, *before* Unity initializes (Doorstop hook). | After the process starts; the loader thread waits for Unity's Mono module (`mono-2.0-bdwgc.dll`) to appear, then proceeds — the game boots normally and Nami hooks in beside it. |
| Reliance on name collision | Yes — a DLL the game imports must be shadowed (`winhttp.dll`), which is detectable and can conflict with other software using that name. | No name collision; injection is explicit and scoped to the launched process. |

## 2. Runtime model (the big one)

| Aspect | BepInEx | Nami |
|---|---|---|
| Where Mono-game plugins execute | **Inside the game's embedded Mono runtime** (Unity 2022-era games ship a .NET Framework 3.5-compatible Mono). Plugins compile against `net35`/`netstandard2.0`-era APIs. | Inside **Nami's own hosted .NET 10 (CoreCLR)** runtime, loaded into the game process via `hostfxr` component activation. Plugins target `net10.0`. |
| BCL available to plugins | Whatever the game's Mono provides (no `Span`-heavy modern APIs by default, old GC, no modern `AssemblyLoadContext` semantics). | Full modern .NET 10 BCL: current GC/JIT, `Span<T>`, `async`, source generators, `System.Text.Json`, etc. |
| Runtime for IL2CPP games | Bundles a **.NET 6 CoreCLR** (BepInEx 6) alongside the game's native IL2CPP; plugins run on that. | Planned: the same CoreCLR hosting machinery as Mono, so plugin code and tooling are identical across both backends. |
| GC coexistence | Mono games: plugins share the game's Boehm GC. IL2CPP: separate CoreCLR GC in-process. | Separate CoreCLR GC in-process in both cases. Mono-game plugin code never allocates in the game's GC. |
| Calling game code from a plugin | Mono games: trivial — plugin IL runs in the game runtime, so it can call any game method directly. IL2CPP: via generated interop. | Mono games: **Tide** — plugin code runs on Nami's .NET and calls INTO the game's Mono via a main-thread bridge with **typed access** (`GameClass`/`GameObject`: static + instance field access, typed method calls with primitive/string args, object creation; verified in-game). Still narrower than in-runtime calls: no scene-object discovery helpers or enum/array values yet, and some Unity internal-call properties are a known edge. |

## 3. Plugin isolation & failure handling

| Aspect | BepInEx | Nami |
|---|---|---|
| Isolation between plugins | Mono games: all plugins share the game's single AppDomain — shared statics, shared assembly resolution, exceptions and `static` state can bleed between plugins and the game. | Every plugin loads into its **own unloadable `AssemblyLoadContext`**: isolated statics, isolated resolution; plugin assemblies are distinct instances even when names collide. |
| Unloading / hot reload | Not supported on Mono (plugins live for the process lifetime). IL2CPP: process-lifetime component contexts. | Collectible ALCs from day one — the foundation for per-mod unload and hot reload (M4 milestone). |
| A crashing plugin | An exception escaping a plugin's update can take down the game or corrupt shared state; no structured quarantine. | **Crash quarantine**: a plugin that throws N consecutive times is disabled (`Quarantined`), `OnUnload` is called, the reason is logged, the game keeps running. Verified by test. |
| Assembly identity conflicts | Two plugins shipping the same dependency fight over one AppDomain resolution. | Each ALC resolves its own copy; only the Nami framework assemblies unify (by design, so plugin↔loader types match). |

## 4. Discovery, load order, dependencies

| Aspect | BepInEx | Nami |
|---|---|---|
| Plugin discovery | `TypeLoader` scans `BepInEx/plugins`, reads metadata with **Mono.Cecil** without loading, caches results under `BepInEx/cache/` keyed by SHA-256 of the assembly. | Probes each DLL in a throwaway **collectible ALC** using reflection; framework refs resolve to the already-loaded copy so probing works both in tests and in the hosted component context. No Cecil anywhere. |
| Dependency resolution | GUID-based `BepInDependency`/`BepInIncompatibility` attributes; chainloader sorts and checks versions (SemVer). | `[PluginDependency]` / `[PluginIncompatibility]` on the plugin class; a pure `DependencyResolver` prunes duplicates, resolves incompatibility pairs deterministically, detects missing deps and **cycles**, and topologically sorts. Unit-tested in isolation. |
| Metadata scanning tech | Mono.Cecil (third-party IL reader). | `System.Reflection` on collectible probe contexts; the design intent is `System.Reflection.Metadata` (Span-based) for anything heavier. |

## 5. Assembly patching vs runtime hooks

| Aspect | BepInEx | Nami |
|---|---|---|
| Preloader patching | BepInEx 5-era Mono flow: `AssemblyPatcher` **rewrites game assemblies with Cecil before they load** (patcher plugins in `BepInEx/patchers`). This is why patched games can carry `.bak` assembly copies. | **None.** Nami never reads, rewrites, or re-emits a game assembly. All extension happens at runtime in Nami's own runtime. |
| Runtime patching API | **HarmonyX** (a Harmony fork) — mature prefix/postfix/transpiler ecosystem; the de-facto standard modders know. IL2CPP patching rides on MonoMod detours / Dobby through Il2CppInterop. | **Wave** (in-house, this repo): x64 inline detours with owner-scoped chains, exact byte restore, ~45 ns/call overhead. Two engines: M1 native-stub gate/observer dispatch for parameterless void targets, and M2 **IL-copy patching** — any signature, Harmony-shaped conventions (`__instance`/`__result`/`__state`/`__args`), skip semantics and result rewriting. Zero Harmony/MonoMod/Cecil. |
| Dependency weight | Core ships HarmonyX + MonoMod + Mono.Cecil regardless of need. | Zero third-party managed dependencies; patching is a loadable subsystem (`Nami.Wave`), not a boot-time cost. |

## 6. IL2CPP interop strategy

| Aspect | BepInEx | Nami (planned) |
|---|---|---|
| First-launch interop generation | Runs **Cpp2IL + Il2CppInterop generator on the player's machine** on first launch — commonly 30 s to 2+ min; results cached by hash (`BepInEx/interop/`). Unity 6 metadata churn (v39+) has caused repeated regressions in be.7xx builds (Cpp2IL downgrades, interop bumps). | **Offline generation only**: `nami interop` runs on the dev/installer machine; the player never waits on a generator. Content-addressed cache keyed by game + metadata hash. |
| Interop assembly load | **Eager preload** of all generated interop assemblies before plugins load (configurable, default on) — hundreds of assemblies, commonly **+100–400 MB working set**. | **Lazy projection**: game types materialize on demand when a mod touches them; emitted projections cached on disk keyed by `sha256(GameAssembly | metadata | schema)`. |
| Metadata parsing | Cpp2IL library reverse-engineering the binary + `global-metadata.dat`. | Native C++ metadata reader in `nami_loader` with a per-Unity-version schema registry and golden corpus; hook Unity's own decoder where metadata is encrypted rather than reimplementing crypto. |
| Plugin target for IL2CPP | .NET 6 (bundled, EOL). | .NET 10 (LTS) — same runtime as Mono games. |

## 7. Configuration & logging

| Aspect | BepInEx | Nami |
|---|---|---|
| Loader config | `doorstop_config.ini` in the game root + `BepInEx/config/` per-plugin `.cfg` **INI** files via `ConfigFile`. | `nami.json` in the nami root (JSON, tolerant parsing, defaults on missing/corrupt file). Per-plugin config sections are a future milestone. |
| Logging | `Logger` → `DiskLogListener` writes `BepInEx/LogOutput.log`; console via `ConsoleManager`; per-plugin `ManualLogSource`. | `LogHub` fan-out to sinks; `FileSink` writes `nami/nami.log`; per-plugin `ILog` tags records with the plugin id. Sinks are isolated so a broken sink can't crash the host. |

## 8. Packaging, CLI, tooling

| Aspect | BepInEx | Nami |
|---|---|---|
| Mod distribution | Loose DLL in `BepInEx/plugins` (plus `patchers/`). | Loose DLL in `nami/mods` today; `.nmod` package format (id, semver, deps, game bounds) is planned. |
| CLI / dev tooling | No first-party CLI for install/inspect (community tools exist). | `nami` CLI: version/doctor/list + a player-facing launcher flow — `launch set <game.exe>`, `launch [offline|steam]` (auto-detects the exe; Steam relay to a clean session after exit), `create` (double-click `launchNami.exe` in the nami root). Self-contained `nami install` (bundled runtime) is planned. |
| Benchmarking | None shipped. | `bench/` harness from M0; comparative gates vs BepInEx/MelonLoader planned for M5. |

## 9. Platform & target matrix

| Aspect | BepInEx | Nami |
|---|---|---|
| Unity Mono | Windows/Linux/macOS, x86/x64 (5.x and 6.x). | **Windows x64 verified** against four Unity Mono titles spanning 2022.3 and Unity 6 (2022.3.5f1, 2022.3.27f1, 2022.3.34f1, 6000.5.4f1); other OS/arch planned. |
| Unity IL2CPP | Windows/Linux/macOS x64 (6.x be). | Planned (same core; IL2CPP bridge milestone). |
| Non-Unity .NET apps | Supported (NET Framework / CoreCLR launchers). | Out of scope — Unity games only. |
| Plugin TFMs | net35/netstandard2.0 (Mono), net6.0 (IL2CPP). | net10.0 everywhere. |

## 10. Codebase & dependencies

| Aspect | BepInEx | Nami |
|---|---|---|
| Third-party runtime deps | Doorstop, HarmonyX, MonoMod.RuntimeDetour/Utils, Mono.Cecil, Cpp2IL, Il2CppInterop, bundled .NET 6. | No third-party managed packages; native side uses only the Windows API + the bundled .NET 10 runtime. Patching/interop are future in-house subsystems. |
| Injection surface | Doorstop (separate project, C++). | In-repo C++ (`native/`): injector + loader, ~600 LOC, fully static link (no MinGW runtime DLLs to resolve in a foreign process). |
| IL tooling | Mono.Cecil everywhere (discovery, patching, interop). | None (by design); reflection-based discovery; native metadata reader planned for IL2CPP. |

## 11. Operational & observable differences (verified)

| Behavior | BepInEx | Nami |
|---|---|---|
| Game folder after install | Doorstop files + `BepInEx/` + possible `.bak` assemblies. | Only `nami/`. |
| Startup added latency (Mono) | Small (Doorstop boot + preloader patch pass). | Small (module wait + CoreCLR host init; measured game process stable at ~375 MB including the game itself). |
| RAM overhead (Mono) | Near zero beyond plugins (they run in the game's runtime). | One modern runtime in-process — a real, measurable cost; the trade for isolation + modern BCL. |
| Crash containment | Plugin exception ⇒ game may crash / corrupt shared state. | Plugin exception ⇒ quarantine path, game continues. |
| Can it run alongside the other | N/A | Nami and BepInEx both present in one game folder conflict (both may fight over injection); don't install together. |

## 12. Ecosystem & maturity (where BepInEx wins today)

- **Existing mods**: BepInEx has a massive catalog; Nami's clean-slate API loads none of it (a deliberate choice).
- **Patching**: HarmonyX's prefix/postfix/transpiler is proven and known to every modder; Wave implements a compatible prefix/postfix core, but without HarmonyX's years of edge-case coverage or its transpiler ecosystem.
- **Documentation & community knowledge**: BepInEx is the default answer; Nami is new.
- **Edge-case hardening**: BepInEx has years of real-world coverage across thousands of games; Nami has four verified games (three Unity 2022.3 Mono, one Unity 6 Mono).

## Summary

The differences reduce to one architectural bet:

- **BepInEx** maximizes compatibility with the existing ecosystem: it lives *inside* the game's
  runtime (Mono), patches game assemblies ahead of load, ships the ecosystem's standard
  libraries (Cecil/HarmonyX), and accepts the resulting coupling — shared AppDomain, proxy
  DLLs in the game root, player-side IL2CPP generation, an old BCL for Mono plugins.
- **Nami** maximizes isolation and modernity: it brings its own runtime and runs plugins there,
  never touches game assemblies, contains each plugin in an unloadable ALC, quarantines
  failures, and keeps the game folder pristine — at the cost of not loading existing mods, a
  patcher that is young (Wave) rather than ecosystem-proven, and the overhead of a second
  runtime.

The long-term bet of Nami is that those costs shrink as the missing pieces land (packaging,
projection, hot reload), while BepInEx's costs (runtime coupling, generation on the player's
machine, metadata churn chasing, EOL .NET 6) are structural and only grow as Unity moves on.
