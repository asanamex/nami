# Nami vs BepInEx — Technical Differences

A point-by-point technical comparison between Nami (this repository, current state) and BepInEx
(the reference Unity mod loader). It is written to be accurate and current as of mid-2026:
BepInEx refers to the **5.x stable (Mono-era)** and **6.0.0-be.\* bleeding-edge** builds;
Nami refers to the architecture implemented and verified in this repo.

Where BepInEx is ahead, that is stated plainly. The goal is a decision document, not a
marketing sheet. (BepInEx-column facts follow BepInEx 5.x / 6.0.0-be docs and behavior
observed in the wild — no BepInEx source lives in this repo to check them against.)

---

## 1. Loading & injection

| Aspect | BepInEx | Nami |
|---|---|---|
| Injection mechanism | **Unity Doorstop**: a proxy DLL dropped into the game root (typically `winhttp.dll`) that Windows loads because the game imports it; Doorstop then boots the preloader before Unity's runtime initializes. | **Launcher injection**: `nami_boot.exe` starts the game suspended, writes the loader DLL path into the process, creates a remote thread that runs `LoadLibraryW`, then resumes the game. No proxy file in the game root. The legacy lane reuses the same injector (no `winhttp`/`doorstop_config.ini`): the loader sets `DOORSTOP_*` env and boots BepInEx 5.x via a `mono_jit_init` detour + main-thread invoke. |
| Files placed in the game directory | `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, a `BepInEx/` tree; games whose assemblies get preloader-patched also gain `.bak` copies (observed in the wild: `Assembly-CSharp.dll.bak`). | One self-contained `nami/` directory next to the executable (`nami/mods` + `nami.log` for native mods; `nami/inex/BepInEx/` + sentinel + `native/nami-inex.log` + `inex/BepInEx/LogOutput.log` when the legacy lane is enabled). Nothing is written into the game's own folders and **no game assembly is ever modified or backed up**. |
| Uninstall | Remove Doorstop files + `BepInEx/`; must verify no assembly was left patched. | Delete the `nami/` folder. |
| When the loader runs | At process start, *before* Unity initializes (Doorstop hook). | After the process starts; the loader thread waits for the game's runtime to appear (`mono-2.0-bdwgc.dll`/`mono.dll` on Mono titles, `GameAssembly.dll` on IL2CPP titles), then proceeds — the game boots normally and Nami hooks in beside it. Legacy mods boot at Doorstop timing via a `mono_jit_init` detour (injector holds the main thread until hook-ready; Ldr load-watch catches dynamically-loaded Mono; `early preloader start rc=0` verified) with a window+domain-gated late kick as fallback — see `docs/architecture.md`. |
| Reliance on name collision | Yes — a DLL the game imports must be shadowed (`winhttp.dll`), which is detectable and can conflict with other software using that name. | No name collision; injection is explicit and scoped to the launched process. |

## 2. Runtime model (the big one)

| Aspect | BepInEx | Nami |
|---|---|---|
| Where Mono-game plugins execute | **Inside the game's embedded Mono runtime** (Unity 2022-era games ship a .NET Framework 3.5-compatible Mono). Plugins compile against `net35`/`netstandard2.0`-era APIs. | Inside **Nami's own hosted .NET 10 (CoreCLR)** runtime, loaded into the game process via `hostfxr` component activation. Plugins target `net10.0`. |
| BCL available to plugins | Whatever the game's Mono provides (no `Span`-heavy modern APIs by default, old GC, no modern `AssemblyLoadContext` semantics). | Full modern .NET 10 BCL: current GC/JIT, `Span<T>`, `async`, source generators, `System.Text.Json`, etc. |
| Runtime for IL2CPP games | Bundles a **.NET 6 CoreCLR** (BepInEx 6) alongside the game's native IL2CPP; plugins run on that. | **Shipped**: the same hosted .NET 10 CoreCLR as Mono titles. The loader auto-detects `GameAssembly.dll` and Tide gains an IL2CPP backend — plugins are byte-identical across Mono and IL2CPP. |
| GC coexistence | Mono games: plugins share the game's Boehm GC. IL2CPP: separate CoreCLR GC in-process. | Separate CoreCLR GC in-process in both cases. Mono-game plugin code never allocates in the game's GC. |
| Calling game code from a plugin | Mono games: trivial — plugin IL runs in the game runtime, so it can call any game method directly. IL2CPP: via generated interop. | Mono and IL2CPP games: **Tide** — plugin code runs on Nami's .NET and calls INTO the game runtime via a main-thread bridge with **typed access** (`GameClass`/`GameObject`: static + instance field/property access incl. live objects, typed method calls with signature-aware boxing, a generic `Get<T>`/`Set<T>`/`Call<T>` API, enums as their underlying int (`long`-backed enums surface as `I64`), arrays via `TideArrays`, object creation (parameterless ctor); verified in-game on both backends — see [tide.md §9](./tide.md) for IL2CPP). Still narrower than in-runtime calls: scene discovery runs through `GameClass.FindObject()` (window-proc executor via plural `FindObjectsOfType` + element 0, active objects) and static accessors (`Camera.main`). |

## 3. Plugin isolation & failure handling

| Aspect | BepInEx | Nami |
|---|---|---|
| Isolation between plugins | Mono games: all plugins share the game's single AppDomain — shared statics, shared assembly resolution, exceptions and `static` state can bleed between plugins and the game. | Native lane: every plugin loads into its **own unloadable `AssemblyLoadContext`**: isolated statics, isolated resolution; plugin assemblies are distinct instances even when names collide. Legacy lane = BepInEx-column behavior (shared AppDomain, no isolation). |
| Unloading / hot reload | Not supported on Mono (plugins live for the process lifetime). IL2CPP: process-lifetime component contexts. | **Shipped.** Collectible ALCs plus a debounced file watcher over top-level `mods/*.dll`: rebuild/drop/delete a mod DLL and it swaps to a new generation live — dependents reload with it, mod files are never locked, and mods can self-reload via `Context.RequestReload()`. Subdirectory DLLs (`.nmod`-installed `mods/<id>/`) are discovered but not watched. |
| A crashing plugin | An exception escaping a plugin's update can take down the game or corrupt shared state; no structured quarantine. | Native lane — **crash quarantine**: a plugin that throws N consecutive times is disabled (`Quarantined`), `OnUnload` is called, the reason is logged, the game keeps running. Verified by test (`ChainloaderTests`, `HotReloadTests`). Legacy lane: a legacy crash is a game crash; `nami inex disable` returns to pure Nami. |
| Assembly identity conflicts | Two plugins shipping the same dependency fight over one AppDomain resolution. | Each ALC resolves its own copy; only the Nami framework assemblies unify (by design, so plugin↔loader types match). |

## 4. Discovery, load order, dependencies

| Aspect | BepInEx | Nami |
|---|---|---|
| Plugin discovery | `TypeLoader` scans `BepInEx/plugins`, reads metadata with **Mono.Cecil** without loading, caches results under `BepInEx/cache/` keyed by SHA-256 of the assembly. | Probes each DLL in a throwaway **collectible ALC** using reflection; framework refs resolve to the already-loaded copy so probing works both in tests and in the hosted component context. No Cecil anywhere. |
| Dependency resolution | GUID-based `BepInDependency`/`BepInIncompatibility` attributes; chainloader sorts and checks versions (SemVer). | `[PluginDependency]` (with enforced `MinimumVersion`: SemVer numeric compare, exact match otherwise) / `[PluginIncompatibility]` on the plugin class; a pure `DependencyResolver` prunes duplicates, resolves incompatibility pairs deterministically, detects missing deps and **cycles**, and topologically sorts. Unit-tested in isolation. |
| Metadata scanning tech | Mono.Cecil (third-party IL reader). | `System.Reflection` on collectible probe contexts; the design intent is `System.Reflection.Metadata` (Span-based) for anything heavier. |

## 5. Assembly patching vs runtime hooks

| Aspect | BepInEx | Nami |
|---|---|---|
| Preloader patching | BepInEx 5-era Mono flow: `AssemblyPatcher` **rewrites game assemblies with Cecil before they load** (patcher plugins in `BepInEx/patchers`). This is why patched games can carry `.bak` assembly copies. | Native lane: **none.** Nami never reads, rewrites, or re-emits a game assembly. All extension happens at runtime in Nami's own runtime. (Legacy lane executes BepInEx 5.x patchers as-is inside game Mono.) |
| Runtime patching API | **HarmonyX** (a Harmony fork) — mature prefix/postfix/transpiler ecosystem; the de-facto standard modders know. IL2CPP patching rides on MonoMod detours / Dobby through Il2CppInterop. | **Wave** (in-house, this repo): x64 inline detours with owner-scoped chains, exact byte restore, ~+45 ns/call M1 observer overhead measured by `bench/Wave.Bench` (x64 Release/.NET 10; varies by machine — see `docs/wave.md`). Two engines: M1 native-stub gate/observer dispatch for parameterless void targets, and M2 **IL-copy patching** — any closed method with a real body (incl. `calli` bodies; open generic definitions via `Wave.Patch(definition, typeArguments, ...)`), Harmony-shaped conventions (`__instance`/`__result`/`__state`/`__args`), skip semantics and result rewriting. Native patching is Wave-only (zero Harmony/MonoMod/Cecil); the legacy lane brings real HarmonyX via BepInEx 5.x (Mono only). IL2CPP titles get native dispatch-stub hooks (`WaveIl2Cpp` — fast observe/skip plus a full path with all arguments incl. stack and result observation/rewriting via `HookFull`, in-house, zero Dobby/Il2CppInterop; see [tide.md §9](./tide.md)). |
| Dependency weight | Core ships HarmonyX + MonoMod + Mono.Cecil regardless of need. | Zero third-party managed dependencies; patching is a loadable subsystem (`Nami.Wave`), not a boot-time cost. |

## 6. IL2CPP interop strategy

Nami's IL2CPP story has two layers, both shipped. The **runtime bridge** lets mods call into the
game through Tide's typed API on IL2CPP titles exactly as on Mono — no interop assemblies,
no generator. The **typed-projection layer** is the dev-time `nami interop` source emitter
(`GameInterop.g.cs` of lazy-cached `GameClass` accessors + method-name constants): each
accessor materializes its `GameClass` on first use and caches it for the process, repeated
member-name lookups are memoized in the native loader (`tide_member_cache`), and per-tick
op groups can ride a single main-thread round trip (`TideBatch`).

| Aspect | BepInEx | Nami |
|---|---|---|
| Runtime bridge (call game code) | Plugins run on the game's IL2CPP via **Il2CppInterop** — generated managed wrappers over every game type, loaded through MonoMod's `HookGen`/detours. | **Shipped**: Tide's IL2CPP backend calls the game's `il2cpp_*` runtime exports directly (classes by name, fields/properties via get_/set_ accessors, methods by name + arity, native array access, GC-handle objects). Ops run on the game's main thread inside its window procedure (the only context IL2CPP tolerates — no export fires per-frame and worker threads AV). Typed `GameClass`/`GameObject` API identical to Mono; verified live on a Unity 6000.0.61 title (see [tide.md §9](./tide.md)). |
| First-launch interop generation | Runs **Cpp2IL + Il2CppInterop generator on the player's machine** on first launch — commonly 30 s to 2+ min; results cached by hash (`BepInEx/interop/`). Unity 6 metadata churn (v39+) has caused repeated regressions in be.7xx builds (Cpp2IL downgrades, interop bumps). | **None at runtime** (no generator needed — the runtime bridge needs no metadata). The offline `nami interop` projection runs on the dev machine and emits one source file; the player never waits on a generator. |
| Interop assembly load | **Eager preload** of all generated interop assemblies before plugins load (configurable, default on) — hundreds of assemblies, commonly **+100–400 MB working set**. | Not applicable (types resolve by name through `GameClass.Resolve`; projections are dev-time source, never loaded assemblies). |
| Metadata parsing | Cpp2IL library reverse-engineering the binary + `global-metadata.dat`. | Not needed for the runtime bridge (the game's own runtime resolves everything). The offline reader targets metadata v24-38 (single-byte XOR de-obfuscation is transparent); custom-encrypted metadata needs per-game reversing (no generic decoder exists). |
| Plugin target for IL2CPP | .NET 6 (bundled, EOL). | .NET 10 (LTS) — same runtime as Mono games, already shipped. |

## 7. Configuration & logging

| Aspect | BepInEx | Nami |
|---|---|---|
| Loader config | `doorstop_config.ini` in the game root + `BepInEx/config/` per-plugin `.cfg` **INI** files via `ConfigFile`. | `nami.json` in the nami root (JSON, tolerant parsing, defaults on missing/malformed file; I/O errors still throw). Per-plugin config shipped: `pluginConfig.<id>` sections via `Context.Config`. Legacy lane: BepInEx `.cfg` files live under `nami/inex/BepInEx/config/`; the switch is the `nami/inex/enabled` file, and no `doorstop_config.ini` is needed (Nami sets `DOORSTOP_*` env directly — Doorstop itself must stay `enabled=false` if present). |
| Logging | `Logger` → `DiskLogListener` writes `BepInEx/LogOutput.log`; console via `ConsoleManager`; per-plugin `ManualLogSource`. | `LogHub` fan-out to sinks; `FileSink` writes `nami/nami.log`; per-plugin `ILog` tags records with the plugin id. Sinks are isolated so a broken sink can't crash the host. Legacy lane adds `nami/native/nami-inex.log` + `nami/inex/BepInEx/LogOutput.log` (see `nami inex status`). |

## 8. Packaging, CLI, tooling

| Aspect | BepInEx | Nami |
|---|---|---|
| Mod distribution | Loose DLL in `BepInEx/plugins` (plus `patchers/`). | Native mods are built from NuGet (`Nami.Sdk`/`Nami.Tide` packages) and land as loose DLLs in `nami/mods` (via `nami run`); `.nmod` (zip + `mod.json`: id/name/version/description/authors/deps/incompatibilities — no game-bounds field) installs to `mods/<id>/` via the `NamiPackage` library (auto-install/CLI wiring future). Legacy mods: BepInEx 5.x tree → `nami/inex/BepInEx` via `nami inex install` (cache/ skipped), DLLs into `nami/inex/BepInEx/plugins`. |
| CLI / dev tooling | No first-party CLI for install/inspect (community tools exist). | `nami` CLI: version/doctor/list/help + `install` (stages a runnable root from build outputs; `--from <zip|url>` installs the self-contained Nami-Install artifact — hash-verified against its SHA-256 manifest, upgrade-safe over an existing root) + `pack` (builds that artifact: managed + native + bundled .NET runtime in one zip) + `run <mod.csproj>` (build, copy to nami/mods, launch) + `interop images|dump|generate|header` (offline IL2CPP projection, dev-time) + a player-facing launcher flow — `launch set <game.exe>`, `launch [offline|steam]` (auto-detects the exe; Steam relay to a clean session after exit, or uninjected relay with `steamRelaySkipInjection`), `create` (double-click `launchNami.exe` in the nami root) + `inex install|enable|disable|status` (legacy BepInEx 5.x lane; `doctor` reports its payload/sentinel state). Plus `dotnet new nami-mod` and NuGet packages. |
| Benchmarking | None shipped. | `bench/` harness from M0 with regression gates (Wave-vs-HarmonyX ratio + absolute budgets, `NAMI_GATE_*`); full-loader shootouts stay a manual protocol (see plan.md M5). |

## 9. Platform & target matrix

| Aspect | BepInEx | Nami |
|---|---|---|
| Unity Mono | Windows/Linux/macOS, x86/x64 (5.x and 6.x). | **Windows x64 verified** against four Unity Mono titles spanning 2022.3 and Unity 6 (2022.3.5f1, 2022.3.27f1, 2022.3.34f1, 6000.5.4f1); other OS/arch planned. |
| Unity IL2CPP | Windows/Linux/macOS x64 (6.x be). | **Shipped** (Windows x64): Tide's IL2CPP backend verified live on D1AL-ogue (Unity 6000.0.61); probing also done on a 2020.3.18 title (Arrow a Row). Legacy BepInEx compat is Mono/BepInEx-5 only; IL2CPP titles skip `inex::arm`. |
| Non-Unity .NET apps | Supported (NET Framework / CoreCLR launchers). | Out of scope — Unity games only. |
| Plugin TFMs | net35/netstandard2.0 (Mono), net6.0 (IL2CPP). | net10.0 everywhere. |

## 10. Codebase & dependencies

| Aspect | BepInEx | Nami |
|---|---|---|
| Third-party runtime deps | Doorstop, HarmonyX, MonoMod.RuntimeDetour/Utils, Mono.Cecil, Cpp2IL, Il2CppInterop, bundled .NET 6. | No third-party managed packages (verified: no `PackageReference` in any `src/*.csproj`; only tests use xunit/coverlet and only `bench/Wave.Bench` uses HarmonyX 2.16.1 as a comparative baseline); native side uses only the Windows API + the bundled .NET 10 runtime. Patching is shipped in-house (Wave); the IL2CPP runtime bridge (Tide backend) and the offline projection emitter (`nami interop`) are shipped in-house too. |
| Injection surface | Doorstop (separate project, C++). | In-repo C++ (`native/`, ~5.7k LOC total — ≈265 injector+boot, the rest the Tide bridge + legacy lane): injector + loader. `nami_loader.dll` is fully static-linked (no MinGW runtime DLLs to resolve in a foreign process); `nami_boot.exe` is MinGW-linked. |
| IL tooling | Mono.Cecil everywhere (discovery, patching, interop). | None in the native lane (by design); reflection-based discovery; IL2CPP access via the game's own `il2cpp_*` runtime exports (Tide IL2CPP backend). The legacy lane runs BepInEx's own Cecil/Harmony stack unmodified. |

## 11. Operational & observable differences (verified)

| Behavior | BepInEx | Nami |
|---|---|---|
| Game folder after install | Doorstop files + `BepInEx/` + possible `.bak` assemblies. | Only `nami/`. |
| Startup added latency (Mono) | Small (Doorstop boot + preloader patch pass). | Small (module wait + CoreCLR host init). One extra in-process CoreCLR is a real, measurable cost — measure per title. |
| RAM overhead (Mono) | Near zero beyond plugins (they run in the game's runtime). | One modern runtime in-process — a real, measurable cost; the trade for isolation + modern BCL. |
| Crash containment | Plugin exception ⇒ game may crash / corrupt shared state. | Native plugin exception ⇒ quarantine path, game continues. Legacy lane: a legacy crash is a game crash (`nami inex disable` returns to pure Nami). |
| Can it run alongside the other | N/A | Supported via the nami-inex lane (Mono only): stage a BepInEx 5.x tree to `nami/inex/BepInEx` + `nami inex enable`; Nami and BepInEx then run side by side, Nami injecting. Only Doorstop's `winhttp` proxy in the game root stays forbidden. |

## 12. Ecosystem & maturity (where BepInEx wins today)

- **Existing mods**: BepInEx has a massive catalog; Nami's native clean-slate API loads none of it — but the nami-inex lane loads unmodified BepInEx 5.x Mono mods from `nami/inex/BepInEx/plugins` (verified: Hardline Logger + Gaspy Menu).
- **Patching**: HarmonyX's prefix/postfix/transpiler is proven and known to every modder; Wave implements a compatible prefix/postfix core, but without HarmonyX's years of edge-case coverage or its transpiler ecosystem.
- **Documentation & community knowledge**: BepInEx is the default answer; Nami is new.
- **Edge-case hardening**: BepInEx has years of real-world coverage across thousands of games; Nami has five verified games (three Unity 2022.3 Mono, one Unity 6 Mono, one Unity 6 IL2CPP) plus Project Hardline via the legacy lane.

## Summary

The differences reduce to one architectural bet:

- **BepInEx** maximizes compatibility with the existing ecosystem: it lives *inside* the game's
  runtime (Mono), patches game assemblies ahead of load, ships the ecosystem's standard
  libraries (Cecil/HarmonyX), and accepts the resulting coupling — shared AppDomain, proxy
  DLLs in the game root, player-side IL2CPP generation, an old BCL for Mono plugins.
- **Nami** maximizes isolation and modernity: it brings its own runtime and runs plugins there,
  never touches game assemblies, contains each plugin in an unloadable ALC, quarantines
  failures, and keeps the game folder pristine — at the cost of a young (Wave) rather than
  ecosystem-proven patcher for native mods, and the overhead of a second
  runtime. The existing BepInEx catalog is reachable through the nami-inex legacy lane instead
  of the native API.

The long-term bet of Nami is that those costs shrink as the remaining pieces land (hot reload,
the per-mod profiler, the IL2CPP runtime bridge, the offline projection emitter, the nami-inex
Mono legacy lane and the self-contained Nami-Install artifact — `nami pack`/`nami install --from` —
are already shipped), while
BepInEx's costs (runtime coupling, generation on the player's machine, metadata churn chasing,
EOL .NET 6) are structural and only grow as Unity moves on.
