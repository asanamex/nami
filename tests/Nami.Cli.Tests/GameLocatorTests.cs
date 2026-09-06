namespace Nami.Cli.Tests;

public sealed class GameLocatorTests : IDisposable
{
    private readonly string _gameDir;

    public GameLocatorTests()
    {
        _gameDir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_gameDir);
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

    private void WriteExe(string name, int size)
    {
        var path = Path.Combine(_gameDir, name);
        File.WriteAllBytes(path, new byte[size]);
        // PE signature so LooksLikePeExecutable passes.
        using var fs = File.OpenWrite(path);
        fs.Position = 0;
        fs.WriteByte((byte)'M');
        fs.WriteByte((byte)'Z');
        fs.Position = 0x3C;
        fs.WriteByte(0x40);
        fs.Position = 0x40;
        fs.WriteByte((byte)'P');
        fs.WriteByte((byte)'E');
    }

    [Fact]
    public void AutoDetect_PicksLargestExe()
    {
        WriteExe("UnityCrashHandler64.exe", 100_000);
        WriteExe("MyGame.exe", 50_000_000);
        WriteExe("small.exe", 10_000);

        var detected = GameLocator.AutoDetect(_gameDir);
        Assert.Equal(Path.Combine(_gameDir, "MyGame.exe"), detected);
    }

    [Fact]
    public void AutoDetect_SkipsKnownNonGame_AndFallsBackToSubdir()
    {
        WriteExe("UnityCrashHandler.exe", 200_000_000);
        Directory.CreateDirectory(Path.Combine(_gameDir, "bin"));
        File.WriteAllBytes(Path.Combine(_gameDir, "bin", "realgame.exe"), new byte[5_000_000]);

        var detected = GameLocator.AutoDetect(_gameDir);
        Assert.Equal(Path.Combine(_gameDir, "bin", "realgame.exe"), detected);
    }

    [Fact]
    public void AutoDetect_ReturnsNull_WhenNothingPlausible()
    {
        WriteExe("UnityCrashHandler.exe", 100);
        Assert.Null(GameLocator.AutoDetect(_gameDir));
    }

    [Fact]
    public void ResolveExplicit_BareName_ResolvesAgainstGameDir()
    {
        WriteExe("MyGame.exe", 1000);
        var resolved = GameLocator.ResolveExplicit(_gameDir, "MyGame.exe");
        Assert.Equal(Path.Combine(_gameDir, "MyGame.exe"), resolved);
    }

    [Fact]
    public void ResolveExplicit_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => GameLocator.ResolveExplicit(_gameDir, "Nope.exe"));
    }

    [Fact]
    public void ResolveExplicit_KnownNonGame_Refuses_UnlessForced()
    {
        WriteExe("UnityCrashHandler64.exe", 1000);
        Assert.Throws<ArgumentException>(() => GameLocator.ResolveExplicit(_gameDir, "UnityCrashHandler64.exe"));
        var forced = GameLocator.ResolveExplicit(_gameDir, "UnityCrashHandler64.exe", force: true);
        Assert.Equal(Path.Combine(_gameDir, "UnityCrashHandler64.exe"), forced);
    }
}
