# Nami Plan (summary)

Full plan: see the saved plan at ~/.commandcode/plans/nami-unity-mod-loader.md (or via /plans).

**M0 (this state):** scaffold, managed core (SDK/Core/CLI), tests, native skeleton.
**M1:** core framework hardening: sample .nmod packages, per-mod ALC verification on real Unity Mono fixture, quarantine, TOML/JSON config, log file.
**M2:** patch engine (prefix/postfix/transpiler) + Mono bridge: lazy projection of Unity types on Mono fixture.
**M3:** IL2CPP bridge: native global-metadata parsing (golden tests across Unity versions), lazy projection end-to-end, offline `nami interop dump`.
**M4:** hot reload (ALC unload), per-mod profiler, templates, docs, profiles.
**M5:** perf hardening + comparative bench gates vs BepInEx 6 / MelonLoader; crash-report story.
