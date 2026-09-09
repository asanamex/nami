using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Nami.Cli.Commands;

namespace Nami.Cli.Tests;

/// <summary>
/// `nami nmod info|install` - .nmod package distribution (manifest info; verified install
/// into a game root's mods/ with incompatibility refusal and dependency warnings).
/// </summary>
public sealed class NmodCommandTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _gameDir;

    public NmodCommandTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_baseDir, "game");
        Directory.CreateDirectory(_gameDir);
        Directory.CreateDirectory(Path.Combine(_gameDir, "nami"));  // a Nami root (FindRoot hit)
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_baseDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private static string MakePackage(string dir, string id, string? dep = null,
        string? incompatible = null, string dllName = "Plugin.dll")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id}.nmod");
        var manifest = new Dictionary<string, object>
        {
            ["id"] = id,
            ["name"] = id.ToUpperInvariant(),
            ["version"] = "1.2.3",
            ["description"] = "test package"
        };
        if (dep is not null)
        {
            manifest["dependencies"] = new[] { dep };
        }

        if (incompatible is not null)
        {
            manifest["incompatibilities"] = new[] { incompatible };
        }

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("mod.json");
            using (var w = new StreamWriter(entry.Open(), Encoding.UTF8))
            {
                w.Write(JsonSerializer.Serialize(manifest));
            }

            var dll = zip.CreateEntry(dllName);
            using (var w = new StreamWriter(dll.Open(), Encoding.UTF8))
            {
                w.Write("fake-plugin-bytes");
            }
        }

        return path;
    }

    private string ModsDir => Path.Combine(_gameDir, "nami", "mods");

    [Fact]
    public void Nmod_Install_ExtractsIntoMods()
    {
        var pkg = MakePackage(Path.Combine(_baseDir, "pkgs"), "mymod");
        var rc = NmodCommand.Run(_gameDir, new[] { "install", pkg });

        Assert.Equal(0, rc);
        Assert.True(File.Exists(Path.Combine(ModsDir, "mymod", "mod.json")));
        Assert.True(File.Exists(Path.Combine(ModsDir, "mymod", "Plugin.dll")));
    }

    [Fact]
    public void Nmod_Install_UpgradeReplacesExistingPackage()
    {
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(Path.Combine(ModsDir, "mymod"));
        File.WriteAllText(Path.Combine(ModsDir, "mymod", "stale.txt"), "old");

        var pkg = MakePackage(Path.Combine(_baseDir, "pkgs"), "mymod");
        var rc = NmodCommand.Run(_gameDir, new[] { "install", pkg });

        Assert.Equal(0, rc);
        Assert.False(File.Exists(Path.Combine(ModsDir, "mymod", "stale.txt")));  // replaced, not merged
        Assert.True(File.Exists(Path.Combine(ModsDir, "mymod", "mod.json")));
    }

    [Fact]
    public void Nmod_Info_PrintsManifest()
    {
        var pkg = MakePackage(Path.Combine(_baseDir, "pkgs"), "infomod", dep: "libmod");
        var rc = NmodCommand.Run(_gameDir, new[] { "info", pkg });

        Assert.Equal(0, rc);  // file-system + exit-code contract (console text is not asserted)
    }

    [Fact]
    public void Nmod_Install_Incompatible_RefusesWithoutTouchingMods()
    {
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(Path.Combine(ModsDir, "other"));
        File.WriteAllText(Path.Combine(ModsDir, "other", "mod.json"),
            "{\"id\":\"other\",\"name\":\"Other\",\"version\":\"1.0.0\"}");

        var pkg = MakePackage(Path.Combine(_baseDir, "pkgs"), "clashmod", incompatible: "other");
        var rc = NmodCommand.Run(_gameDir, new[] { "install", pkg });

        Assert.NotEqual(0, rc);
        Assert.False(Directory.Exists(Path.Combine(ModsDir, "clashmod")));  // refused before extraction
    }

    [Fact]
    public void Nmod_Install_DependsOnMissingPackage_StillInstalls()
    {
        // The dependency is not installed anywhere: install still succeeds (best-effort
        // loose plugins cannot be enumerated), the warning goes to stderr.
        var pkg = MakePackage(Path.Combine(_baseDir, "pkgs"), "needylib", dep: "not-installed-lib");
        var rc = NmodCommand.Run(_gameDir, new[] { "install", pkg });

        Assert.Equal(0, rc);
        Assert.True(Directory.Exists(Path.Combine(ModsDir, "needylib")));
    }

    [Fact]
    public void Nmod_Install_NoRoot_Errors()
    {
        var noRoot = Path.Combine(_baseDir, "no-root-game");
        Directory.CreateDirectory(noRoot);
        var pkg = MakePackage(Path.Combine(_baseDir, "pkgs"), "orphanmod");
        var rc = NmodCommand.Run(noRoot, new[] { "install", pkg });

        Assert.NotEqual(0, rc);
        Assert.False(Directory.Exists(Path.Combine(noRoot, "mods")));
    }

    [Fact]
    public void Nmod_Install_MissingPackageFile_Errors()
    {
        var rc = NmodCommand.Run(_gameDir, new[] { "install", Path.Combine(_baseDir, "does-not-exist.nmod") });
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void Nmod_Install_InvalidPackage_Errors()
    {
        var notAPackage = Path.Combine(_baseDir, "not-a-package.nmod");
        using (var zip = ZipFile.Open(notAPackage, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("no manifest here");
        }

        var rc = NmodCommand.Run(_gameDir, new[] { "install", notAPackage });
        Assert.NotEqual(0, rc);
        Assert.False(Directory.Exists(Path.Combine(ModsDir, "not-a-package")));
    }
}
