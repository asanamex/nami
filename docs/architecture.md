# Nami Architecture

This document describes Nami's architecture as implemented (working end-to-end in
real Unity games: load, patch, and typed game access on Mono and IL2CPP).

## Goal

A Unity mod loader (Mono **and** IL2CPP) with no dependency on
BepInEx/HarmonyX/MonoMod/Cecil.

## The core idea: bring your own runtime

BepInEx-Mono plugins execute inside the game's embedded Mono (old BCL, no ALCs,
no hot reload). Nami instead:

1. **Injects a native loader** into the game process at startup (no proxy files in the game dir).
2. The loader **waits for Unity's Mono** to initialize, then **hosts a bundled modern .NET
   (CoreCLR via hostfxr)** inside the game process.
3. The managed `Nami.Runtime.Boot.Run` starts the **chainloader**, which loads each mod into its
   own unloadable `AssemblyLoadContext`.
4. Mods run on .NET 10 with a modern BCL, real GC, ALC isolation, and crash quarantine -
   never on the game's runtime.

Plugins get a modern runtime on *both* Mono and IL2CPP games (the IL2CPP side uses
the same hosting machinery; only the game-type bridge differs).

## Component map

```
native/                          C++17 (Windows x64 first)
  injector/injector_main.cpp     inject_into_game(): CreateProcessW(suspended) →
                                 VirtualAllocEx(path) → WriteProcessMemory →
                                 CreateRemoteThread(LoadLibraryW) → hook-ready wait
                                 (30s) → ResumeThread
  injector/injector_exe.cpp      nami_boot.exe entry (wmain): parses <game> <loader> [--root]
  loader/loader_exports.cpp      nami_loader.dll: DllMain spawns the boot thread
                                 (CreateThread) - no separate export is called by the injector
  loader/loader_main.cpp         boot thread: boot-guard crash handler + safe-mode check
                                 FIRST (core/bootguard.cpp), then waits for the game runtime
                                 - mono-2.0-bdwgc.dll / mono.dll OR GameAssembly.dll (60s),
                                 then hosts CoreCLR
  loader/tide_pump.cpp           Tide Mono main-thread executor: mono_runtime_invoke hook + pre/
                                 post drain queues, install lock, re-entrancy guard; shared
                                 detour toolkit (measure_relocatable_prologue /
                                 build_trampoline / install_native_detour (+ try_ variant -
                                 14-byte mov rax,jmp rax, 5-byte near-jump fallback;
                                 opt-in E8 rel32 fixup and RIP-relative disp32 fixup
                                 (incl. 0F-prefixed SIMD loads); trampolines allocated
                                 within ±2GB of the target so fixups always reach)
  loader/inex_bootstrap.h/.cpp   nami-inex legacy lane: arm() 0/1/2 (payload+sentinel gating),
                                 DOORSTOP_* env, mono_jit_init_version/mono_jit_init detour
                                 attempt (E8-tolerant + near-jump, Ldr load-watch,
                                 hook-ready signal), watcher thread
                                 (window + domain-stability gates, late Start + kick)
  loader/tide_ops.cpp            Tide native ops (UnityLog, parameterless InvokeStatic)
  loader/tide_objects.cpp        Tide Mono typed game access (field/property/method/object/array
                                 ops, GCHandle *_v2 handles, exception surfacing)
  loader/tide_member_cache.h/.cpp
                                 shared member-resolution memoization (SRW-guarded open-addressing
                                 map, process lifetime, incl. negative results; tagged per backend)
  loader/tide_il2cpp.cpp         Tide IL2CPP main-thread executor: window-proc drain (subclasses
                                 the game's main window; ops run inside its message pump)
  loader/tide_il2cpp_ops.cpp     Tide IL2CPP typed game access (mirrors tide_objects.cpp against
                                 the il2cpp_* exports)
  loader/tide_il2cpp_patch.cpp   IL2CPP method patching (WaveIl2Cpp backend): resolve
                                 Il2CppMethodInfo on the main thread, follow jump thunks,
                                 install/remove dispatch-stub detours (nami_il2cpp_hook/unhook);
                                 covers short prologues (5-byte near jump) incl. leaf
                                 getters and Unity 6 lazy-init thunks
  loader/native_stub.cpp         dispatch-stub detours for native (IL2CPP) method hooks: fast
                                 path (save arg regs → managed dispatch observe/skip →
                                 tail-jump the trampoline or return) and full path (all args
                                 incl. stack, prefix/postfix dispatch, result save/rewrite
                                 via a 2-slot rax/xmm0 pointer, all state above a reserved
                                 callee scratch zone); exact restore (shared with smoke tests)
  loader/tide_abi.h              shared Tide value/request ABI (TideValue, CallRequest, BatchRequest)
  core/runtime_host.cpp          hostfxr: initialize_for_runtime_config → get_runtime_delegate(
                                 hdt_load_assembly_and_get_function_pointer) →
                                 load_assembly_and_get_function_pointer(Nami.Runtime.dll,
                                 ComponentEntry.EntryPoint, UNMANAGEDCALLERSONLY sentinel)
  smoke/smoke_main.cpp           native toolchain smoke test (ctest)

src/Nami.Runtime/                managed in-game bootstrap
  ComponentEntry.cs              [UnmanagedCallersOnly] entry, BootArgs struct, catch→log
  Boot.cs                        LogHub+FileSink (nami.log), config load, chainloader, update loop
src/Nami.Tide/                   bridge API mods call (see docs/tide.md)
src/Nami.Core/                   chainloader (ALC per mod, quarantine, resolver, discovery,
                                 hot reload: generations, command queue drained between ticks,
                                 FileSystemWatcher with debounce; per-mod profiler in
                                 Profiling/ModProfiler.cs - histogram tick timings logged on an
                                 interval and exposed to mods as IModMetrics)
src/Nami.Wave/                   patching engine (x64 inline detours + IL-copy patches)
src/Nami.Sdk/                    public plugin API ([NamiPlugin], NamiPlugin, PluginInfo, ...)
src/Nami.Cli/                    `nami` console tool: version/doctor/list/help + install (stages a
                                  runnable root from build outputs via Stager) + run <mod.csproj>
                                  (builds a mod, copies it into nami/mods, launches) + interop
                                  (offline IL2CPP projection: images/dump/generate/header) + inex
                                  (legacy lane: install/enable/disable/status; Stager stages,
                                  InexCommand manages) +
                                  the launcher flow - launch set <game.exe> (stored in nami.json), launch
                                 [offline|steam] (spawns native/nami_boot.exe; Steam relay to
                                 steam://rungameid/<id> after the game exits), create
                                 (self-extracts launchNami.exe + run-with-nami.bat into the nami
                                 root). Game exe auto-detection (GameLocator) picks the largest
                                 .exe, skipping crash handlers/updaters.
  Stager.cs                      stages <game>/nami from the repo build outputs: managed runtime,
                                 native injector/loader, bundled .NET runtime, mods/, nami.json;
                                 and the Nami-Install artifact flow - Pack() (self-contained
                                 zip + SHA-256 manifest.json) / InstallFromArtifact()
                                 (hash-verified, upgrade-safe extract; local path or URL)
  PackCommand.cs                 `nami pack [out.zip]` - builds the installer artifact
                                 (managed + native + bundled .NET runtime, one zip)
tools/launch-shim/               launchNami.exe - tiny self-contained console app, embedded in
                                 Nami.Cli; spawns nami_boot.exe with paths from its own location
tools/templates/nami-mod/        `dotnet new nami-mod` template: a net10.0 mod project referencing
                                 the Nami.Sdk/Nami.Tide NuGet packages (+ optional TideExample.cs)
samples/HelloNami/               example mod (log-only)
samples/TideProbe/               in-game proof of Tide typed access (generic API, enums, arrays,
                                 Camera.main scene access) - Mono titles
samples/TideProbeIl2Cpp/         in-game proof of the Tide IL2CPP backend (same API on
                                 GameAssembly.dll titles)
```

## Boot sequence (verified in-game)

1. `nami_boot.exe` launches the game suspended, injects `nami_loader.dll` via the classic
   LoadLibraryW remote-thread pattern, holds the game main thread until the loader
   signals hook-ready (30s timeout), then resumes the game.
2. Loader thread polls for the game's runtime - `mono-2.0-bdwgc.dll`/`mono.dll` on Mono
   titles, `GameAssembly.dll` on IL2CPP titles (Unity initialized) - then hosts CoreCLR:
   - `hostfxr_initialize_for_runtime_config(<nami>/Nami.Runtime.runtimeconfig.json)`
   - `hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer)` - **note: the
     enum value is 5**, not 1 (com/winrt types come first); passing 1 returns the wrong delegate.
   - `load_assembly_and_get_function_pointer(Nami.Runtime.dll, "Nami.Runtime.ComponentEntry,
     Nami.Runtime", "EntryPoint", (const wchar_t*)-1 /*UNMANAGEDCALLERSONLY sentinel*/)`
     - the delegate_type sentinel must be `(char_t*)-1`, not the literal string.
3. `ComponentEntry.EntryPoint` parses the `BootArgs` blob (wide root path + Mono module
   handle - null on IL2CPP, currently unused by `Boot.Run`), calls `Boot.Run`.
4. `Boot.Run` writes `nami.log`, loads `nami.json` config, starts the chainloader, starts the
   hot-reload file watcher, deletes the boot-guard `boot-pending` marker (the native boot
   phase is over), and spins the update loop on the boot thread (16 ms ticks). Each
   tick drains queued reload commands first, then updates active mods (profiler-timed when
   `profiler.enabled`).

## Layout of a Nami root (`<game>/nami/`)

```
dotnet/host/fxr/<ver>/hostfxr.dll
dotnet/shared/Microsoft.NETCore.App/<ver>/   (bundled runtime)
Nami.Runtime.dll  Nami.Runtime.deps.json  Nami.Runtime.runtimeconfig.json
Nami.Core.dll     Nami.Sdk.dll           Nami.Tide.dll
native/nami_boot.exe  native/nami_loader.dll
launchNami.exe  run-with-nami.bat        (written by `nami create`)
mods/*.dll                               (loose plugin DLLs + mods/<id>/ from .nmod
                                         extraction; the watcher covers top-level DLLs -
inex/BepInEx/{core,plugins,patchers,config} (optional legacy payload; cache/ never
                                         copied by `nami inex install`)
inex/enabled                             (sentinel file; absent = pure Nami boot)
native/nami-inex.log                     (legacy-lane log, only when armed)
inex/BepInEx/LogOutput.log               (BepInEx's own log, last-run evidence)
boot-pending / safe-mode / nami-crash.log  (boot-guard markers - see below)
nami.json                                  (written by `nami install`; `launch set` adds gameExe)
nami.log                                   (runtime log)
```

The root is created by `nami install <game>` - either staged from the repo's build outputs
(dev flow) or extracted from the self-contained Nami-Install artifact (`nami pack` →
`nami install <game> --from <zip|url>`; end-user flow). The artifact is the nami-root layout
in one zip (managed + native + bundled .NET runtime) with a SHA-256 `manifest.json`;
install verifies every file against it, refuses tampered entries without clobbering a working
install, and replaces only framework files on upgrade (mods/, inex/, logs and boot-guard
markers survive). `nami run`/`nami launch`/`launchNami.exe` then invoke
`native/nami_boot.exe <game.exe> native/nami_loader.dll`; the loader derives the root as two
levels up and the game executable comes from `nami.json` (`gameExe`).

 Legacy lane (Mono only, non-blocking): if `inex/BepInEx/core/BepInEx.Preloader.dll`
  **and** `inex/enabled` both exist, `inex::arm` sets the four `DOORSTOP_*` env vars,
  attempts `mono_jit_init_version`/`mono_jit_init` detours for Doorstop-timed `Start`
  (E8-tolerant prologue handling + near-jump fallback, Ldr load-watch for
  dynamically-loaded Mono) and signals hook-ready, then spawns a watcher
  thread (drain fallback + window/domain-gated chainloader kick). IL2CPP titles skip
  this entirely. The loader thread proceeds to CoreCLR hosting immediately either way.

## Tide (game access, opt-in)

**Tide** lets mods call into the game's Mono runtime from Nami's .NET. The core constraint
is thread affinity: Mono calls must run on the game's main thread - direct calls from a
CoreCLR thread crash CoreCLR's GC, and calls from a foreign native thread crash Mono's Boehm
GC. Tide hooks `mono_runtime_invoke` (called constantly by the game main thread) with a safe
native detour and drains queued ops inline on the main thread. On top of that it provides
**typed game access**: static/instance field and property read/write, typed method calls, and
live object creation/calls through GC-handle-backed handles (`GameClass`/`GameObject`).
Verified in-game: `Debug.Log`, typed string/int calls, `new GameObject()`, instance method
calls - game stable. The flag gates the boot self-test (`[boot] attaching Tide bridge...`);
mod-issued Tide calls route to the loader-detected backend once the loader is present.
Consumers beyond mods: the inex legacy lane reuses the drain (post-invoke late sequence)
and the shared detour toolkit (`mono_jit_init*` hooks). Full details: `docs/tide.md`.

## Inex (legacy BepInEx lane, Mono only)

Unmodified BepInEx 5.x mods run inside the game's own Mono, managed by Nami instead of
Doorstop's proxy - no `winhttp.dll`, no `doorstop_config.ini`; the tree lives at
`nami/inex/BepInEx/` and Nami's injector is the only thing that touches the game.

- **Arm** (`inex::arm`, non-blocking): proceeds only if `inex/BepInEx/core/
  BepInEx.Preloader.dll` **and** the `inex/enabled` sentinel both exist (codes 0/1/2 =
  absent/disabled/armed). Sets the four `DOORSTOP_*` env vars, attempts a
  `mono_jit_init` detour for Doorstop-timed `Start`, spawns the watcher thread, and
  returns - the loader thread proceeds to CoreCLR hosting immediately either way.
- **Why not just invoke Start late**: the preloader patches the one-shot entrypoint
  (`Application..cctor`) into an already-loaded CoreModule to no effect, so a late
  `Start` alone leaves the chainloader permanently unfired (observed: config written,
  then eternal silence). Worse, invoking anything through pre-reload Mono handles
  kills the process silently - Unity reloads the script domain mid-boot.
- **Epoch logic**: `mono_domain_get()` is sampled on the main thread until 5 consecutive
  stable reads 1s apart (max 120 tries); every observed domain change resets the
  Start epoch so the new domain gets exactly one fresh `Start`.
- **Kick**: one atomic drain call runs preloader `Start` + `Chainloader.Initialize(null,
  false, null)` + `Chainloader.Start()` via `PostInvoke` (outside any nested invoke
  frame - scene-iteration APIs abort from re-entry), after a visible game window
  (scene-live proxy, 180s timeout) **and** domain stability. Redundant firings are
  safe: preloader `Start` is epoch-guarded, `Initialize`/`Start` carry BepInEx-side
  `_initialized`/`_loaded` guards.
- **Boundaries**: legacy mods share game Mono (no ALC isolation, no quarantine, no
  hot-reload - a legacy crash is a game crash); `nami inex disable` returns to pure
  Nami. IL2CPP titles skip `arm` (BepInEx 6 needs its own CoreCLR lane).

## Boot-guard (crash safety on the native path)

The native boot path (inex arm, runtime wait, hostfxr hosting, the Tide self-test) runs
inside the game process; a fault there used to take the whole game down with zero recovery.
Boot-guard fixes that (native/core/bootguard.cpp):

- **Crash handler first.** The loader installs a vectored exception handler before any risky
  work. Faults on Nami-owned threads (the loader boot thread) are **contained**: the
  injector's hook-ready event is signaled and the faulting thread is terminated - the game
  main thread resumes and the game boots clean and unmodded.
- **Evidence + safe mode.** Any fault while `boot-pending` exists (i.e. during the native
  boot phase) appends a record to `nami-crash.log` (stage, code, rip, address, thread) and
  writes `safe-mode`. On the next launch the loader skips inex + CoreCLR hosting entirely
  and the game boots unmodded.
- **Auto-recovery.** `safe-mode` carries a boots-remaining counter (3); each clean boot
  decrements it and at 0 the marker is deleted and Nami is fully back. Delete
  `<root>/safe-mode` manually (or wait 3 boots) to restore Nami immediately.
- **Only hard faults are contained.** The VEH classifies by exception code: access
  violations, illegal instructions, stack overflow, division-by-zero, privileged
  instructions, in-page errors, /GS and heap-corruption codes. Catchable software
  exceptions pass through untouched - C++ throws (0xE06D7363) and .NET exceptions
  (0xE0434352, raised by CoreCLR for every managed `throw`) are normal control flow,
  not crashes. (Regression: the first version's high-bit catch-all killed Nami-owned
  threads on benign C++/.NET exceptions during managed boot - caught by the smoke suite.)
- **Boundary.** `Boot.Run` deletes `boot-pending` once the update loop is ticking - crashes
  after that point are runtime crashes (mods, Tide) and do not trigger safe mode; managed
  mod failures stay in the quarantine path.
- `nami doctor` reports the boot-guard state.

## Design notes

- Discovery probes plugin DLLs in a throwaway collectible ALC; `Nami.Sdk`/framework refs resolve
  to an already-loaded copy (the hostfxr component ALC in-game, default ALC in tests) so types
  unify. Plugin-to-plugin deps are validated by `DependencyResolver`, not the probe. Both the
  probe and the real load read assemblies from raw bytes, so mod files are never locked -
  a rebuilt DLL can overwrite itself in place while the game runs (hot reload prerequisite).
- Each plugin loads into its own collectible `PluginLoadContext`. Quarantine disables a throwing
  plugin after N consecutive failures (state `Quarantined`, `OnUnload` called best-effort, then
  the ALC is unloaded). All structural changes (quarantine unload, watcher-driven reload) are
  queued and drained between update ticks, so reloads never race a running `OnUpdate`.
- Hot reload unloads the target plus every transitive dependent (reverse load order), then
  reloads the reload-set ids that still have manifests on disk in dependency order; a reload
  of an id that is not yet loaded (fresh mod drop) loads it fresh. `PluginReloaded` reports
  old/new generations.
- The per-mod profiler buckets tick durations into a log-scale histogram (allocation-free hot
  path) and logs a per-mod summary every `profiler.summaryIntervalSeconds`; mods read the same
  numbers in-process via `Context.Profiler` (`IModMetrics` on the SDK).
- The injected `nami_loader.dll` is fully statically linked (no MinGW runtime DLL deps) so
  `LoadLibraryW` succeeds inside the game process.
- Logging fans out to sinks (console/file); a broken sink can never crash the host.

## Future layers

- Inex: early-boot fidelity shipped (injector holds the main thread until hook-ready;
  Ldr load-watch catches dynamically-loaded Mono; `early preloader start rc=0`
  verified) and boot-guard safe mode shipped (crash containment + auto-recovery -
  see above); BepInEx 6 / IL2CPP lane (own CoreCLR + interop
  orchestration) and legacy-pack distribution remain.
- IL2CPP patching: dispatch-stub hooks shipped and verified in-game (WaveIl2Cpp -
  fast path observe/skip with raw pointer args, full path with all-args +
  result observation/rewriting via `HookFull`, short-prologue + RIP-relative support
  incl. Unity 6 lazy-init thunks, see docs/tide.md §9). Remaining vs BepInEx 6:
  argument marshaling to managed types (raw slots only today) and per-title coverage.
- Shipped: `Nami.Interop` - offline (dev-time) typed projection for IL2CPP modders
  (`nami interop images/dump/generate/header`; metadata v24-38, verified on a
  Unity 6000.0.61 title - see `src/Nami.Interop/Il2CppMetadata.cs`).
- `.nmod` distribution (zip + `mod.json`): the `NamiPackage` read/install library is
  implemented and tested, and `nami nmod info|install` is shipped. Per-plugin config
  (`pluginConfig.<id>` → `Context.Config`) is shipped.
- Wave-vs-HarmonyX comparative bench gates are shipped; full-loader shootouts
  (BepInEx/MelonLoader boot-to-playable on a real title) stay a manual protocol.
  (Hot reload and the per-mod profiler are shipped - see the chainloader notes above.)
