# Nami Tutorial

This guide walks you from a clean checkout to a mod running inside a real Unity game —
including calling into the game itself through Tide's typed API.

> **Current scope:** Windows x64, Unity **Mono** and **IL2CPP** games (see
> [docs/tide.md §9](docs/tide.md) for the IL2CPP backend). BepInEx is **not** required and
> must **not** be installed (its doorstop proxy conflicts with Nami's launcher).

---

## 1. What you need

- Windows 10/11 x64
- A Unity game — **Mono** (game folder has a `*_Data/Managed/` directory and no
  `GameAssembly.dll`) or **IL2CPP** (game folder has `GameAssembly.dll` + a
  `*_Data/il2cpp_data/` directory; most Unity 6 titles and many 2019–2022 titles)
- [.NET SDK 10.0+](https://dotnet.microsoft.com/download)
- CMake 3.20+ and a C++17 compiler (MinGW or MSVC) — only to build the native injector
- (Optional) a second copy of the game for testing, so the original stays pristine

## 2. Build Nami

```bat
:: managed projects (SDK / Core / Runtime / CLI / tests)
dotnet build Nami.slnx

:: native injector + loader
cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build
```

Artifacts you need:

| File | Where | Purpose |
|---|---|---|
| `nami_boot.exe` | `native/build/` | launches the game and injects Nami |
| `nami_loader.dll` | `native/build/` | injected into the game; hosts .NET |
| `Nami.Runtime.dll` (+ `.deps.json`, `.runtimeconfig.json`) | `src/Nami.Runtime/bin/Release/net10.0/` | managed in-game bootstrap |
| `Nami.Core.dll`, `Nami.Sdk.dll`, `Nami.Tide.dll` | same output dir | loader core + plugin API + game bridge |

## 3. Stage a Nami root next to the game

Nami keeps *everything* in one folder inside the game directory — nothing is written into the
game's own folders. The CLI stages it from the repo's build outputs (managed runtime + native
injector + a bundled .NET runtime copied from your local install):

```bat
:: from the repo root, after the build in step 2:
nami install "C:\path\to\YourGame"
```

Resulting layout:

```
<game>/
└── nami/
    ├── Nami.Runtime.dll / .deps.json / .runtimeconfig.json
    ├── Nami.Core.dll
    ├── Nami.Sdk.dll
    ├── Nami.Tide.dll
    ├── dotnet/host/fxr/<ver> + dotnet/shared/Microsoft.NETCore.App/<ver>   (bundled runtime)
    ├── native/nami_boot.exe + nami_loader.dll
    ├── mods/            ← your mod DLLs go here
    └── nami.json        ← config (created by nami install; defaults when absent)
```

## 4. Write a mod

Use the `nami-mod` template (installed from the repo) to scaffold a class library that
references the `Nami.Sdk` and `Nami.Tide` NuGet packages:

```bat
dotnet new install tools\templates\nami-mod
dotnet new nami-mod -n MyFirstMod
```

The generated project looks like this (a `net10.0` class library referencing the packages —
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
    <PackageReference Include="Nami.Sdk" Version="0.1.0" />
    <PackageReference Include="Nami.Tide" Version="0.1.0" />
  </ItemGroup>
</Project>
```

And `Plugin.cs`:

```csharp
using Nami.Sdk;

namespace MyFirstMod;

[NamiPlugin]
[PluginInfo("com.example.mymod", "My First Mod", "0.1.0", Description = "A Nami mod.")]
public sealed class MyMod : NamiPlugin
{
    private int _ticks;

    public override void OnLoad()
    {
        Context.Log.Info("My First Mod loaded on .NET " + Environment.Version);
    }

    public override void OnUpdate()
    {
        // Log every ~2 seconds (120 ticks x 16 ms).
        if (++_ticks % 120 == 0)
        {
            Context.Log.Info("My First Mod tick " + _ticks);
        }
    }
}
```

`[PluginDependency("other.mod.id")]` declares a dependency; `[PluginIncompatibility(...)]`
declares a conflict. Dependencies load first; conflicts are resolved at load time.

The template also emits `TideExample.cs` (a commented example of calling into the game).
Pass `-U false` when scaffolding to leave that file out (the `Nami.Tide` package reference
stays).

### Calling into the game (Tide)

A mod that only logs to Nami can't touch the game. **Tide** is the bridge that lets your mod
call the game's own Mono runtime — every call executes safely on the game's main thread.
See **[docs/tide.md](docs/tide.md)** for the full story (including why it has to work this
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

        Tide.UnityLog("MyMod loaded — this shows up in Unity's own log!");

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
(details in `nami-tide.log`).

## 5. Install and run

```bat
:: 1. build the mod, drop it into nami\mods, and launch the game through Nami
nami run "MyFirstMod\MyFirstMod.csproj" "%GAME%"
```

(The game opens normally, Nami is injected, and your mod is loaded. `nami run` is shorthand
for: `dotnet build -c Release`, copy the produced DLL into `<game>\nami\mods`, then launch
via `nami_boot.exe`.) Check that Nami booted:

```bat
type "%GAME%\nami\nami.log"
```

Expected output (timestamps and levels as written by the file sink):

```
[17:32:25.118 INFO] [boot]        Nami managed runtime booting (nami_root=...\nami)
[17:32:25.126 INFO] [boot]        clr=10.0.10 os=Microsoft Windows ... arch=X64
[17:32:25.544 INFO] [chainloader] Loaded com.example.mymod 1.0.0 (MyMod.dll)
[17:32:25.544 INFO] [com.example.mymod] MyMod loaded on .NET 10.0.10
[17:32:25.566 INFO] [boot]        chainloader activated: 1 plugin(s) loaded
```

(With `"enableMonoBridge": true`, the log also shows `[boot] attaching Tide bridge...` then
`[boot] Tide bridge OK: Unity Debug.Log executed on the game main thread` before the
chainloader lines.)

If a mod misbehaves (throws repeatedly in `OnUpdate`), Nami **quarantines** it — disables it,
calls `OnUnload`, logs the reason, and the game keeps running.

## 6. Configuration (`nami.json`)

Optional file in the nami root. `nami install` creates one (with defaults); if it is absent
(or unreadable), Nami boots with defaults. Keys are written camelCase and read
case-insensitively.

```json
{
  "quarantineEnabled": true,
  "quarantineThreshold": 5,
  "logLevel": "Info",
  "enabledPlugins": ["com.example.mymod"],
  "enableMonoBridge": false
}
```

- `enabledPlugins`: only these load (empty = all).
- `logLevel`: accepted and stored, but not yet wired to the runtime's minimum level (the
  loader logs at Info); planned.
- `enableMonoBridge`: enables **Tide** on Mono titles — the bridge that lets mods call into
  the game's Mono runtime (every call runs safely on the game's main thread). Off by default
  because it patches a live game export and is verified on Unity Mono across four titles so far
  (2022.3 and Unity 6); see [docs/tide.md](docs/tide.md). On **IL2CPP** titles no flag is
  needed: Tide auto-detects `GameAssembly.dll` and uses the window-proc executor (no code
  patching) — see [docs/tide.md §9](docs/tide.md).

## 7. The sample mod

`samples/HelloNami/` is a ready-made mod — run it with the same `nami run` flow as any mod
project:

```bat
nami run "samples\HelloNami\HelloNami.csproj" "%GAME%"
```

It logs once on load (`HelloNami loaded inside the Nami CoreCLR runtime!`) and then every
~120 update ticks.

## 8. CLI

```
nami version                        print version
nami install [gameDir]              stage a Nami root next to a game (from build outputs)
nami launch set <game.exe> [--steam-id <appid>] [--force] [gameDir]
                                    remember which executable is the game
nami launch [offline|steam] [gameDir]
                                    run the game with Nami injected (offline, default)
nami create [offline|steam] [gameDir]
                                    write launchNami.exe + run-with-nami.bat into the nami root
nami run <mod.csproj> [gameDir]     build a mod, stage it into nami/mods, launch the game
nami doctor [gameDir]               check a Nami install
nami list   [gameDir]               list installed mods
```

`nami launch` runs the game through Nami the same way `nami_boot.exe` does. When no game
executable has been set, it auto-detects the largest `.exe` in the game folder (skipping
known crash handlers/updaters). `nami launch steam` runs the game with Nami injected and,
after the game exits, starts a clean unmodded session via `steam://rungameid/<appid>` (set
the app id with `nami launch set --steam-id`; without one it falls back to offline).
`nami create` writes `launchNami.exe` (self-contained) + `run-with-nami.bat` into the nami
root, so the game can be started with Nami by double-clicking, without the CLI open.
`nami run` is the modder's loop: build the mod, copy it into `nami/mods`, and launch.

## 9. Running the test suite

```bat
dotnet test Nami.slnx              :: runs all four test projects
```

(Or individually: `dotnet test tests/Nami.Tests`, `tests/Nami.Wave.Tests`,
`tests/Nami.Cli.Tests`, `tests/Nami.Tide.Tests`.)

## 10. Known limitations

- **Tide scope**: typed access covers static and instance fields/properties (primitives,
  strings, live objects, enums as their underlying int), a generic `Get<T>`/`Set<T>`/`Call<T>`
  API, arrays (`TideArrays`), object creation, and live scene objects via static accessors
  (`Camera.main`). On **IL2CPP** titles the same API runs through the IL2CPP backend
  (auto-detected; see [docs/tide.md §9](docs/tide.md)). Still missing on both backends:
  Unity's scene-iteration scan APIs (`Object.FindObjectOfType` — Unity aborts these from
  foreign re-entry) and a generated strongly-typed projection layer.
- **Wave scope**: M1 supports parameterless void methods; **M2** (IL-copy) patches any
  non-generic method with a real body — any signature, prefix/postfix, skip, result
  rewriting. Windows x64 only.
- The game must be launched through the Nami injector; use `nami launch` or the
  `launchNami.exe` shortcut `nami create` writes (Steam launch options can point at that).
- Do not run alongside BepInEx/Doorstop in the same game folder.
