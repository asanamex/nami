using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Nami.Cli;

namespace Nami.Cli.Tests;

/// <summary>
/// Nami-Install: the self-contained installer artifact - `nami pack` (zip + SHA-256 manifest)
/// and `nami install --from <artifact>` (hash-verified, upgrade-safe extraction).
/// </summary>
public sealed class PackInstallTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _artifacts;
    private readonly string _repo;
    private readonly string _gameDir;

    public PackInstallTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        _artifacts = Path.Combine(_baseDir, "artifacts");
        _repo = Path.Combine(_baseDir, "repo");
        _gameDir = Path.Combine(_baseDir, "game");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_gameDir);
        WriteArtifacts();
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

    private string ArtifactPath => Path.Combine(_baseDir, "nami-1.0.0.zip");

    /// <summary>Builds a fake artifacts root whose files are distinguishable by content.</summary>
    private void WriteArtifacts()
    {
        Directory.CreateDirectory(_artifacts);
        foreach (var f in Stager.ManagedFiles)
        {
            File.WriteAllText(Path.Combine(_artifacts, f), f);
        }

        Directory.CreateDirectory(Path.Combine(_artifacts, "native", "build"));
        File.WriteAllText(Path.Combine(_artifacts, "native", "build", "nami_boot.exe"), "nami_boot.exe");
        File.WriteAllText(Path.Combine(_artifacts, "native", "build", "nami_loader.dll"), "nami_loader.dll");

        Directory.CreateDirectory(Path.Combine(_artifacts, "dotnet", "host", "fxr", "10.0.0"));
        File.WriteAllText(Path.Combine(_artifacts, "dotnet", "host", "fxr", "10.0.0", "hostfxr.dll"), "hostfxr.dll");
    }

    [Fact]
    public void Pack_ProducesCompleteSelfContainedArtifact()
    {
        var result = Stager.Pack(_repo, ArtifactPath, _artifacts);

        Assert.Equal("1.0.0", result.Version);
        Assert.Equal("10.0.0", result.Runtime);
        Assert.True(File.Exists(ArtifactPath));

        using var zip = ZipFile.OpenRead(ArtifactPath);
        foreach (var f in Stager.ManagedFiles)
        {
            Assert.NotNull(zip.GetEntry(f));
        }

        Assert.NotNull(zip.GetEntry("native/nami_boot.exe"));
        Assert.NotNull(zip.GetEntry("native/nami_loader.dll"));
        Assert.NotNull(zip.GetEntry("dotnet/host/fxr/10.0.0/hostfxr.dll"));
        Assert.NotNull(zip.GetEntry(Stager.ManifestFileName));

        using var reader = new StreamReader(zip.GetEntry(Stager.ManifestFileName)!.Open());
        using var manifest = JsonDocument.Parse(reader.ReadToEnd());
        Assert.Equal("nami", manifest.RootElement.GetProperty("product").GetString());
        Assert.Equal("10.0.0", manifest.RootElement.GetProperty("runtime").GetString());
        // 7 managed + 2 native + 1 dotnet file, each with a real sha-256 hash.
        var files = manifest.RootElement.GetProperty("files");
        Assert.Equal(10, files.EnumerateObject().Count());
        Assert.Equal(64, files.GetProperty("Nami.Runtime.dll").GetString()!.Length);
    }

    [Fact]
    public void Pack_MissingArtifacts_Throws()
    {
        var empty = Path.Combine(_baseDir, "empty-artifacts");
        var ex = Assert.Throws<InvalidOperationException>(() => Stager.Pack(_repo, ArtifactPath, empty));
        Assert.Contains("cannot pack", ex.Message);
    }

    [Fact]
    public void InstallFromArtifact_CreatesFullRoot()
    {
        Stager.Pack(_repo, ArtifactPath, _artifacts);
        var staged = Stager.InstallFromArtifact(_gameDir, ArtifactPath);

        Assert.Equal(Path.Combine(_gameDir, "nami"), staged.Root);
        Assert.Equal("1.0.0", staged.Version);
        foreach (var f in Stager.ManagedFiles)
        {
            var dst = Path.Combine(staged.Root, f);
            Assert.True(File.Exists(dst), $"{f} should be installed");
            Assert.Equal(f, File.ReadAllText(dst)); // content matches the source file
        }

        Assert.Equal("nami_loader.dll", File.ReadAllText(Path.Combine(staged.Root, "native", "nami_loader.dll")));
        Assert.Equal("hostfxr.dll", File.ReadAllText(Path.Combine(staged.Root, "dotnet", "host", "fxr", "10.0.0", "hostfxr.dll")));
        Assert.True(Directory.Exists(Path.Combine(staged.Root, "mods")));
        Assert.True(File.Exists(Path.Combine(staged.Root, "nami.json")));
    }

    [Fact]
    public void InstallFromArtifact_Upgrade_ReplacesFramework_KeepsUserContent()
    {
        Stager.Pack(_repo, ArtifactPath, _artifacts);

        // Simulate an existing install with user content and a stale framework file.
        var root = Path.Combine(_gameDir, "nami");
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        File.WriteAllText(Path.Combine(root, "mods", "keep.dll"), "user mod");
        File.WriteAllText(Path.Combine(root, "nami.log"), "old log");
        File.WriteAllText(Path.Combine(root, "Nami.Tide.dll"), "STALE");
        File.WriteAllText(Path.Combine(root, "safe-mode"), "1");

        Stager.InstallFromArtifact(_gameDir, ArtifactPath);

        Assert.Equal("user mod", File.ReadAllText(Path.Combine(root, "mods", "keep.dll")));
        Assert.Equal("old log", File.ReadAllText(Path.Combine(root, "nami.log")));
        Assert.True(File.Exists(Path.Combine(root, "safe-mode")), "boot-guard markers must survive an upgrade");
        Assert.Equal("Nami.Tide.dll", File.ReadAllText(Path.Combine(root, "Nami.Tide.dll")));
    }

    [Fact]
    public void InstallFromArtifact_RemovesObsoleteRootLoader_KeepsUserContent()
    {
        Stager.Pack(_repo, ArtifactPath, _artifacts);
        var root = Path.Combine(_gameDir, "nami");
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        File.WriteAllText(Path.Combine(root, "mods", "keep.dll"), "user mod");
        File.WriteAllText(Path.Combine(root, "nami_loader.dll"), "STALE");

        Stager.InstallFromArtifact(_gameDir, ArtifactPath);

        Assert.False(File.Exists(Path.Combine(root, "nami_loader.dll")), "retired root loader must be removed on upgrade");
        Assert.Equal("user mod", File.ReadAllText(Path.Combine(root, "mods", "keep.dll")));
        Assert.True(File.Exists(Path.Combine(root, "native", "nami_loader.dll")));
    }

    [Fact]
    public void InstallFromArtifact_NotANamiArtifact_Throws()
    {
        var fake = Path.Combine(_baseDir, "not-nami.zip");
        using (var zip = ZipFile.Open(fake, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("hello");
        }

        var ex = Assert.Throws<InvalidOperationException>(() => Stager.InstallFromArtifact(_gameDir, fake));
        Assert.Contains("not a Nami installer artifact", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_gameDir, "nami")), "no root may be created for a non-artifact");
    }

    [Fact]
    public void InstallFromArtifact_TamperedEntry_Throws_AndDoesNotClobber()
    {
        Stager.Pack(_repo, ArtifactPath, _artifacts);
        Stager.InstallFromArtifact(_gameDir, ArtifactPath);
        var root = Path.Combine(_gameDir, "nami");
        Assert.Equal("Nami.Sdk.dll", File.ReadAllText(Path.Combine(root, "Nami.Sdk.dll")));

        // Tamper with one entry inside the artifact.
        using (var zip = ZipFile.Open(ArtifactPath, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Nami.Sdk.dll")!;
            using var stream = entry.Open();
            stream.SetLength(0);
            var bytes = Encoding.UTF8.GetBytes("TAMPERED");
            stream.Write(bytes, 0, bytes.Length);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => Stager.InstallFromArtifact(_gameDir, ArtifactPath));
        Assert.Contains("integrity check failed for 'Nami.Sdk.dll'", ex.Message);

        // The working file from the previous install must be untouched.
        Assert.Equal("Nami.Sdk.dll", File.ReadAllText(Path.Combine(root, "Nami.Sdk.dll")));
    }

    [Fact]
    public void Install_FromFileUri_Works()
    {
        Stager.Pack(_repo, ArtifactPath, _artifacts);
        var uri = new Uri(ArtifactPath).AbsoluteUri; // file:///C:/...
        Assert.StartsWith("file://", uri);

        var staged = Stager.InstallFromArtifact(_gameDir, uri);
        Assert.Equal("1.0.0", staged.Version);
        Assert.True(File.Exists(Path.Combine(staged.Root, "Nami.Runtime.dll")));
    }

    [Fact]
    public void InstallFromArtifact_MissingArtifact_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Stager.InstallFromArtifact(_gameDir, Path.Combine(_baseDir, "nope.zip")));
        Assert.Contains("installer artifact not found", ex.Message);
    }
}