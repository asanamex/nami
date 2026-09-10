using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Generations;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Tests;

public class ChainloaderTests
{
    private static void CopyPlugins(GameDirFixture fixture)
    {
        var targetDir = fixture.ModsDir;
        Directory.CreateDirectory(targetDir);

        // Copy each fixture plugin assembly (plus the SDK it references) from the test output dir,
        // which receives them transitively via the fixture project references.
        var outputDir = AppContext.BaseDirectory;
        foreach (var pluginDll in new[] { "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll", "BadPlugin.dll" })
        {
            File.Copy(Path.Combine(outputDir, pluginDll), Path.Combine(targetDir, pluginDll));
        }

        // Each plugin dir also needs Nami.Sdk.dll for resolution from the plugin's own directory.
        var sdkDll = Path.Combine(outputDir, "Nami.Sdk.dll");
        File.Copy(sdkDll, Path.Combine(targetDir, "Nami.Sdk.dll"));
    }

    private static (LogHub Hub, Chainloader Loader) Create(GameDirFixture fixture)
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        var chainloader = new Chainloader(fixture.Root, NamiConfig.Load(fixture.Root), hub);
        return (hub, chainloader);
    }

    [Fact]
    public void LoadAll_LoadsFixturesInDependencyOrder()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture);

        var (_, chainloader) = Create(fixture);
        var loaded = chainloader.LoadAll();

        var ids = chainloader.Plugins.Select(p => p.Manifest.Id).ToArray();
        Assert.Contains("dev.nami.fixtures.alpha", ids);
        Assert.Contains("dev.nami.fixtures.beta", ids);
        Assert.Contains("dev.nami.fixtures.gamma", ids);

        // Order: alpha (no deps) before beta (deps alpha) before gamma (deps beta).
        var alpha = Array.IndexOf(ids, "dev.nami.fixtures.alpha");
        var beta = Array.IndexOf(ids, "dev.nami.fixtures.beta");
        var gamma = Array.IndexOf(ids, "dev.nami.fixtures.gamma");
        Assert.True(alpha < beta && beta < gamma);

        Assert.All(chainloader.Plugins, p => Assert.Equal(LifetimeState.Running, p.Lifetime));
        chainloader.Shutdown();
    }

    [Fact]
    public void Plugin_IsLoadedInItsOwnAssemblyLoadContext()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture);

        var (_, chainloader) = Create(fixture);
        chainloader.LoadAll();

        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
        var beta = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.beta");

        Assert.NotSame(alpha.Instance.GetType().Assembly, beta.Instance.GetType().Assembly);
        // Assemblies are byte-loaded (hot reload keeps mod files unlocked), so Location is
        // empty; isolation is proven by each plugin living in a distinct ALC.
        Assert.NotSame(alpha.LoadContext, beta.LoadContext);
        chainloader.Shutdown();
    }

    [Fact]
    public void BadPlugin_IsQuarantinedAndGameKeepsRunning()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig(quarantineEnabled: true, threshold: 3);
        CopyPlugins(fixture);

        var (_, chainloader) = Create(fixture);
        chainloader.LoadAll();

        var bad = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.bad");
        var quarantined = 0;
        chainloader.PluginQuarantined += _ => quarantined++;

        // Bad throws every update; drive enough frames to trip the threshold.
        for (var i = 0; i < 6; i++)
        {
            chainloader.UpdateAll();
        }

        Assert.Equal(LifetimeState.Quarantined, bad.Lifetime);
        Assert.NotNull(bad.QuarantinedAt);
        Assert.True(bad.ConsecutiveFailures >= 3);
        Assert.Equal(1, quarantined);

        // Other plugins still run after the bad one is disabled.
        chainloader.UpdateAll();
        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
        Assert.Equal(LifetimeState.Running, alpha.Lifetime);
        chainloader.Shutdown();
    }

    [Fact]
    public void Chainloader_BootstrapsWithNoConfigFile()
    {
        using var fixture = new GameDirFixture();
        // No nami.json written: must still work with defaults.
        CopyPlugins(fixture);

        var (_, chainloader) = Create(fixture);
        var loaded = chainloader.LoadAll();
        Assert.NotEmpty(loaded);
        chainloader.Shutdown();
    }

    [Fact]
    public void Discovery_IgnoresNonPluginAssemblies()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        Directory.CreateDirectory(fixture.ModsDir);
        File.WriteAllText(Path.Combine(fixture.ModsDir, "NotAPlugin.dll"), "garbage");

        var (_, chainloader) = Create(fixture);
        var manifests = chainloader.DiscoverPlugins();
        Assert.Empty(manifests);
    }

    [Fact]
    public void BadPlugin_StaysActiveWhenQuarantineDisabled()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig(quarantineEnabled: false, threshold: 3);
        CopyPlugins(fixture);

        var (_, chainloader) = Create(fixture);
        chainloader.LoadAll();

        var bad = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.bad");
        var quarantined = 0;
        chainloader.PluginQuarantined += _ => quarantined++;

        for (var i = 0; i < 6; i++)
        {
            chainloader.UpdateAll();
        }

        Assert.NotEqual(LifetimeState.Quarantined, bad.Lifetime);
        Assert.True(bad.ConsecutiveFailures >= 3);
        Assert.Equal(0, quarantined);
        chainloader.Shutdown();
    }

    [Fact]
    public void EnabledPlugins_SupportsGlobPatterns()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig(enabledPlugins: new List<string> { "dev.nami.fixtures.?????", "nomatch-*" });
        CopyPlugins(fixture);

        var (_, chainloader) = Create(fixture);
        chainloader.LoadAll();

        // ???? matches exactly 5 chars: alpha loads, bad (3 chars) does not.
        // (Gamma also matches but needs beta, which the glob excludes.)
        var ids = chainloader.Plugins.Select(p => p.Manifest.Id).ToArray();
        Assert.Contains("dev.nami.fixtures.alpha", ids);
        Assert.DoesNotContain("dev.nami.fixtures.bad", ids);
        chainloader.Shutdown();
    }

    [Fact]
    public void EnabledPlugins_ExactIdStillWorks()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig(enabledPlugins: new List<string> { "dev.nami.fixtures.alpha" });
        CopyPlugins(fixture);

        var (_, chainloader) = Create(fixture);
        chainloader.LoadAll();

        Assert.Single(chainloader.Plugins);
        Assert.Equal("dev.nami.fixtures.alpha", chainloader.Plugins[0].Manifest.Id);
        chainloader.Shutdown();
    }
}
