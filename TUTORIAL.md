# Nami Tutorial

This guide walks you from a clean checkout to a mod running inside a real Unity game —
including calling into the game itself through Tide's typed API.

> **Current scope:** Windows x64, Unity **Mono** games (IL2CPP is a later milestone).
> BepInEx is **not** required and must **not** be installed (its doorstop proxy conflicts
> with Nami's launcher).

---

## 1. What you need

- Windows 10/11 x64
- A Unity **Mono** game (check: the game folder has a `*_Data/Managed/` directory and no
  `GameAssembly.dll`)
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
| `Nami.Core.dll`, `Nami.Sdk.dll` | same output dir | loader core + plugin API |

## 3. Stage a Nami root next to the game

Nami keeps *everything* in one folder inside the game directory — nothing is written into the
game's own folders. Create `<game>/nami/` and copy:

```bat
set GAME=C:\path\to\YourGame
set NAMI=C:\path\to\nami\src\Nami.Runtime\bin\Release\net10.0

mkdir "%GAME%\nami\native"
mkdir "%GAME%\nami\mods"

copy native\build\nami_loader.dll  "%GAME%\nami\native\"
copy "%NAMI%\Nami.Runtime.dll"       "%GAME%\nami\"
copy "%NAMI%\Nami.Runtime.deps.json" "%GAME%\nami\"
copy "%NAMI%\Nami.Runtime.runtimeconfig.json" "%GAME%\nami\"
copy "%NAMI%\Nami.Core.dll" "%GAME%\nami\"
copy "%NAMI%\Nami.Sdk.dll"  "%GAME%\nami\"
```

Nami hosts its own .NET runtime, so copy a runtime next to it:

```bat
:: from a .NET 10 install:
xcopy /e /i "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.x" "%GAME%\nami\dotnet\shared\Microsoft.NETCore.App\10.0.x\"
xcopy /e /i "C:\Program Files\dotnet\host\fxr\10.0.x"             "%GAME%\nami\dotnet\host\fxr\10.0.x\"
```

(Use the same `10.0.x` patch version in both paths; Nami's runtimeconfig rolls forward.)

Resulting layout:

```
<game>/
└── nami/
    ├── Nami.Runtime.dll / .deps.json / .runtimeconfig.json
    ├── Nami.Core.dll
    ├── Nami.Sdk.dll
    ├── native/nami_loader.dll
    ├── mods/            ← drop your mod DLLs here
    └── nami.json        ← optional config (created on first run)
```

## 4. Write a mod

Create a class library targeting `net10.0` that references `Nami.Sdk.dll`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Nami.Sdk">
      <HintPath>path\to\nami\src\Nami.Sdk\bin\Release\net10.0\Nami.Sdk.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

```csharp
using Nami.Sdk;

namespace MyMod;

[NamiPlugin]
[PluginInfo("com.example.mymod", "My Mod", "1.0.0", Description = "My first Nami mod.")]
public sealed class MyMod : NamiPlugin
{
    public override void OnLoad()
    {
        Context.Log.Info("MyMod loaded on .NET " + Environment.Version);
    }

    public override void OnUpdate()
    {
        // called ~60x/second while the mod is active
    }
}
```

`[PluginDependency("other.mod.id")]` declares a dependency; `[PluginIncompatibility(...)]`
declares a conflict. Dependencies load first; conflicts are resolved at load time.

### Calling into the game (Tide)

A mod that only logs to Nami can't touch the game. **Tide** is the bridge that lets your mod
call the game's own Mono runtime — every call executes safely on the game's main thread.
See **[docs/tide.md](docs/tide.md)** for the full story (including why it has to work this
way). To use it:

1. Add `Nami.Tide.dll` to your mod's references (next to `Nami.Sdk.dll`) and copy it into the
   game's `nami/` folder next to `Nami.Sdk.dll`.
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
:: 1. copy your built mod DLL into the mods folder
copy MyMod\bin\Release\net10.0\MyMod.dll "%GAME%\nami\mods\"

:: 2. launch the game through Nami (injects and hosts .NET 10 inside the game)
native\build\nami_boot.exe "%GAME%\YourGame.exe" "%GAME%\nami\native\nami_loader.dll"
```

The game opens normally. Check that Nami booted:

```bat
type "%GAME%\nami\nami.log"
```

Expected output:

```
[boot]        Nami managed runtime booting (nami_root=...\nami)
[boot]        clr=10.0.10 os=Microsoft Windows ... arch=X64
[chainloader] Loaded com.example.mymod 1.0.0 (MyMod.dll)
[com.example.mymod] MyMod loaded on .NET 10.0.10
[boot]        chainloader activated: 1 plugin(s) loaded
```

If a mod misbehaves (throws repeatedly in `OnUpdate`), Nami **quarantines** it — disables it,
calls `OnUnload`, logs the reason, and the game keeps running.

## 6. Configuration (`nami.json`)

Optional file in the nami root. Created with defaults on first boot if absent.

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
- `enableMonoBridge`: enables **Tide** — the bridge that lets mods call into the game's Mono
  runtime (every call runs safely on the game's main thread). Off by default because it
  patches a live game export and is verified on Unity 2022.3 Mono so far; see
  [docs/tide.md](docs/tide.md).

## 7. The sample mod

`samples/HelloNami/` is a ready-made mod:

```bat
dotnet build samples/HelloNami -c Release
copy samples\HelloNami\bin\Release\net10.0\HelloNami.dll "%GAME%\nami\mods\"
```

It logs once on load and every ~120 update ticks.

## 8. CLI

```
nami version              print version
nami doctor [gameDir]     check a Nami install
nami list   [gameDir]     list installed mods
```

## 9. Running the test suite

```bat
dotnet test tests/Nami.Tests
```

## 10. Known limitations

- **Mono games only** — IL2CPP support is a later milestone.
- **Tide scope**: mods can `UnityLog` and call parameterless static game methods. Reading/
  writing game fields and calling methods with arguments (or on live objects) is the next
  Tide milestone.
- **Wave scope**: patching supports parameterless void methods so far.
- The game must be launched via `nami_boot.exe`; Steam launch options / shortcuts can point at
  a wrapper script that calls it.
- Do not run alongside BepInEx/Doorstop in the same game folder.
