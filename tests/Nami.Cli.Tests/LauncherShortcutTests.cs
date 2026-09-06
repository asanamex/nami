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
}
