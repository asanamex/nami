# Nami Tutorial: installing and running

This guide does one thing: get Nami running next to your game without breaking anything.
Writing your own mods lives in [docs/modding.md](docs/modding.md).

> **Current scope:** Windows 10/11 x64, Unity **Mono** and **IL2CPP** games.
> BepInEx is **not** required. Do **not** drop Doorstop's proxy (`winhttp.dll`, or
> `doorstop_config.ini` with `enabled=true`) into the game folder - it conflicts with
> Nami's launcher.

---

## 0. Will this break my game?

Short version: no, if you follow the steps. Longer version, so you can relax:

- **Nami touches exactly one new folder**: `<game>/nami/`. It never modifies, moves, or
  deletes your game files. Everything Nami needs (its own .NET runtime, its DLLs, your
  mods, its logs, its config) lives inside `nami/`.
- **Uninstalling is deleting that folder.** Delete `<game>/nami/` and the game is exactly
  as it was before. (Also delete any desktop shortcut you made.)
- **Back up anyway.** Copy the game folder somewhere safe before you start, or verify the
  game files through Steam/GOG after you are done experimenting. Backups are cheap;
  re-downloading a 50 GB game is not.
- **Play offline first.** Run your first Nami sessions offline. Nami injects code into the
  game process, and no one can promise an anti-cheat system will like that. Single-player
  offline play is the safe zone. The FAQ below says more.
- **If your antivirus complains** about `nami_boot.exe`, that is the heuristic signature of
  the injection technique (suspended launch plus remote thread), which is also how debuggers
  and legitimate tooling work. Either allow the file or stop here. Never disable your
  antivirus wholesale for a mod loader.

How to tell which runtime your game uses: open the game folder. A `*_Data/Managed/`
directory with no `GameAssembly.dll` means **Mono**. A `GameAssembly.dll` next to the exe
(plus `*_Data/il2cpp_data/`) means **IL2CPP**. Nami handles both; you just need to know
because a few log lines and options differ.

## 1. What you need

**Players (recommended path):**

- Windows 10/11 x64
- Your Unity game, ideally with a backup copy
- The `nami-1.0.0.zip` release file (from the
  [releases page](https://github.com/asanamex/nami/releases)) - about 38 MB
- Mod DLLs you want to run (from their authors), or none at all for a first smoke test

No .NET SDK, no CMake, no compiler, no command line wizardry. If the zip path ever fails
you, the fallback is building the CLI from source (section 3).

**Builders (optional):** [.NET SDK 10.0+](https://dotnet.microsoft.com/download), plus
CMake 3.20+, Ninja, and a C++17 compiler (MinGW-w64) for the native injector.

## 2. Install from the release zip (no build tools)

Every step below says what success looks like. Go slowly; each step takes seconds.

**Step 1. Back up the game.** Copy the whole game folder to a second location. Skip this
only if you can re-download the game quickly.

**Step 2. Unzip into place.** Open `nami-1.0.0.zip`. Inside is the *contents* of a
`nami/` folder (flat framework layout: `native/`, `dotnet/`, managed DLLs, plus a
`manifest.json` of SHA-256 hashes) - there is no `nami/` folder in the zip itself.
Create a `nami/` folder directly inside the game folder, next to the game exe, and
extract the zip into it. Then create an empty `mods/` folder inside:

```
YourGame/
  YourGame.exe
  YourGame_Data/
  nami/                  <-- create this, extract the zip into it
    Nami.Runtime.dll
    Nami.Core.dll
    ...
    native/nami_boot.exe
    native/nami_loader.dll
    dotnet/              <-- bundled .NET, you do nothing with it
    manifest.json        <-- hash manifest, leave it alone
    mods/                <-- create empty
```

Success: `<game>/nami/mods/` exists and is empty. Nothing else in the game folder changed.

**Step 3. Tell Nami which exe is the game.** Create `<game>/nami/nami.json` with this
content (replace the exe name with yours):

```json
{
  "gameExe": "C:\\Games\\YourGame\\YourGame.exe"
}
```

Use double backslashes in JSON paths. Why this matters: without it Nami guesses the
largest `.exe`, which is sometimes a crash handler, and then "nothing happens" in confusing
ways. This one line removes a whole class of confusion. (The file tolerates comments,
trailing commas, and missing keys - anything absent boots with defaults.)

**Step 4. Add mods (or skip).** For a first smoke test, skip this step entirely: Nami
boots fine with zero mods, and that proves the machinery before any mod is involved. When
you have mod DLLs, drop the loose `.dll` files straight into `<game>/nami/mods/` (not
into subfolders).

**Step 5. Boot it.** Open a terminal in `<game>/nami/native/` and run (replace the exe
name; keep the quotes):

```bat
nami_boot.exe "C:\Games\YourGame\YourGame.exe" "C:\Games\YourGame\nami\native\nami_loader.dll"
```

The game window should open the way it always does. That is the whole trick: the game
starts suspended for a moment, Nami loads, then the game resumes and plays normally.

**Step 6. Confirm it worked.** While the game runs, open `<game>/nami/nami.log` in a text
editor. You want to see the boot block:

```
[boot]        Nami managed runtime booting (nami_root=...\nami)
[boot]        clr=10.0.10 os=Microsoft Windows ... arch=X64
[boot]        chainloader activated: 1 plugin(s) loaded
```

With zero mods the count reads `0 plugin(s)` - still a pass. If a mod is present you
should see a `Loaded <id> <version> (<file>)` line for it. Close the game normally when
done; the log stays on disk for inspection.

**Step 7. Make a double-click shortcut (optional).** Create `Start with Nami.bat` next to
the game exe containing the same command from step 5 (with your real paths). Double-click
it instead of the game exe whenever you want mods. Starting the game exe directly always
boots it clean and unmodded - useful for comparison when something looks off.

## 3. The CLI path (build from source)

Do this if you want the `nami` command (one-line install/launch, mod scaffolding,
doctor checks) or if you plan to write mods. Players who finished section 2 can stop.

```bat
::: everything (src + tests + fixtures + samples + Wave.Bench + launch-shim)
dotnet build Nami.slnx

::: native injector + loader
cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build
```

Then, from the repo root:

```bat
::: stage a root from build outputs (Release tree; refuses stale outputs, see below)
nami install "C:\path\to\YourGame"

:::: same, but from the release artifact (every file hash-verified against manifest.json)
nami install "C:\path\to\YourGame" --from nami-1.0.0.zip

::: remember the game exe once (add --steam-id <appid> for Steam titles)
nami launch set "C:\path\to\YourGame\YourGame.exe" "C:\path\to\YourGame"

::: run with Nami injected; auto-detects the exe only when unset
nami launch "C:\path\to\YourGame"

:::: same, but through the Steam client (needs --steam-id set above; refuses without one)
nami launch steam "C:\path\to\YourGame"

::: double-clickable shortcut + batch file into the nami root
nami create "C:\path\to\YourGame"

::: modder loop: build a mod project, stage it into nami/mods, launch
nami run "MyFirstMod\MyFirstMod.csproj" "C:\path\to\YourGame"

::: sanity check of an install
nami doctor "C:\path\to\YourGame"
```

Two behaviors that surprise people, both deliberate:

- Stage and pack **refuse stale build outputs** (a Debug tree newer than Release, or native
  binaries older than their C++ sources) instead of silently booting old code. Rebuild
  Release outputs and retry.
- Every stage **deletes retired root-level `nami_loader.dll` copies** (an old layout left
  one next to the managed DLLs, where it hijacks loading). `nami doctor` reports the
  `layout : current` (or `OBSOLETE`) line for the same reason.

## 4. If something looks wrong

Work through this table top to bottom. Almost everything is answered by `nami/nami.log`
plus, for game-call failures, `nami/native/nami-tide.log`.

| Symptom | Most likely cause | What to do |
|---|---|---|
| Game opens, log has no `[boot]` block | Nami never injected (wrong exe path, AV removed a file, stale root) | Re-check the step 5 command paths letter by letter; confirm `native/nami_boot.exe` and `native/nami_loader.dll` exist; check antivirus quarantine |
| Game opens clean, log ends before mods load | Safe mode from an earlier native crash | Look for `safe-mode` in `nami/`; it auto-clears after 3 clean boots, or delete it to restore Nami now; read `nami-crash.log` |
| `Loaded` line missing for your mod | Mod not discovered | DLL must sit directly in `nami/mods/` (top level); check `enabledPlugins` in `nami.json` is empty or includes it; look for a load error naming the file |
| Mod loads then disappears from later ticks | Quarantine tripped (repeated `OnUpdate` throws) | Log names the mod and the reason; fix the mod; the game kept running, which is the feature working |
| `TideException ... code -1` | Member not found (wrong assembly/class/member spelling, case, or arity) | Check names against the game version; details in `nami-tide.log` |
| `TideException ... code -3` | No game window yet (IL2CPP executor needs a visible window) | Retry after the game window exists; mods should call `Tide.EnsureReady()` first |
| `detour refused` in `nami-tide.log` | Method prologue too short or unsafe to patch | Nami refused rather than corrupt anything; that specific hook is unsupported on this title |
| `nami install` says stale outputs | Mixed Debug/Release trees (source path only) | Rebuild Release outputs, or reinstall `--from` the release zip |
| Steam overlay / online modes | Default launch is offline | For Steam titles set `--steam-id`; for anti-cheat games prefer not injecting at all (see FAQ) |
| Updated Nami, old behavior persists | Something cached the old files | Re-extract the zip over `nami/` (mods, saves, logs, and markers survive; framework files refresh) |

If none of this fits, the report that gets fixed fastest names: the game and its Unity
version, what you expected, what happened instead, and the relevant lines from
`nami/nami.log` (plus `nami-tide.log` for game-call failures).

## 5. FAQ

**Can I get banned for this?**
Nobody can promise you safety. Nami injects code into the game process, and anti-cheat
systems are allowed to dislike that. Offline single-player is the sane default. For online
or anti-cheat titles, either do not inject or use setups that launch cleanly; Nami cannot
make injection undetectable and does not try to hide.

**Does this slow my game down?**
With no mods, the cost is the injection plus a per-frame hook that costs two atomic reads
when idle (verified stable over thousands of ticks in-game). Mods cost whatever the mods
do; the per-mod profiler (`profiler` source in the log) shows exact per-mod timings when
you wonder who spent your frame budget.

**Where do I get mods?**
From mod authors (loose DLLs into `nami/mods/`, or `.nmod` packages via `nami nmod
install` if you have the CLI). There is no central mod store run by Nami.

**How do I update Nami?**
Re-extract the new release zip over `<game>/nami/`. Your mods, config, logs, and markers
are user content and survive; framework files refresh. Then boot once and check the log
head for the new version line.

**How do I uninstall?**
Delete `<game>/nami/` (and your shortcut/batch file if you made one). That is everything
Nami ever created. Verify the game folder matches your backup if you want to be thorough.

**The game updated and mods broke.**
Normal: game updates change game code that mods and hooks target. Wait for mod updates,
and check `nami.log` - member-not-found errors after a game patch mean exactly this.

**Can I use this on Linux / Steam Deck / Mac?**
Not yet. Windows x64 only; non-Windows platforms are on the missing list.

## 6. What next

- Run the bundled sample mod: with the CLI, `nami run "samples\HelloNami\HelloNami.csproj" "%GAME%"`.
  Without it, you need the compiled `HelloNami.dll` in `nami/mods/` by hand.
- Write your own: [docs/modding.md](docs/modding.md) (scaffolding, Tide game access,
  config, CLI reference, tests, limits).
- Understand the machinery: [docs/architecture.md](docs/architecture.md),
  [docs/tide.md](docs/tide.md), [docs/wave.md](docs/wave.md).
