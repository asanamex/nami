# Nami Plan (roadmap)

Full blueprint: `~/.commandcode/plans/nami-unity-mod-loader.md` (or via `/plans`).

## Status

- **M0 — done.** Loader hosts .NET 10 inside a real Unity Mono game; chainloader with ALC
  isolation, quarantine, config, logging.
- **M1 — done.** Wave (patching engine): x64 inline detours, gate/observer chains.
- **M1.5 — done.** Wave M2: Harmony-style IL-copy patching — prefix/postfix by convention
  (`__instance`/`__result`/`__state`/`__args`), skip semantics, result rewriting, any
  signature; multi-owner chains rebuild the patched body atomically.
- **M2 — done (Mono slice).** Tide: cross-runtime bridge + **typed game access** — static and
  instance field/property access, typed method calls, live object creation/calls, **generic
   typed API** (`Get<T>`/`Set<T>`/`Call<T>`), **enum values** (as underlying int;
   `long`-backed enums surface as `I64`), **array
  values** (`TideArrays`), and **live scene-object access** via static accessors
  (`Camera.main`), all on the game's main thread. Verified in-game against four Unity Mono
  titles spanning 2022.3 and Unity 6: Project Hardline (2022.3.27f1), Parasocial (2022.3.5f1),
  ROUNDS (2022.3.34f1), and The Gaspy Color War (6000.5.4f1).
- **M2 done:** a public scene-iteration API — `GameClass.FindObject()` (`TideCallOp.FindObject`,
  ABI slot 12), implemented as `FindObjectsOfType` + element 0 through the window-proc
  executor and verified in-game on Hardline 2022.3.27f1.

## Next

- **M3 — done.** Dev experience: `Nami.Sdk`/`Nami.Tide` NuGet packages (dotnet pack), the
   `dotnet new nami-mod` template (scaffolds a mod referencing the packages, with a
   Tide-usage example file that `--UseTide false` excludes), `nami install <game>` (stages a runnable
  root from the repo's build outputs, bundling the .NET runtime from the local install), and
  `nami run <mod.csproj>` (builds the mod, drops it into `nami/mods`, launches the game). A
  modder now goes idea → template → `nami run` without hand-staging.
  - **Shipped earlier (launcher slice):** the player-facing `nami launch` flow — `launch set
    <game.exe>`, `launch [offline|steam]` (Steam relay to a clean session after exit),
    `create` (double-click `launchNami.exe` + `run-with-nami.bat` in the nami root), and
    `doctor` reporting the configured game exe. Auto-detects the game as the largest `.exe`.
  - **Remaining (future "Nami-Install" product):** a self-contained downloadable installer
    that bundles the .NET runtime into a single artifact for end users (today `nami install`
    stages from a local build).
- **M4 — done (runtime bridge + v1 patching).** Same main-thread drain pattern for the runtime bridge.
  - **Shipped:** `WaveIl2Cpp` — native dispatch-stub detours on IL2CPP game methods
    (resolve `Il2CppMethodInfo` → `methodPointer` → jump-thunk following → detour;
    prefix observer + skip, raw register args, exact restore; install on the main
    thread via the window-proc executor). Machinery verified by native smoke tests
    (observe/skip/restore on a real function) + managed contract tests; in-game
    verification on a real IL2CPP title pending (`fixtures-dev/`).
  - **Shipped:** a working IL2CPP backend — loader auto-detects `GameAssembly.dll`, the
    managed Tide layer routes to `nami_il2cpp_*` exports, and ops run on the game's main
    thread inside its window procedure (subclassed drain). Verified live on D1AL-ogue
    (Unity 6000.0.61, real IL2CPP): typed `Debug.Log`, property getters (`Get<bool>`),
    enums (`Get<int>`), `string[]` reads, and exception surfacing (`FormatException`
    message) all work; the Mono backend is unchanged (regression-verified on ROUNDS).
  - **Empirical findings that shaped the design:** exports are E9 jmp-thunks; Unity 6
    metadata may be encrypted (older titles plaintext `0xFAB11BAF`); no il2cpp export
    fires per-frame (24 instrumented, all zero over 35 s) so a `runtime_invoke` detour
    cannot power the drain; no VM API is safe from a worker thread (even attached); and
    no VM call is safe *inside* a `runtime_invoke` detour frame. The safe context is the
    game's main thread inside its window proc — verified stable. `domain_assembly_open`
    returns an assembly, not an image (use `il2cpp_assembly_get_image`); GC handles are
    full 64-bit page-table indices (truncating to 32 bits AVs, as on Unity 6 Mono).
  - **Shipped:** offline `global-metadata.dat` parsing + `nami interop` (images/dump/generate/header):
    metadata v24-38 (single-byte XOR transparent) with calibrated struct strides and all three type-definition layouts
    (v24.1 92B / v27+ 88B / v35+ 84B); typed projection emits compilable `GameInterop.g.cs`
    (verified: compiles warning-free on a Unity 6000.0.61 title).
- **M5 — depth (done):**
  - **Shipped — hot reload:** generation-based live reload. A `FileSystemWatcher` (debounced,
    top-level `mods/*.dll` only) detects rebuilt/dropped/deleted mod DLLs; reloads run through
    a command queue drained between update ticks. A reload unloads the target plus all
    transitive dependents (reverse load order), releases the ALC, and loads the new generation
    in dependency order; a not-yet-loaded id (fresh drop) loads fresh. DLLs in subdirectories
    (`.nmod`-installed `mods/<id>/`) are discovered but not watched — reload via
    `Context.RequestReload()` or restart. Mod assemblies load from bytes, so files are never
    locked. Mods can self-reload via `Context.RequestReload()`. Verified by 7 dedicated tests
    (transitive reload, queued request, fresh drop, removal, watcher auto-load/auto-reload).
  - **Shipped — per-mod profiler:** histogram tick timings (avg/p95/max, allocation-free hot
    path), periodic summaries in `nami.log` under the `profiler` source, exposed to mods as
    `Context.Profiler` (`IModMetrics`). Config: `profiler.enabled`/`summaryIntervalSeconds`.
    Tide-op latency is wired too: `Tide.Call`/`CallInstance` record into the current
    `OnUpdate` profiler via `TideMetrics` (`UnityLog`/`InvokeStatic` excluded).
  - **Shipped — comparative bench gates:** `bench/Wave.Bench` patches identical
    `Add(int,int)->int` targets with same-shaped Wave-M2 vs HarmonyX (2.16.1, the exact
    fork BepInEx 6 ships) prefix+postfix pairs and gates exact restore (±25%), M1/M2
    absolute budgets, the Wave-vs-Harmony ratio (≤4x; measured ~0.6x on x64 Release),
    and result equality — nonzero exit on breach, budgets via `NAMI_GATE_*` env.
    `bench/Nami.Bench` gates load ms/plugin and update ms/frame the same way.
    Full-loader shootouts (BepInEx/MelonLoader boot-to-playable on a real title) stay a
    manual protocol — neither loader runs in CI: same game, same mod count, compare
    boot-to-first-tick from `nami.log` vs `LogOutput.log` plus steady-state RSS.

- **M6 — nami-inex legacy lane (Mono shipped: late-boot + early-boot).** Nami
  boots real BepInEx 5.x inside the game's own Mono — no Doorstop proxy, tree rooted at
  `nami/inex/`, managed by `nami inex install|enable|disable|status` plus a
  `nami/inex/enabled` sentinel the native loader reads: four DOORSTOP_* env vars
  (`PROCESS_PATH`, `MANAGED_FOLDER_DIR` derived as `<exe>_Data\Managed`, `INVOKE_DLL_PATH`
  pointing at `nami/inex/BepInEx/core/BepInEx.Preloader.dll`, `DLL_SEARCH_DIRS` pointing at
  `nami/inex/BepInEx/core`) + `Doorstop.Entrypoint.Start` on the game main thread (shared
  Tide detour toolkit; `mono_jit_init_version`/`mono_jit_init` on `mono-2.0-bdwgc.dll` or
  `mono.dll` are attempted in order, with E8-tolerant prologue decoding, rel32 fixup,
  and a 5-byte near-jump fallback for short prologues (verified: jit hook installs on
  Unity 2022.3 Mono; early `Start` fires via the suspended-main-thread install plus the
  Ldr load-watch, otherwise the late path covers deterministically).
  Window-visible-gated (as scene-live proxy, 180s timeout) +
  domain-stability-gated chainloader kick: `mono_domain_get` sampled on the main thread
  until 5 consecutive stable reads 1s apart (max 120 tries; any domain change resets the
  Start epoch so the new domain gets exactly one fresh Start), then one atomic drain call
  runs preloader `Start` + `Initialize(null,false,null)` + `Start()` via `PostInvoke`
  (outside any nested invoke frame; `Initialize`/`Start` carry BepInEx-side
  `_initialized`/`_loaded` guards). Verified manually (no in-repo fixture — games are
  gitignored): Hardline Logger 1.0.0 + Gaspy Menu 3.0.0 load and run on Project Hardline
  with boot logs identical to the Doorstop baseline; only automated coverage is the CLI
  file-ops suite (`InexCommandTests`).
  Shipped: boot-guard safe mode (vectored crash handler, fault containment on loader
  threads, `nami-crash.log` + `safe-mode` markers, 3-boot auto-recovery — see
  docs/architecture.md). Remaining: BepInEx 6 / IL2CPP lane (own CoreCLR, interop
  orchestration), legacy-pack distribution.

## Verified in-game evidence

Tide's in-game probe (the `TideProbe` sample) exercises: the bridge self-test, typed
`Debug.Log(object)` with string and boxed-int args, `new GameObject()` +
`GetInstanceID()`, the generic typed API (`Get<bool>`/`Set<bool>`), an enum property read as
its underlying int, a `string[]` read via `TideArrays`, and — once the scene loads — live
scene-object access via `Camera.main`. Representative `nami.log` (ROUNDS / 2022.3.34f1;
timestamps elided; see `docs/tide.md` §4 for the full transcript):

```
[INFO ] [boot] Nami managed runtime booting (nami_root=...\nami)
[INFO ] [boot] Tide bridge OK: Unity Debug.Log executed on the game main thread
[INFO ] [chainloader] Loaded dev.nami.samples.tideprobe 0.1.0 (TideProbe.dll)
[INFO ] [dev.nami.samples.tideprobe] typed Debug.Log(string) OK
[INFO ] [dev.nami.samples.tideprobe] typed Debug.Log(int) OK (primitive arg marshaled)
[INFO ] [dev.nami.samples.tideprobe] created GameObject instance (handle=7688)
[INFO ] [dev.nami.samples.tideprobe] GameObject.GetInstanceID() = -62
[INFO ] [dev.nami.samples.tideprobe] Application.runInBackground (typed Get<bool>) = True
[INFO ] [dev.nami.samples.tideprobe] QualitySettings.shadowResolution (enum via Get<int>) = 0
[INFO ] [dev.nami.samples.tideprobe] Environment.GetCommandLineArgs() length = 1
[INFO ] [dev.nami.samples.tideprobe] args[0] = 'C:\...\ROUNDS.exe'
[INFO ] [dev.nami.samples.tideprobe] TideProbe boot checks complete; scene probe fires after the scene loads
... (~10 s later)
[INFO ] [dev.nami.samples.tideprobe] Camera.main found live instance (handle=165640)
[INFO ] [dev.nami.samples.tideprobe] live Camera.name = 'MainCamera'
[INFO ] [dev.nami.samples.tideprobe] TideProbe verification complete
game alive and stable (4000+ ticks), zero crashes
```

Verified titles: Project Hardline (2022.3.27f1), Parasocial (2022.3.5f1), ROUNDS
(2022.3.34f1), and The Gaspy Color War (Unity 6 / 6000.5.4f1) — each ran the bridge
self-test, typed calls, object creation/instance calls, and the generic/enum/array checks
with the game stable; ROUNDS additionally verified live scene-object access (`Camera.main`
→ `MainCamera`).

Unity 6 exposed (and fixed) a GCHandle ABI issue: its Mono stores handles as 64-bit encoded
pointers that can live above 4 GB, which the legacy `mono_gchandle_*` entry points truncate.
Tide now uses the full-64-bit `mono_gchandle_*_v2` variants (see `docs/tide.md` §6).
