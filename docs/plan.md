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
  instance field/property access, typed method calls, live object creation/calls, all on the
  game's main thread. Verified in-game against Project Hardline (Unity 2022.3.27f1 Mono).
- **M2 remaining:** internal-call property edge (e.g. `Time.timeScale` getters crash from the
  nested drain); scene-object discovery ergonomics; Mono exception-message surfacing.

## Next

- **M3 — dev experience:** NuGet packaging of `Nami.Sdk`/`Nami.Tide`, `dotnet new nami-mod`
  template, `nami install <game>` / `nami run`, so a modder goes from idea to running mod
  without hand-staging.
- **M4 — IL2CPP:** native `global-metadata.dat` parsing (golden corpus), lazy projection,
  offline `nami interop dump`; same main-thread-drain pattern for the runtime bridge.
- **M5 — depth:** Wave with typed args/returns; hot reload; per-mod profiler; comparative
  bench gates vs BepInEx/MelonLoader.

## Verified in-game evidence (Project Hardline, Unity 2022.3.27f1 Mono)

```
Tide bridge OK: Unity Debug.Log executed on the game main thread
typed Debug.Log(string) call OK
typed Debug.Log(int) call OK (primitive arg marshaled)
created GameObject instance (handle=7688)
GameObject.GetInstanceID() = 0
game alive and stable (~379 MB), mod update loop ticking
```
