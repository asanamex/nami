using Nami.Cli.Commands;
using Nami.Core.Configuration;

namespace Nami.Cli.Tests;

public sealed class LauncherAndShortcutTests : IDisposable
{
    private readonly string _root;
    private readonly string _gameDir;

    public LauncherAndShortcutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"), "nami");
        Directory.CreateDirectory(_root);
        _gameDir = Path.GetDirectoryName(_root)!;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_gameDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private void StageCompleteRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "native"));
        File.WriteAllText(Path.Combine(_root, "native", "nami_boot.exe"), "x");
        File.WriteAllText(Path.Combine(_root, "native", "nami_loader.dll"), "x");
        foreach (var f in Launcher.RequiredRootFiles)
        {
            File.WriteAllText(Path.Combine(_root, f), "x");
        }

        Directory.CreateDirectory(Path.Combine(_root, "dotnet", "host", "fxr", "10.0.0"));
        File.WriteAllText(Path.Combine(_root, "dotnet", "host", "fxr", "10.0.0", "hostfxr.dll"), "x");
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public string? FileName;
        public string? Arguments;
        public string? WorkingDir;
        public int ExitCode;

        public int Run(string fileName, string arguments, string workingDirectory)
        {
            FileName = fileName;
            Arguments = arguments;
            WorkingDir = workingDirectory;
            return ExitCode;
        }
    }

    [Fact]
    public void LaunchInjected_ReportsMissingFiles()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Launcher.LaunchInjected(Path.Combine(_gameDir, "Game.exe"), _root, new FakeRunner()));
        Assert.Contains("incomplete", ex.Message);
        Assert.Contains("nami_boot.exe", ex.Message);
    }

    [Fact]
    public void LaunchInjected_BuildsCorrectCommandLine()
    {
        StageCompleteRoot();
        var gameExe = Path.Combine(_gameDir, "Game.exe");
        File.WriteAllText(gameExe, "game");

        var runner = new FakeRunner { ExitCode = 0 };
        var message = Launcher.LaunchInjected(gameExe, _root, runner);

        Assert.Equal(Path.Combine(_root, "native", "nami_boot.exe"), runner.FileName);
        Assert.Equal($"\"{gameExe}\" \"{Path.Combine(_root, "native", "nami_loader.dll")}\"", runner.Arguments);
        Assert.Equal(_gameDir, runner.WorkingDir);
        Assert.Equal("game exited (Nami was injected)", message);
    }

    [Fact]
    public void LaunchCommand_ResolveGameExe_UsesConfigured()
    {
        var config = new NamiConfig { GameExe = Path.Combine(_gameDir, "Game.exe") };
        File.WriteAllText(config.GameExe, "game");
        Assert.Equal(config.GameExe, LaunchCommand.ResolveGameExe(_gameDir, config));
    }

    [Fact]
    public void LaunchCommand_ResolveGameExe_MissingConfigured_Throws()
    {
        var config = new NamiConfig { GameExe = Path.Combine(_gameDir, "Gone.exe") };
        Assert.Throws<InvalidOperationException>(() => LaunchCommand.ResolveGameExe(_gameDir, config));
    }

    [Fact]
    public void ShortcutGenerator_WritesShimAndBat()
    {
        StageCompleteRoot();
        var gameExe = Path.Combine(_gameDir, "Game.exe");
        File.WriteAllText(gameExe, "game");

        ShortcutGenerator.WriteShortcuts(_root, gameExe, "offline");

        var shim = Path.Combine(_root, ShortcutGenerator.ShimFileName);
        Assert.True(File.Exists(shim), "launchNami.exe should be extracted from the embedded resource");
        Assert.True(File.Exists(Path.Combine(_root, ShortcutGenerator.BatFileName)));

        var bat = File.ReadAllText(Path.Combine(_root, ShortcutGenerator.BatFileName));
        Assert.Contains("nami_boot.exe", bat);
        Assert.Contains(gameExe, bat);
    }

    [Fact]
    public void SteamMode_StartsMissingClient_BootMatchesOffline()
    {
        StageCompleteRoot();
        var gameExe = Path.Combine(_gameDir, "Game.exe");
        File.WriteAllText(gameExe, "game");

        var originalOpenUri = Launcher.OpenUri;
        var originalRunning = Launcher.SteamClientRunning;
        var uris = new List<string>();
        var calls = 0;
        Launcher.OpenUri = uri => uris.Add(uri);
        Launcher.SteamClientRunning = () => { calls++; return calls > 1; };
        try
        {
            Launcher.EnsureSteamRunning();

            var runner = new FakeRunner { ExitCode = 0 };
            var message = Launcher.LaunchInjected(gameExe, _root, runner);

            Assert.Equal(Path.Combine(_root, "native", "nami_boot.exe"), runner.FileName);
            Assert.Equal($"\"{gameExe}\" \"{Path.Combine(_root, "native", "nami_loader.dll")}\"", runner.Arguments);
            Assert.Equal(_gameDir, runner.WorkingDir);
            Assert.Equal("game exited (Nami was injected)", message);
            Assert.Single(uris);
            Assert.Equal("steam://", uris[0]);
        }
        finally
        {
            Launcher.OpenUri = originalOpenUri;
            Launcher.SteamClientRunning = originalRunning;
        }
    }

    [Fact]
    public void SteamMode_SkipsStartWhenRunning()
    {
        var originalOpenUri = Launcher.OpenUri;
        var originalRunning = Launcher.SteamClientRunning;
        var uris = new List<string>();
        Launcher.OpenUri = uri => uris.Add(uri);
        Launcher.SteamClientRunning = () => true;
        try
        {
            Launcher.EnsureSteamRunning();
            Assert.Empty(uris);
        }
        finally
        {
            Launcher.OpenUri = originalOpenUri;
            Launcher.SteamClientRunning = originalRunning;
        }
    }

    [Fact]
    public void SteamStart_TimeoutThrowsBeforeBoot()
    {
        StageCompleteRoot();
        var originalOpenUri = Launcher.OpenUri;
        var originalRunning = Launcher.SteamClientRunning;
        var uris = new List<string>();
        Launcher.OpenUri = uri => uris.Add(uri);
        Launcher.SteamClientRunning = () => false;
        try
        {
            var runner = new FakeRunner();
            var ex = Assert.Throws<InvalidOperationException>(() => Launcher.EnsureSteamRunning(0));
            Assert.Contains("did not appear within 0s", ex.Message);
            Assert.Null(runner.FileName);
            Assert.Single(uris);
        }
        finally
        {
            Launcher.OpenUri = originalOpenUri;
            Launcher.SteamClientRunning = originalRunning;
        }
    }

    [Fact]
    public void SteamPath_NeverEmitsRungameid()
    {
        var originalOpenUri = Launcher.OpenUri;
        var originalRunning = Launcher.SteamClientRunning;
        var uris = new List<string>();
        var calls = 0;
        Launcher.OpenUri = uri => uris.Add(uri);
        Launcher.SteamClientRunning = () => { calls++; return calls > 1; };
        try
        {
            Launcher.EnsureSteamRunning();
            Assert.NotEmpty(uris);
            Assert.DoesNotContain(uris, u => u.Contains("rungameid"));
        }
        finally
        {
            Launcher.OpenUri = originalOpenUri;
            Launcher.SteamClientRunning = originalRunning;
        }
    }

    [Fact]
    public void EnsureSteamAppContext_ConfigAppId_WritesFile()
    {
        var gameExe = Path.Combine(_gameDir, "Game.exe");
        File.WriteAllText(gameExe, "game");
        var config = new NamiConfig { SteamAppId = "2386580" };

        var appId = LaunchCommand.EnsureSteamAppContext(gameExe, config);

        Assert.Equal("2386580", appId);
        Assert.Equal("2386580", File.ReadAllText(Path.Combine(_gameDir, "steam_appid.txt")));
    }

    [Fact]
    public void EnsureSteamAppContext_ExistingFile_NoConfig_UsesFile()
    {
        var gameExe = Path.Combine(_gameDir, "Game.exe");
        File.WriteAllText(gameExe, "game");
        File.WriteAllText(Path.Combine(_gameDir, "steam_appid.txt"), "2495980\n");
        var config = new NamiConfig();

        var appId = LaunchCommand.EnsureSteamAppContext(gameExe, config);

        Assert.Equal("2495980", appId);
    }

    [Fact]
    public void EnsureSteamAppContext_ConfigWinsOverFile()
    {
        var gameExe = Path.Combine(_gameDir, "Game.exe");
        File.WriteAllText(gameExe, "game");
        File.WriteAllText(Path.Combine(_gameDir, "steam_appid.txt"), "111");
        var config = new NamiConfig { SteamAppId = "2386580" };

        var appId = LaunchCommand.EnsureSteamAppContext(gameExe, config);

        Assert.Equal("2386580", appId);
        Assert.Equal("2386580", File.ReadAllText(Path.Combine(_gameDir, "steam_appid.txt")));
    }

    [Fact]
    public void EnsureSteamAppContext_MissingEverywhere_Throws()
    {
        var gameExe = Path.Combine(_gameDir, "Game.exe");
        File.WriteAllText(gameExe, "game");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LaunchCommand.EnsureSteamAppContext(gameExe, new NamiConfig()));
        Assert.Contains("--steam-id", ex.Message);
        Assert.False(File.Exists(Path.Combine(_gameDir, "steam_appid.txt")));
    }

    [Fact]
    public void EnsureSteamAppContext_InvalidAppId_Throws()
    {
        var gameExe = Path.Combine(_gameDir, "Game.exe");
        File.WriteAllText(gameExe, "game");
        File.WriteAllText(Path.Combine(_gameDir, "steam_appid.txt"), "not-an-id");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LaunchCommand.EnsureSteamAppContext(gameExe, new NamiConfig()));
        Assert.Contains("not-an-id", ex.Message);
    }

}
