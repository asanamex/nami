# Nami vs BepInEx — where the difference is real

This is the honest comparison, not marketing. BepInEx is mature and works; Nami is new and
chooses a different architecture. These are the concrete ways that architecture pays off —
and the costs.

## What Nami does differently

### 1. Plugins run on a modern runtime, even in Mono games

BepInEx-Mono loads plugins **into the game's own embedded Mono** — the .NET Framework 3.5-era
runtime Unity ships. Plugins are stuck with that BCL, that GC, that JIT (or lack of one).

Nami **hosts its own .NET 10 (CoreCLR) inside the game process** and runs plugins there —
modern BCL, modern GC, real `AssemblyLoadContext`s, `Span`, `async`, source generators,
whatever the ecosystem has today. BepInEx-Mono cannot do this at all; it is the single biggest
architectural gap.

### 2. Real isolation and crash quarantine

- BepInEx plugins share the game's single AppDomain: one plugin's `static` state, assembly
  resolution and exceptions bleed into every other plugin and the game.
- Nami loads each mod into its **own unloadable `AssemblyLoadContext`** — separate static
  state, separate resolution — and **hot-reloads mods live**: rebuild or drop a top-level DLL
  into `nami/mods` and it swaps into a new generation without restarting the game (dependents
  reload with it; mod files are never locked; subdirectory DLLs are discovered but not watched).
- A mod that throws repeatedly is **quarantined** (auto-disabled with a report) while the
  game keeps running.

### 3. No proxy files in the game directory

BepInEx installs a `winhttp.dll` proxy + `doorstop_config.ini` into the game root and
overrides Unity's DLL resolution. Nami's `nami_boot.exe` launches the game and injects a
loader (a remote thread calls `LoadLibraryW` on `nami_loader.dll`) — the game folder itself
stays **pristine** (everything lives under `nami/`).

### 4. No runtime interop generation on the player's machine

BepInEx-IL2CPP's first launch runs a minutes-long Cpp2IL + generator pipeline and preloads
hundreds of interop assemblies (100-400 MB). Nami's IL2CPP backend calls the game's own
`il2cpp_*` runtime exports directly through Tide — no interop assemblies and no generator at
all. The dev-time projection (`nami interop generate` → `GameInterop.g.cs`) is shipped;
lazy on-demand in-process materialization remains design intent, not shipped behavior.

## What Nami does NOT have yet (honest)

| Capability | BepInEx | Nami (current) |
|---|---|---|
| Unity Mono modding | mature | **works end-to-end** (load, patch, typed game access) |
| Unity IL2CPP modding | mature | **works end-to-end via Tide's IL2CPP backend** (same typed API; verified live on Unity 6000.0.61) — dev-time typed projections via `nami interop generate` are shipped too |
| Harmony-style method patching | yes (HarmonyX) | **Wave** — in-house detours + IL-copy patching: M1 gate/observer for parameterless void targets; M2 prefix/postfix for any closed method with a real body, skip, `__instance`/`__result`/`__state`/`__args` |
| Calling game code from mods | yes (in-process) | **Tide** — typed fields/properties, calls, objects (parameterless `NewObject()`), a generic `Get<T>`/`Set<T>`/`Call<T>` API, enums (underlying int; `long`-backed as `I64`), arrays, live scene objects via statics — Mono and IL2CPP (Mono bridge behind `enableMonoBridge`, which gates the boot self-test) |
| Ecosystem / existing mods | huge | zero (clean-slate API) |
| Years of edge-case hardening | yes | no — expect bugs |
| Packaging / templates / installer | mature | **in** — NuGet packages (`Nami.Sdk`/`Nami.Tide`), `dotnet new nami-mod`, `nami install`/`run`, `nami launch`/`create`, `nami interop`; self-contained downloadable installer next |

## The bet

BepInEx optimizes for **compatibility with an existing ecosystem** — which is exactly why it
carries net35, Mono-internal plugins, HarmonyX, Cecil, and a proxy DLL. Nami optimizes for
**isolation, a modern runtime, and a clean load path**, which means new mods, new tooling,
and a different ceiling — at the cost of not loading the existing BepInEx catalog.

Whether that trade is worth it is the milestone-by-milestone question; the architecture is
built so each milestone (IL2CPP, patching, hot reload, projection) lands on the same core —
IL2CPP and hot reload have already landed.
