# Contributing to Nami

Three ways to help, in order of value. No coding required for the first two.

## 1. Test your games (most valuable)

Nami is verified on a handful of titles and every Unity build differs. Run it
against your games and report back. Working reports count as much as broken ones.

Open an issue titled `Title: <game name>` with:

```
Nami version:
Unity version (from the game log or output_log.txt):
Mono or IL2CPP:
What you tried (boot / SlowMo toggle / demo mod / your own mod):
What happened:
nami/nami.log lines around the problem (last ~30):
```

Confirmed reports land on the [Verified-Games wiki page](https://github.com/asanamex/nami/wiki/Verified-Games).

## 2. Write or port a mod

Start from [nami-demo](https://github.com/asanamex/nami-demo): four tiny working
mods plus compiled DLLs. The [First-Mod wiki page](https://github.com/asanamex/nami/wiki/First-Mod)
covers the lifecycle, Tide calls, config, and hot reload. Broken mods found in
the wild (with logs) are welcome as issues too.

## 3. Change the code

Prerequisites: .NET SDK 10.0+. Native code: CMake 3.20+, Ninja, C++17 (MinGW-w64).

```powershell
dotnet build Nami.slnx -c Release
dotnet test tests/Nami.Tests/Nami.Tests.csproj -c Release
dotnet test tests/Nami.Wave.Tests/Nami.Wave.Tests.csproj -c Release
dotnet test tests/Nami.Cli.Tests/Nami.Cli.Tests.csproj -c Release
dotnet test tests/Nami.Tide.Tests/Nami.Tide.Tests.csproj -c Release
cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build
```

Conventions:

- Release is the gate. Keep `dotnet build -c Release` at 0 warnings (treat-warnings-as-errors is on).
- One change per pull request. No unrelated refactors, no drive-by fixes.
- Match the surrounding style. Boring code wins.
- Tests prove behavior, not implementation. A bug fix should arrive with a test that fails without it.
- Docs live next to the code they describe (`docs/`, wiki for operators). If behavior changes, update both.
- Never reference specific test games or app IDs in public text. Use placeholders.

Pull requests target `master` and need a clean gate before merge.
