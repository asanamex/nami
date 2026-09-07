using Nami.Cli.Commands;

namespace Nami.Cli.Tests;

public sealed class StagerTests : IDisposable
{
    private readonly string _gameDir;
    private readonly string _artifacts;
    private readonly string _repo;

    public StagerTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(baseDir, "game");
        _artifacts = Path.Combine(baseDir, "artifacts");
        _repo = Path.Combine(baseDir, "repo");
        Directory.CreateDirectory(_gameDir);
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_gameDir)!, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private void WriteArtifacts()
    {
        Directory.CreateDirectory(_artifacts);
        foreach (var f in Stager.ManagedFiles)
        {
            File.WriteAllText(Path.Combine(_artifacts, f), "x");
        }

        Directory.CreateDirectory(Path.Combine(_artifacts, "native", "build"));
        File.WriteAllText(Path.Combine(_artifacts, "native", "build", "nami_boot.exe"), "x");
        File.WriteAllText(Path.Combine(_artifacts, "native", "build", "nami_loader.dll"), "x");

        Directory.CreateDirectory(Path.Combine(_artifacts, "dotnet", "host", "fxr", "10.0.0"));
        File.WriteAllText(Path.Combine(_artifacts, "dotnet", "host", "fxr", "10.0.0", "hostfxr.dll"), "x");
    }

    [Fact]
    public void Stage_WithArtifacts_CreatesFullRoot()
    {
        WriteArtifacts();
        var staged = Stager.Stage(_gameDir, _repo, _artifacts);

        Assert.Equal(Path.Combine(_gameDir, "nami"), staged.Root);
        foreach (var f in Stager.ManagedFiles)
        {
            Assert.True(File.Exists(Path.Combine(staged.Root, f)), $"{f} should be staged");
        }

        Assert.True(File.Exists(Path.Combine(staged.Root, "native", "nami_boot.exe")));
        Assert.True(File.Exists(Path.Combine(staged.Root, "native", "nami_loader.dll")));
        Assert.True(File.Exists(Path.Combine(staged.Root, "dotnet", "host", "fxr", "10.0.0", "hostfxr.dll")));
        Assert.True(Directory.Exists(Path.Combine(staged.Root, "mods")));
        Assert.True(File.Exists(Path.Combine(staged.Root, "nami.json")));
    }

    [Fact]
    public void Stage_MissingArtifacts_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Stager.Stage(_gameDir, _repo, _artifacts));
        Assert.Contains("missing build artifacts", ex.Message);
    }

    [Fact]
    public void Stage_IsIdempotent_Overwrites()
    {
        WriteArtifacts();
        Stager.Stage(_gameDir, _repo, _artifacts);
        // A second stage should not throw and should keep a valid root.
        var staged = Stager.Stage(_gameDir, _repo, _artifacts);
        Assert.Equal(Path.Combine(_gameDir, "nami"), staged.Root);
    }
}

public sealed class RunCommandArgTests
{
    [Fact]
    public void MissingProject_ReturnsUsageError()
    {
        // RunCommand.Run validates the project path before touching a root.
        var exit = RunCommand.Run(Path.GetTempPath(), Array.Empty<string>());
        Assert.Equal(2, exit);
    }
}

public sealed class RunCommandStageTests : IDisposable
{
    private readonly string _dir;

    public RunCommandStageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void StageModOutput_CopiesNonNamiDlls_AndSkipsFramework()
    {
        var output = Path.Combine(_dir, "out");
        var mods = Path.Combine(_dir, "mods");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "MyFirstMod.dll"), "x");
        File.WriteAllText(Path.Combine(output, "Nami.Sdk.dll"), "x");
        File.WriteAllText(Path.Combine(output, "Nami.Tide.dll"), "x");

        var count = RunCommand.StageModOutput(output, mods);

        Assert.Equal(1, count);
        Assert.True(File.Exists(Path.Combine(mods, "MyFirstMod.dll")));
        Assert.False(File.Exists(Path.Combine(mods, "Nami.Sdk.dll")));
    }

    [Fact]
    public void FindOutputDir_ReturnsTfmFolder()
    {
        var projectDir = Path.Combine(_dir, "proj");
        var tfm = Path.Combine(projectDir, "bin", "Release", "net10.0");
        Directory.CreateDirectory(tfm);
        File.WriteAllText(Path.Combine(projectDir, "proj.csproj"), "<Project />");

        var result = RunCommand.FindOutputDir(Path.Combine(projectDir, "proj.csproj"));

        Assert.Equal(tfm, result);
    }

    [Fact]
    public void FindOutputDir_MissingBin_ReturnsNull()
    {
        var projectDir = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "empty.csproj"), "<Project />");

        Assert.Null(RunCommand.FindOutputDir(Path.Combine(projectDir, "empty.csproj")));
    }
}
