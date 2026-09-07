using Nami.Cli.Commands;

namespace Nami.Cli.Tests;

/// <summary>Roundtrips for `nami inex install/enable/disable/status` on temp dirs
/// with a fake legacy payload (existence checks only — no real BepInEx needed).</summary>
public sealed class InexCommandTests : IDisposable
{
    private readonly string _dir;

    public InexCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nami-cli-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "nami"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string MakePayload(bool withPreloader = true)
    {
        var src = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(src, "core"));
        Directory.CreateDirectory(Path.Combine(src, "plugins"));
        if (withPreloader)
        {
            File.WriteAllText(Path.Combine(src, "core", "BepInEx.Preloader.dll"), "fake");
        }

        File.WriteAllText(Path.Combine(src, "plugins", "Mod.dll"), "fake");
        Directory.CreateDirectory(Path.Combine(src, "cache"));
        File.WriteAllText(Path.Combine(src, "cache", "junk.dat"), "fake");
        return src;
    }

    [Fact]
    public void InstallCopiesPayloadSkipsCache()
    {
        var exit = InexCommand.Run(_dir, ["install", MakePayload()]);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_dir, "nami", "inex", "BepInEx", "core", "BepInEx.Preloader.dll")));
        Assert.True(File.Exists(Path.Combine(_dir, "nami", "inex", "BepInEx", "plugins", "Mod.dll")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "nami", "inex", "BepInEx", "cache")));
        // Stays disabled until explicit enable.
        Assert.False(File.Exists(Path.Combine(_dir, "nami", "inex", "enabled")));
    }

    [Fact]
    public void InstallRejectsNonPayload()
    {
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(1, InexCommand.Run(_dir, ["install", empty]));
    }

    [Fact]
    public void EnableRequiresPayloadThenTogglesSentinel()
    {
        Assert.Equal(1, InexCommand.Run(_dir, ["enable"]));
        Assert.Equal(0, InexCommand.Run(_dir, ["install", MakePayload()]));
        Assert.Equal(0, InexCommand.Run(_dir, ["enable"]));
        Assert.True(File.Exists(Path.Combine(_dir, "nami", "inex", "enabled")));
        Assert.Equal(0, InexCommand.Run(_dir, ["disable"]));
        Assert.False(File.Exists(Path.Combine(_dir, "nami", "inex", "enabled")));
    }

    [Fact]
    public void StatusAndUsageDoNotThrow()
    {
        Assert.Equal(0, InexCommand.Run(_dir, ["status"]));
        Assert.Equal(1, InexCommand.Run(_dir, []));
        Assert.Equal(1, InexCommand.Run(_dir, ["bogus"]));
    }

    [Fact]
    public void RequiresStagedRoot()
    {
        var bare = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bare);
        try
        {
            Assert.Equal(1, InexCommand.Run(bare, ["status"]));
            Assert.Equal(1, InexCommand.Run(bare, ["enable"]));
        }
        finally
        {
            Directory.Delete(bare, recursive: true);
        }
    }
}
