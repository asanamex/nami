# Nami Modding Guide

Writing mods, configuring them, and using the developer side of the CLI. For installing
Nami into a game and running it, see [TUTORIAL.md](../TUTORIAL.md).

## 1. Write a mod

Use the `nami-mod` template (installed from the repo) to scaffold a class library that
references the `Nami.Sdk` and `Nami.Tide` NuGet packages:

```bat
dotnet new install tools\templates\nami-mod
dotnet new nami-mod -n MyFirstMod
```

The generated project looks like this (a `net10.0` class library referencing the packages -
it also adds `LangVersion latest` and sets `RootNamespace`/`AssemblyName` from the project
name):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>MyFirstMod</RootNamespace>
    <AssemblyName>MyFirstMod</AssemblyName>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Nami.Sdk" Version="1.0.0" />
    <PackageReference Include="Nami.Tide" Version="1.0.0" />
  </ItemGroup>
</Project>
```

And `Plugin.cs`:

```csharp
using Nami.Sdk;

namespace MyFirstMod;

[NamiPlugin]
[PluginInfo("com.example.mymod", "My Nami Mod", "0.1.0", Description = "A Nami mod.")]
public sealed class MyMod : NamiPlugin
{
    private int _ticks;

    public override void OnLoad()
    {
        Context.Log.Info("My Nami Mod loaded on .NET " + Environment.Version);
    }

    public override void OnUpdate()
    {
        // Log every ~2 seconds (120 ticks x 16 ms).
        if (++_ticks % 120 == 0)
        {
            Context.Log.Info("My Nami Mod tick " + _ticks);
        }
    }
}
```

`[PluginDependency("other.mod.id")]` declares a dependency (optionally
`MinimumVersion = "1.2.0"`, enforced at load - SemVer numeric compare, exact match
otherwise); `[PluginIncompatibility(...)]`
declares a conflict. Dependencies load first; conflicts are resolved at load time.

The template also emits `TideExample.cs` (a commented example of calling into the game).
Pass `--UseTide false` when scaffolding to leave that file out (the `Nami.Tide` package reference
stays).

If a mod misbehaves (throws repeatedly in `OnUpdate`), Nami **quarantines** it - disables it,
calls `OnUnload`, logs the reason, and the game keeps running.

### Calling into the game (Tide)

A mod that only logs to Nami can't touch the game. **Tide** is the bridge that lets your mod
call the game's own runtime (Mono or IL2CPP, auto-detected) - every call executes on the game's main thread.
See **[tide.md](tide.md)** for the full story (including why it has to work this
way). To use it:

1. Reference the `Nami.Tide` NuGet package (the template already does).
2. Enable the bridge in `<game>/nami/nami.json`: `{ "enableMonoBridge": true }`
3. Call it from your mod:

```csharp
using Nami;
using Nami.Sdk;

[NamiPlugin]
[PluginInfo("com.example.mymod", "My Mod", "1.0.0")]
public sealed class MyMod : NamiPlugin
{
    public override void OnLoad()
    {
        if (!Tide.IsAvailable) return;

        Tide.UnityLog("MyMod loaded - this shows up in Unity's own log!");

        // Read/write a static field or property on a game class:
        var myClass = GameClass.Resolve("Assembly-CSharp", "MyGame", "PlayerStats");
        int hp = myClass.GetStaticInt("MaxHealth");
        myClass.SetStaticInt("MaxHealth", hp * 2);

        // Create an object and call instance methods:
        using var go = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "GameObject")
                                   .NewObject();
        Tide.UnityLog($"made GameObject #{go.CallIntMethod("GetInstanceID")}");
    }
}
```

All calls block until the game main thread has run them; failures throw `TideException`
(details in `nami/native/nami-tide.log`).

### Hot reload: iterate without restarting the game

With `hotReload.enabled` (default on) you never relaunch to update a mod:

- **Rebuild in place** - `dotnet build` your mod straight into `nami/mods` (or copy the new
  DLL over the old one). Nami notices the file changed and swaps the mod to a new generation:
  `OnUnload` runs, the old `AssemblyLoadContext` is released, and the fresh assembly loads
  with state reset.
- **Drop a new DLL** into `nami/mods` - it is discovered and loaded automatically.
- **Delete a DLL** - the mod is unloaded. (Dependents of a deleted mod are not force-unloaded;
  they reload as a set the next time any of them changes.)
- Dependants reload too: if mod A changes, everything depending on A (transitively) reloads
  in dependency order. A mod can trigger its own reload with `Context.RequestReload()`.
- Rapid successive writes (a build) are collapsed by a debounce (`hotReload.debounceMs`,
  default 500), so a rebuild reloads once.

The `nami.log` shows each step: `Hot reload: <ids>`, `Unloaded '...' (gen N)`, then
`Reloaded '...' gen N -> gen M`.

### Per-mod profiler

`Context.Profiler` (an `IModMetrics`) gives your mod its own live performance snapshot -
useful for a debug overlay or adaptive quality:

```csharp
public override void OnUpdate()
{
    var m = Context.Profiler;
    if (m.TickCount > 0 && m.P95Ms > 8.0)
    {
        Context.Log.Warn($"slow frame budget: p95={m.P95Ms:F2}ms max={m.MaxMs:F2}ms");
    }
}
```

`TickCount`, `LastMs`, `AvgMs`, `P95Ms`, `MaxMs` come from the chainloader's per-tick timing
(histogram-based, allocation-free on the hot path); `ToSummaryLine()` renders it all. The
same numbers are logged periodically under the `profiler` source (see the `profiler` config
section below). Tide-op latency (`TideOpCount`/`TideOpAvgMs`) is recorded for
the typed `Call`/`CallInstance` ops while a mod's `OnUpdate` runs (`UnityLog`/`InvokeStatic` excluded).

## 2. Configuration (`nami.json`)

Optional file in the nami root. `nami install` creates one (with defaults); if it is absent
(or malformed JSON), Nami boots with defaults (I/O errors still throw). Keys are written
camelCase and read case-insensitively.

```json
{
  "quarantineEnabled": true,
  "quarantineThreshold": 5,
  "logLevel": "Info",
  "enabledPlugins": ["com.example.mymod"],
  "enableMonoBridge": false,
  "steamAppId": "2386580",
  "profiler": { "enabled": true, "summaryIntervalSeconds": 30.0 },
  "hotReload": { "enabled": true, "autoWatch": true, "debounceMs": 500 },
  "pluginConfig": { "com.example.mymod": { "greeting": "hi" } }
}
```

- `quarantineEnabled`: master switch for crash quarantine (default on); quarantine trips
  at `quarantineThreshold` consecutive `OnUpdate` throws.
- `enabledPlugins`: only these load (empty = all). Entries are `*`/`?` globs,
  case-insensitive (`com.example.*`); exact ids work as before.
- `logLevel`: minimum level for the console/file sinks (`Trace`/`Debug`/`Info`/`Warn`/
  `Error`/`Fatal`, case-insensitive, default `Info`); unknown values fall back to `Info`.
- `enableMonoBridge`: enables **Tide** - the bridge that lets mods call into
  the game (every call runs safely on the game's main thread). The flag gates the boot
  self-test (`[boot] attaching Tide bridge...`) on both backends; mod-issued Tide calls
  route to the auto-detected backend (Mono, or IL2CPP via `GameAssembly.dll`) whenever
  the loader is present. Off by default because the Mono path patches a live game export
  and is verified on Unity Mono across four titles so far (2022.3 and Unity 6); see
  [tide.md](tide.md). The IL2CPP backend patches nothing (window-proc executor).
- `profiler`: the built-in per-mod profiler. When `enabled` (default), each `OnUpdate` is
  timed and a summary line (`ticks/last/avg/p95/max` per mod, generation included) is logged
  under the `profiler` source every `summaryIntervalSeconds`. Mods read their own metrics
  in-process via `Context.Profiler` (`IModMetrics`).
- `hotReload`: live mod reload. When `enabled` (default), the chainloader supports generation
  reloads; when `autoWatch` (default) it also watches top-level `nami/mods/*.dll` and
  automatically reloads a mod whose DLL is rebuilt/replaced (or loads one newly dropped)
  after a `debounceMs` quiet window. DLLs in subdirectories (e.g. `.nmod`-installed
  `mods/<id>/`) are discovered but not watched - touch them via rebuild of a top-level
  DLL, `Context.RequestReload()`, or restart. Mod files are never locked - you can
  rebuild in place while the game runs. Disable both to pin a session to its mods until restart.
- `pluginConfig`: per-plugin sections (`pluginConfig.<id>`) read via `Context.Config`
  (`GetString/GetInt/GetDouble/GetBool`, all with fallbacks); missing keys/sections yield
  defaults, never throw.
- `gameExe` / `steamAppId`: written by `nami launch set` (which game to run; the Steam
  app id steam mode needs). Steam mode resolves the id from `steamAppId` first, then an
  existing `steam_appid.txt` next to the game, and refuses without one.
- Legacy lane (`nami inex`) has **no** `nami.json` flag - the switch is the
  `nami/inex/enabled` file (`nami inex enable` writes it empty, `disable` deletes it,
  payload kept). Payload staged but sentinel missing = pure Nami boot.

## 3. CLI reference

```
nami version                        print version
nami install [gameDir] [--from <zip|url>]
                                    install a Nami root (from build outputs, or from a
                                    self-contained installer artifact via --from)
nami pack [out.zip] [--artifacts <root>]  build the self-contained installer artifact (managed +
                                    native + bundled .NET runtime, hash-verified manifest)
nami launch set <game.exe> [--steam-id <appid>] [--force] [gameDir]
                                    remember which executable is the game
nami launch [offline|steam] [gameDir]
                                    run the game with Nami injected (offline, default; steam runs through Steam, needs --steam-id)
nami create [offline|steam] [gameDir]
                                    write launchNami.exe + run-with-nami.bat into the nami root
nami run <mod.csproj> [gameDir]     build a mod, stage it into nami/mods, launch the game
nami doctor [gameDir]               basic sanity check of a Nami install
nami list   [gameDir]               list installed mods
nami interop images|dump|generate|header [args...] [gameDir]
                                    offline IL2CPP typed-projection tooling (dev-time)
nami inex install|enable|disable|status [args...] [gameDir]
                                    legacy BepInEx lane (boots BepInEx 5.x in game Mono)
nami nmod info|install [args...] [gameDir]
                                    .nmod package distribution (manifest info / install)
nami help                           show help
```

`nami launch` runs the game through Nami the same way `nami_boot.exe` does. When no game
executable has been set, it auto-detects the largest `.exe` directly in the game folder
(skipping known helpers like crash handlers/updaters; falls back to immediate subfolders
excluding `_Data/` and `nami/`). `nami launch steam` ensures the Steam client is running,
syncs the app id to `steam_appid.txt` next to the game (set it with
`nami launch set --steam-id`; without one it refuses), then launches with Nami injected —
the game runs with full Steam context (in-game status, overlay). Nothing happens after
the game exits. `nami create` writes `launchNami.exe` (self-contained) + `run-with-nami.bat` into the nami
root, so the game can be started with Nami by double-clicking, without the CLI open.
`nami run` is the modder's loop: build the mod, copy it into `nami/mods`, and launch.

Stage and pack refuse stale build outputs (a Debug tree newer than Release, or native
binaries older than their C++ sources): rebuild Release outputs or pass `--artifacts <root>`.
Every stage also deletes retired root-level `nami_loader.dll` copies (shadow-load risk).

### BepInEx mods through nami-inex

Nami boots real BepInEx 5.x mods inside the game's own Mono - no emulation, so
unmodified legacy mods (including Harmony patchers) run as-is, managed by `nami`
instead of Doorstop's proxy. Mono titles only; the loader skips the lane on IL2CPP
(BepInEx 6 needs its own CoreCLR lane):

```bat
:: 1. copy a working BepInEx 5.x tree (core [+ plugins/patchers/config], never cache/)
::    source must contain core/BepInEx.Preloader.dll or install refuses it
nami inex install "C:\path\to\WorkingGame\BepInEx" "%GAME%"
:: 2. drop BepInEx mod DLLs into <game>\nami\inex\BepInEx\plugins
:: 3. disable Doorstop in the game folder (doorstop_config.ini: enabled=false)
nami inex enable "%GAME%"
nami launch "%GAME%"
```

`install` validates the preloader and stays disabled until `enable`; `enable` requires
a staged payload; `disable` deletes only the sentinel (payload kept, next boot is
pure Nami). Under the hood Nami sets the four `DOORSTOP_*` variables
(`PROCESS_PATH`, `MANAGED_FOLDER_DIR` derived as `<exe>_Data\Managed`,
`INVOKE_DLL_PATH` pointing at `nami/inex/BepInEx/core/BepInEx.Preloader.dll`,
`DLL_SEARCH_DIRS` pointing at `nami/inex/BepInEx/core`) and invokes
`Doorstop.Entrypoint.Start` - `doorstop_config.ini` is never read.

Rules that differ from Nami mods: a legacy crash is a game crash (no Nami quarantine
in game Mono - `nami inex disable` returns to a pure Nami boot). Boot timing is dual
path: a `mono_jit_init` detour attempts Doorstop timing (runs before first managed
execution); if it misses or the prologue refuses the detour, a background watcher
fires the drain fallback and a chainloader kick (`Initialize`+`Start`, both guarded)
once a window is visible and the script domain is stable - so legacy plugins appear
late (scene live), not at process start. `nami inex status` shows payload+sentinel
state, a hint when staged-but-disabled, the last 3 lines of `native/nami-inex.log`,
and `LogOutput.log` presence/size. IL2CPP/BepInEx-6 titles are not covered yet
(BepInEx 6 needs its own CoreCLR lane).
`nami doctor` prints root, mods dir, quarantine display, `*.dll` count, the configured or
auto-detected exe, any missing launcher files, and the inex payload/sentinel state -
a smoke check, not a full environment report.
It also reports the obsolete-layout line (`layout : current`, or `layout : OBSOLETE ...`
when a retired root-level `nami_loader.dll` is present) and the boot-guard line
(`bootguard: normal`, or `SAFE MODE` with the auto-clear rule after a boot crash).

## 4. The sample mod

`samples/HelloNami/` is a ready-made mod - run it with the same `nami run` flow as any mod
project:

```bat
nami run "samples\HelloNami\HelloNami.csproj" "%GAME%"
```

It logs once on load (`HelloNami loaded inside the Nami CoreCLR runtime!`) and then every
~120 update ticks. (`samples/TideProbe/` and `samples/TideProbeIl2Cpp/` are the Tide
bridge probes for Mono and IL2CPP; `samples/TideProbeIl2CppPatch/` is the IL2CPP
patching demo - it hooks real game methods in phases and self-reports PASS/FAIL.)

## 5. Running the test suite

```bat
dotnet test Nami.slnx              :: runs all four test projects
```

(Or individually: `dotnet test tests/Nami.Tests`, `tests/Nami.Wave.Tests`,
`tests/Nami.Tide.Tests`.) Current markers by project (`[Fact]` + `[Theory]`):
(38 + 105 + 58 + 46; theories expand those to 38 + 109 + 60 + 51 runnable cases),
and `dotnet test` exit code stays the source of truth.

## 6. Known limitations

- **Tide scope**: typed access covers static and instance fields/properties (primitives,
  strings, live objects, enums as their underlying int - `long`-backed enums surface as
  `I64`), a generic `Get<T>`/`Set<T>`/`Call<T>` API, arrays (`TideArrays`), object creation
  (parameterless ctor), and live scene objects via static accessors (`Camera.main`) or
  `GameClass.FindObject()` (first loaded object of a class through the window-proc executor
  as plural `FindObjectsOfType` + element 0 - active objects only, null on miss, needs a
  visible game window). On
  **IL2CPP** titles the same typed API runs through the IL2CPP backend (auto-detected; see
  [tide.md §9](tide.md)). Dev-time typed projections come from
  `nami interop generate` (see [tide.md §9](tide.md) and `nami interop --help`).
  Still missing: invoking Unity's singular `Object.FindObjectOfType` wrapper directly
  (Unity aborts it from foreign re-entry - use `FindObject()` instead).
- **Wave scope**: `Wave.Patch` routes prefix-only hooks on small primitive signatures
  to a native fast stub, everything else (postfix, transpilers, instance methods,
  references, `__result`/`__state`/`__args`) to IL-copy patching - prefix/postfix, skip,
  result rewriting (`__instance` by value, struct receivers observed as a copy;
  `__result`/`__state` by ref; open generic definitions via
  `Wave.Patch(definition, typeArguments, ...)`; `ref` hook params and exotic `calli`
  shapes refused). Windows x64 only. `Wave.Hook` is the legacy entry point
  (parameterless void methods, gate/observer).
- **Typed IL2CPP hooks**: `WaveIl2Cpp.HookTyped` needs the exact user-parameter `TideType`
  shape (an empty array selects a zero-parameter method); the native resolver refuses to
  guess hidden ABI slots. Callbacks read and write args and results through generic
  `GetArgument<T>` / `SetArgument<T>` / `GetResult<T>` / `SetResult<T>`, see instance
  receivers as a borrowed `This` (do not dispose), and may box primitives into `Object`
  slots.
- The game must be launched through the Nami injector; use `nami launch` or the
  `launchNami.exe` shortcut `nami create` writes (Steam launch options can point at that).
- Do not drop Doorstop's proxy (`winhttp.dll`) or a loose `BepInEx/` tree in the game
  folder; legacy BepInEx 5.x payloads belong under `nami/inex/` via `nami inex`
  (with `doorstop_config.ini: enabled=false` if that file is present). `nami inex
  disable` returns to a pure Nami boot with the payload kept.
