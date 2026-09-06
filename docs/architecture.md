# Nami Architecture

This document describes Nami's architecture at M0 (working end-to-end in a real Unity Mono game).

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

## Component map (M0)

```
native/                          C++17 (Windows x64 first)
  injector/injector_main.cpp     nami_boot.exe: CreateProcessW(suspended) →
                                VirtualAllocEx(path) → WriteProcessMemory →
                                CreateRemoteThread(LoadLibraryW) → ResumeThread
  loader/loader_exports.cpp      nami_loader.dll: DllMain (empty) + nami_loader_start export
  loader/loader_main.cpp         boot thread: waits for mono-2.0-bdwgc.dll (30s), UTF-8 root
  core/runtime_host.cpp          hostfxr: initialize_for_runtime_config → get_runtime_delegate(
                                hdt_load_assembly_and_get_function_pointer) →
                                load_assembly_and_get_function_pointer(Nami.Runtime.dll,
                                ComponentEntry.EntryPoint, UNMANAGEDCALLERSONLY sentinel)

src/Nami.Runtime/                managed in-game bootstrap
  ComponentEntry.cs              [UnmanagedCallersOnly] entry, BootArgs struct, catch→log
  Boot.cs                        LogHub+FileSink (nami.log), config load, chainloader, update loop
  MonoBridge.cs                  OPT-IN reverse-interop into game Mono (see below)

src/Nami.Core/                   chainloader (ALC per mod, quarantine, resolver, discovery)
src/Nami.Sdk/                    public plugin API ([NamiPlugin], NamiPlugin, PluginInfo, ...)
samples/HelloNami/               example mod
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
Nami.Core.dll     Nami.Sdk.dll
native/nami_loader.dll                     (loader derives root as two levels up)
mods/*.dll                                 (loose plugin DLLs in M0; .nmod later)
nami.json                                  (optional config)
nami.log                                   (runtime log)
```

## The Mono bridge (experimental, opt-in)

`MonoBridge` resolves the game Mono's embedding API (`mono_get_root_domain`,
`mono_thread_attach`, `mono_assembly_loaded`, `mono_class_from_name`,
`mono_class_get_method_from_name`, `mono_runtime_invoke`, `mono_string_new`) as raw function
pointers from the `mono-2.0-bdwgc.dll` module handle and can invoke `UnityEngine.Debug.Log`
from the Nami runtime — "reverse interop", the Mono analogue of Il2CppInterop.

**Status:** resolves the root domain and attaches the thread successfully, but the first
`mono_assembly_loaded` call crashes CoreCLR at a fixed offset — a CoreCLR↔Mono (Boehm GC) interop
issue. It is disabled by default (`enableMonoBridge: false` in `nami.json`) so the loader is
stable; hardening it is a tracked research item.

## Design notes

- Discovery probes plugin DLLs in a throwaway collectible ALC; `Nami.Sdk`/framework refs resolve
  to an already-loaded copy (the hostfxr component ALC in-game, default ALC in tests) so types
  unify. Plugin-to-plugin deps are validated by `DependencyResolver`, not the probe.
- Each plugin loads into its own collectible `PluginLoadContext`; shared framework refs reuse the
  already-loaded copy. Quarantine disables a throwing plugin after N consecutive failures.
- The injected `nami_loader.dll` is fully statically linked (no MinGW runtime DLL deps) so
  `LoadLibraryW` succeeds inside the game process.
- Logging fans out to sinks (console/file); a broken sink can never crash the host.

## Future layers

- `Nami.Projection` — lazy game-type projection to managed, content-addressed cache.
- `Nami.Patch` — detour-based patch engine with a HarmonyX-familiar API.
- `Nami.Interop` — offline (dev-time) reference assembly generation for IL2CPP modders.
- IL2CPP bridge — same hosting, plus native metadata reading of `global-metadata.dat`.
- `.nmod` packaging, hot reload, per-mod profiler, comparative bench gates.
