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
  typed API** (`Get<T>`/`Set<T>`/`Call<T>`), **enum values** (as underlying int), **array
  values** (`TideArrays`), and **live scene-object access** via static accessors
  (`Camera.main`), all on the game's main thread. Verified in-game against four Unity Mono
  titles spanning 2022.3 and Unity 6: Project Hardline (2022.3.27f1), Parasocial (2022.3.5f1),
  ROUNDS (2022.3.34f1), and The Gaspy Color War (6000.5.4f1).
- **M2 remaining:** a Wave-installed per-frame script callback to unlock Unity's
  scene-iteration scan APIs (`FindObjectOfType` aborts from embedding re-entry); a generated
  strongly-typed projection layer over the generic API.

## Next

- **M3 — dev experience:** NuGet packaging of `Nami.Sdk`/`Nami.Tide`, `dotnet new nami-mod`
  template, `nami install <game>` / `nami run`, so a modder goes from idea to running mod
  without hand-staging.
  - **Shipped early (launcher slice):** the player-facing `nami launch` flow — `launch set
    <game.exe>`, `launch [offline|steam]` (Steam relay to a clean session after exit),
    `create` (double-click `launchNami.exe` + `run-with-nami.bat` in the nami root), and
    `doctor` reporting the configured game exe. Auto-detects the game as the largest `.exe`.
  - `nami install` (self-contained: bundles/downloads the .NET runtime + managed artifacts) is
    still the future "Nami-Install" product.
- **M4 — IL2CPP:** native `global-metadata.dat` parsing (golden corpus), lazy projection,
  offline `nami interop dump`; same main-thread-drain pattern for the runtime bridge.
- **M5 — depth:** hot reload; per-mod profiler; comparative bench gates vs BepInEx/MelonLoader;
  scene-iteration scan APIs via a Wave-installed per-frame script callback.

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
