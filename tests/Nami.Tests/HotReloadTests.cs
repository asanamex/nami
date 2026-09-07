using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Tests;

public class HotReloadTests
{
    private static void CopyPlugins(GameDirFixture fixture, params string[] pluginDlls)
    {
        Directory.CreateDirectory(fixture.ModsDir);
        var outputDir = AppContext.BaseDirectory;
        foreach (var pluginDll in pluginDlls)
        {
            File.Copy(Path.Combine(outputDir, pluginDll), Path.Combine(fixture.ModsDir, pluginDll));
        }

        // Plugin resolution needs the SDK next to the plugin assemblies.
        File.Copy(Path.Combine(outputDir, "Nami.Sdk.dll"), Path.Combine(fixture.ModsDir, "Nami.Sdk.dll"));
    }

    private static Chainloader Create(GameDirFixture fixture, NamiConfig? config = null, LogHub? hub = null)
    {
        hub ??= new LogHub { MinimumLevel = LogLevel.Debug };
        var chainloader = new Chainloader(fixture.Root, config ?? NamiConfig.Load(fixture.Root), hub);
        return chainloader;
    }

    [Fact]
    public void Reload_ReplacesPluginAndDependentsInNewGenerations()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll", "BadPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();

        var alphaBefore = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
        var genBefore = alphaBefore.Generation;

        // alpha has no dependents besides beta, but gamma depends on beta — the fixpoint
        // reload set is alpha + beta + gamma (reverse-load-order unload, deps-first reload).
        var result = chainloader.Reload("dev.nami.fixtures.alpha");

        Assert.True(result.AnyChanges);
        Assert.Empty(result.Failed);
        Assert.Empty(result.Unloaded);
        Assert.Equal(new[] { "dev.nami.fixtures.alpha", "dev.nami.fixtures.beta", "dev.nami.fixtures.gamma" }, result.Reloaded);

        var alphaAfter = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
        var betaAfter = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.beta");
        Assert.True(alphaAfter.Generation > genBefore);
        Assert.Equal(1, alphaAfter.ReloadCount);
        Assert.Equal(1, betaAfter.ReloadCount);
        Assert.Equal(1, chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.gamma").ReloadCount);
        Assert.NotSame(alphaBefore.Instance, alphaAfter.Instance);
        Assert.NotSame(alphaBefore.LoadContext, alphaAfter.LoadContext);
        Assert.All(chainloader.Plugins, p => Assert.Equal(PluginState.Active, p.State));
        chainloader.Shutdown();
    }

    [Fact]
    public void RequestReload_QueuesUntilNextUpdateTick()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll", "GammaPlugin.dll", "BadPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();

        var genBefore = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha").Generation;

        Assert.True(chainloader.RequestReload("dev.nami.fixtures.alpha"));
        Assert.False(chainloader.RequestReload("dev.nami.fixtures.unknown")); // not loaded → not queued

        // Not applied synchronously — the command drains on the next tick.
        Assert.Equal(genBefore, chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha").Generation);
        chainloader.UpdateAll();

        var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
        Assert.True(alpha.Generation > genBefore);
        Assert.Equal(PluginState.Active, alpha.State);
        chainloader.Shutdown();
    }

    [Fact]
    public void Reload_NewPluginDropIsLoadedFresh()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        // Gamma is NOT in mods initially.
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        Assert.DoesNotContain(chainloader.Plugins, p => p.Manifest.Id == "dev.nami.fixtures.gamma");

        // Simulate dropping a brand-new mod dll while the game runs (as the watcher would see),
        // then reload by its id — it is not loaded yet, so it must be loaded fresh.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "GammaPlugin.dll"),
            Path.Combine(fixture.ModsDir, "GammaPlugin.dll"));

        var result = chainloader.Reload("dev.nami.fixtures.gamma");

        Assert.Empty(result.Failed);
        Assert.Equal(new[] { "dev.nami.fixtures.gamma" }, result.Reloaded);
        var gamma = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.gamma");
        Assert.Equal(PluginState.Active, gamma.State);
        chainloader.Shutdown();
    }

    [Fact]
    public void Reload_UnloadedModFileIsRemoved()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll");

        var chainloader = Create(fixture);
        chainloader.LoadAll();
        File.Delete(Path.Combine(fixture.ModsDir, "BetaPlugin.dll"));

        var result = chainloader.Reload("dev.nami.fixtures.beta");

        Assert.Empty(result.Reloaded);
        Assert.Empty(result.Failed);
        Assert.Equal(new[] { "dev.nami.fixtures.beta" }, result.Unloaded);
        Assert.DoesNotContain(chainloader.Plugins, p => p.Manifest.Id == "dev.nami.fixtures.beta");
        // Alpha (no dependency on beta) is untouched and still active.
        Assert.Equal(PluginState.Active, chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha").State);
        chainloader.Shutdown();
    }

    [Fact]
    public void Watcher_AutoLoadsDroppedPluginAfterDebounce()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll");

        var config = new NamiConfig { RootPath = fixture.Root };
        config.HotReload.DebounceMs = 150;
        var chainloader = Create(fixture, config);
        chainloader.LoadAll();
        chainloader.StartHotReload();

        // Drop a new mod while the watcher is live; the debounce window collapses the FS burst.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "GammaPlugin.dll"),
            Path.Combine(fixture.ModsDir, "GammaPlugin.dll"));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               !chainloader.Plugins.Any(p => p.Manifest.Id == "dev.nami.fixtures.gamma"))
        {
            chainloader.UpdateAll();
            Thread.Sleep(50);
        }

        var gamma = chainloader.Plugins.FirstOrDefault(p => p.Manifest.Id == "dev.nami.fixtures.gamma");
        Assert.NotNull(gamma);
        Assert.Equal(PluginState.Active, gamma.State);
        chainloader.Shutdown();
    }

    [Fact]
    public void Watcher_AutoReloadsChangedModFile()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll", "BetaPlugin.dll");

        var config = new NamiConfig { RootPath = fixture.Root };
        config.HotReload.DebounceMs = 150;
        var chainloader = Create(fixture, config);
        chainloader.LoadAll();
        chainloader.StartHotReload();

        var genBefore = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha").Generation;

        Thread.Sleep(300); // ensure the write is unambiguously newer than LoadedAtUtc + 100ms slack
        File.SetLastWriteTimeUtc(Path.Combine(fixture.ModsDir, "AlphaPlugin.dll"), DateTime.UtcNow);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            chainloader.UpdateAll();
            var alpha = chainloader.Plugins.FirstOrDefault(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
            if (alpha is not null && alpha.Generation != genBefore)
            {
                break;
            }

            Thread.Sleep(50);
        }

        var reloaded = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
        Assert.True(reloaded.Generation > genBefore, "watcher did not reload the touched mod within the timeout");
        Assert.Equal(PluginState.Active, reloaded.State);
        chainloader.Shutdown();
    }

    [Fact]
    public void StartHotReload_RespectsConfigSwitches()
    {
        using var fixture = new GameDirFixture();
        fixture.WriteConfig();
        CopyPlugins(fixture, "AlphaPlugin.dll");

        var config = new NamiConfig { RootPath = fixture.Root };
        config.HotReload.Enabled = false;
        var chainloader = Create(fixture, config);
        chainloader.LoadAll();
        chainloader.StartHotReload(); // no-op: hot reload disabled

        // Nothing threw, and a manual reload still works with the watcher off.
        var result = chainloader.Reload("dev.nami.fixtures.alpha");
        Assert.Single(result.Reloaded);
        chainloader.Shutdown();
        chainloader.Dispose(); // Dispose is idempotent and stops the watcher
    }
}
