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
  instance field access, typed method calls (with primitive/string args), live object
  creation/calls, all on the game's main thread. Verified in-game against four Unity Mono
  titles spanning 2022.3 and Unity 6: Project Hardline (2022.3.27f1), Parasocial (2022.3.5f1),
  ROUNDS (2022.3.34f1), and The Gaspy Color War (6000.5.4f1).
- **M2 remaining:** internal-call property edge (e.g. `Time.timeScale` getters crash from the
  nested drain); scene-object discovery ergonomics; enum/array values; Mono exception-message
  surfacing.

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
- **M5 — depth:** scene-object discovery and broader Tide values; hot reload; per-mod
  profiler; comparative bench gates vs BepInEx/MelonLoader.

## Verified in-game evidence

**Project Hardline (Unity 2022.3.27f1 Mono):**

```
Tide bridge OK: Unity Debug.Log executed on the game main thread
typed Debug.Log(string) call OK
typed Debug.Log(int) call OK (primitive arg marshaled)
created GameObject instance (handle=7688)
GameObject.GetInstanceID() = 0
game alive and stable (~379 MB), mod update loop ticking
```

**Parasocial (Unity 2022.3.5f1 Mono):**

```
Tide bridge OK: Unity Debug.Log executed on the game main thread
typed Debug.Log(string) call OK
typed Debug.Log(int) call OK (primitive arg marshaled)
created GameObject instance (handle=7640)
GameObject.GetInstanceID() = 0
TideProbe verification complete
game alive and stable (~1.2 GB), mod update loop ticking
```

**ROUNDS (Unity 2022.3.34f1 Mono):**

```
Tide bridge OK: Unity Debug.Log executed on the game main thread
typed Debug.Log(string) call OK
typed Debug.Log(int) call OK (primitive arg marshaled)
created GameObject instance (handle=7688)
GameObject.GetInstanceID() = 0
TideProbe verification complete
game alive and stable (~331 MB), mod update loop ticking
```

**The Gaspy Color War (Unity 6 / 6000.5.4f1 Mono):**

```
Tide bridge OK: Unity Debug.Log executed on the game main thread
typed Debug.Log(string) call OK
typed Debug.Log(int) call OK (primitive arg marshaled)
created GameObject instance (handle=2088006189104)
GameObject.GetInstanceID() = 0
TideProbe verification complete
game alive and stable (~650 MB), mod update loop ticking
```

Unity 6 exposed (and fixed) a GCHandle ABI issue: its Mono stores handles as 64-bit encoded
pointers that can live above 4 GB, which the legacy `mono_gchandle_*` entry points truncate.
Tide now uses the full-64-bit `mono_gchandle_*_v2` variants (see `docs/tide.md` §6).
