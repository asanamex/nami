# Nami Architecture

This document describes Nami's architecture as implemented (working end-to-end in a real
Unity Mono game: load, patch, and typed game access).

## Goal

A Unity mod loader (Mono **and** IL2CPP) engineered from scratch to be faster, safer, and more
modern than BepInEx — with a clean-slate API and zero dependency on BepInEx/HarmonyX/MonoMod/Cecil.

## The core idea: bring your own runtime

BepInEx-Mono plugins execute inside the game's ancient embedded Mono (net35-era BCL, no ALCs,
no hot reload). Nami instead:

1. **Injects a native loader** into the game process at startup (no proxy files in the game dir).
2. The loader **waits for Unity's Mono** to initialize, then **hosts a bundled modern .NET
   (CoreCLR via hostfxr)** inside the game process.
3. The managed `Nami.Runtime.Boot.Run` starts the **chainloader**, which loads each mod into its
   own unloadable `AssemblyLoadContext`.
4. Mods run on .NET 10 with a modern BCL, real GC, ALC isolation, and crash quarantine —
   never on the game's runtime.

This is the architectural break from BepInEx: plugins get a *modern* runtime on *both* Mono and
IL2CPP games (the IL2CPP side uses the same hosting machinery; only the game-type bridge differs).

## Component map

```
native/                          C++17 (Windows x64 first)
  injector/injector_main.cpp     inject_into_game(): CreateProcessW(suspended) →
                                 VirtualAllocEx(path) → WriteProcessMemory →
                                 CreateRemoteThread(LoadLibraryW) → ResumeThread
  injector/injector_exe.cpp      nami_boot.exe entry (wmain): parses <game> <loader> [--root]
  loader/loader_exports.cpp      nami_loader.dll: DllMain spawns the boot thread
                                 (CreateThread) — no separate export is called by the injector
  loader/loader_main.cpp         boot thread: waits for mono-2.0-bdwgc.dll / mono.dll (30s),
                                 UTF-8 root
  loader/tide_pump.cpp           Tide main-thread executor: mono_runtime_invoke hook + pre/post
                                 drain queues, install lock, re-entrancy guard
  loader/tide_ops.cpp            Tide native ops (UnityLog, parameterless InvokeStatic)
  loader/tide_objects.cpp        Tide typed game access (field/property/method/object/array ops,
                                 GCHandle *_v2 handles, exception surfacing)
  loader/tide_abi.h              shared Tide value/request ABI (TideValue, CallRequest)
  core/runtime_host.cpp          hostfxr: initialize_for_runtime_config → get_runtime_delegate(
                                 hdt_load_assembly_and_get_function_pointer) →
                                 load_assembly_and_get_function_pointer(Nami.Runtime.dll,
                                 ComponentEntry.EntryPoint, UNMANAGEDCALLERSONLY sentinel)
  smoke/smoke_main.cpp           native toolchain smoke test (ctest)

src/Nami.Runtime/                managed in-game bootstrap
  ComponentEntry.cs              [UnmanagedCallersOnly] entry, BootArgs struct, catch→log
  Boot.cs                        LogHub+FileSink (nami.log), config load, chainloader, update loop
src/Nami.Tide/                   bridge API mods call (see docs/tide.md)
src/Nami.Core/                   chainloader (ALC per mod, quarantine, resolver, discovery)
src/Nami.Wave/                   patching engine (x64 inline detours + IL-copy patches)
src/Nami.Sdk/                    public plugin API ([NamiPlugin], NamiPlugin, PluginInfo, ...)
src/Nami.Cli/                    `nami` console tool: version/doctor/list + install (stages a
                                 runnable root from build outputs via Stager) + run <mod.csproj>
                                 (builds a mod, copies it into nami/mods, launches) + the launcher
                                 flow — launch set <game.exe> (stored in nami.json), launch
                                 [offline|steam] (spawns native/nami_boot.exe; Steam relay to
                                 steam://rungameid/<id> after the game exits), create
                                 (self-extracts launchNami.exe + run-with-nami.bat into the nami
                                 root). Game exe auto-detection (GameLocator) picks the largest
                                 .exe, skipping crash handlers/updaters.
  Stager.cs                      stages <game>/nami from the repo build outputs: managed runtime,
                                 native injector/loader, bundled .NET runtime, mods/, nami.json
tools/launch-shim/               launchNami.exe — tiny self-contained console app, embedded in
                                 Nami.Cli; spawns nami_boot.exe with paths from its own location
tools/templates/nami-mod/        `dotnet new nami-mod` template: a net10.0 mod project referencing
                                 the Nami.Sdk/Nami.Tide NuGet packages (+ optional TideExample.cs)
samples/HelloNami/               example mod (log-only)
samples/TideProbe/               in-game proof of Tide typed access (generic API, enums, arrays,
                                 Camera.main scene access)
```

## Boot sequence (verified in-game)

1. `nami_boot.exe` launches the game suspended, injects `nami_loader.dll` via the classic
   LoadLibraryW remote-thread pattern, resumes the game.
2. Loader thread polls for `mono-2.0-bdwgc.dll` (Unity Mono initialized), then hosts CoreCLR:
   - `hostfxr_initialize_for_runtime_config(<nami>/Nami.Runtime.runtimeconfig.json)`
   - `hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer)` — **note: the
     enum value is 5**, not 1 (com/winrt types come first); passing 1 returns the wrong delegate.
   - `load_assembly_and_get_function_pointer(Nami.Runtime.dll, "Nami.Runtime.ComponentEntry,
     Nami.Runtime", "EntryPoint", (const wchar_t*)-1 /*UNMANAGEDCALLERSONLY sentinel*/)`
     — the delegate_type sentinel must be `(char_t*)-1`, not the literal string.
3. `ComponentEntry.EntryPoint` parses the `BootArgs` blob (wide root path + mono module handle),
   calls `Boot.Run`.
4. `Boot.Run` writes `nami.log`, loads `nami.json` config, starts the chainloader, and spins the
   update loop on the boot thread (16 ms ticks).

## Layout of a Nami root (`<game>/nami/`)

```
dotnet/host/fxr/<ver>/hostfxr.dll
dotnet/shared/Microsoft.NETCore.App/<ver>/   (bundled runtime)
Nami.Runtime.dll  Nami.Runtime.deps.json  Nami.Runtime.runtimeconfig.json
Nami.Core.dll     Nami.Sdk.dll           Nami.Tide.dll
native/nami_boot.exe  native/nami_loader.dll
launchNami.exe  run-with-nami.bat        (written by `nami create`)
mods/*.dll                                 (loose plugin DLLs; .nmod later)
nami.json                                  (written by `nami install`; `launch set` adds gameExe)
nami.log                                   (runtime log)
```

The root is created by `nami install <game>` (Stager stages the managed runtime, native
injector/loader and a bundled .NET runtime from the repo's build outputs); `nami run`/`nami
launch`/`launchNami.exe` then invoke `native/nami_boot.exe <game.exe> native/nami_loader.dll`;
the loader derives the root as two levels up and the game executable comes from `nami.json`
(`gameExe`).

## Tide (game access, opt-in)

**Tide** lets mods call into the game's Mono runtime from Nami's .NET. The core constraint
(learned the hard way): Mono calls must run on the game's main thread — direct calls from a
CoreCLR thread crash CoreCLR's GC, and calls from a foreign native thread crash Mono's Boehm
GC. Tide hooks `mono_runtime_invoke` (called constantly by the game main thread) with a safe
native detour and drains queued ops inline on the main thread. On top of that it provides
**typed game access**: static/instance field and property read/write, typed method calls, and
live object creation/calls through GC-handle-backed handles (`GameClass`/`GameObject`).
Verified in-game: `Debug.Log`, typed string/int calls, `new GameObject()`, instance method
calls — game stable. Opt-in via `"enableMonoBridge": true`. Full details: `docs/tide.md`.

## Design notes

- Discovery probes plugin DLLs in a throwaway collectible ALC; `Nami.Sdk`/framework refs resolve
  to an already-loaded copy (the hostfxr component ALC in-game, default ALC in tests) so types
  unify. Plugin-to-plugin deps are validated by `DependencyResolver`, not the probe.
- Each plugin loads into its own collectible `PluginLoadContext`; shared framework refs reuse the
  already-loaded copy. Quarantine disables a throwing plugin after N consecutive failures
  (state `Quarantined`, `OnUnload` called best-effort; the ALC itself is not unloaded).
- The injected `nami_loader.dll` is fully statically linked (no MinGW runtime DLL deps) so
  `LoadLibraryW` succeeds inside the game process.
- Logging fans out to sinks (console/file); a broken sink can never crash the host.

## Future layers

- Tide: Unity scene-iteration scan APIs (`FindObjectOfType`) via a Wave-installed per-frame
  script callback; a generated strongly-typed projection layer over the generic `Get<T>` API.
- `Nami.Interop` — offline (dev-time) reference assembly generation for IL2CPP modders.
- IL2CPP bridge — same hosting, plus native metadata reading of `global-metadata.dat`.
- `.nmod` packaging, hot reload, per-mod profiler, comparative bench gates.
