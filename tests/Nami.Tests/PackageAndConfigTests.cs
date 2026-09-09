using System.IO.Compression;
using System.Text.Json;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Core.Plugins;
using Nami.Sdk;

namespace Nami.Tests;

public class PackageAndConfigTests
{
    // ------------------------------------------------------------------ .nmod

    /// <summary>Builds a valid .nmod (zip with mod.json + a plugin dll + a nested file).</summary>
    private static string BuildNmod(string dir, string id, string dllSource)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id}.nmod");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var manifest = zip.CreateEntry("mod.json");
            using (var w = new StreamWriter(manifest.Open()))
            {
                w.Write($$$"""{"id":"{{{id}}}","name":"{{{id}}}","version":"1.2.3","dependencies":["dev.other"]}""");
            }

            using (var dll = zip.CreateEntry("MyMod.dll").Open())
            using (var src = File.OpenRead(dllSource))
            {
                src.CopyTo(dll);
            }

            using (var native = zip.CreateEntry("native/extra.dll").Open())
            using (var src = File.OpenRead(dllSource))
            {
                src.CopyTo(native);
            }
        }

        return path;
    }

    private static string AnyPluginDll()
    {
        // Any managed dll works for structural tests; the test output dir has several.
        return Path.Combine(AppContext.BaseDirectory, "Nami.Sdk.dll");
    }

    [Fact]
    public void Nmod_ReadManifestParsesPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "nami-nmod-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hub = new LogHub();
            var path = BuildNmod(root, "com.example.pkg", AnyPluginDll());

            var manifest = NamiPackage.ReadManifest(path, hub);

            Assert.NotNull(manifest);
            Assert.Equal("com.example.pkg", manifest.Id);
            Assert.Equal("1.2.3", manifest.Version);
            Assert.Equal("com.example.pkg", manifest.Name);
            Assert.Contains("dev.other", manifest.Dependencies);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Nmod_InstallExtractsIntoPerPackageDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "nami-nmod-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hub = new LogHub();
            var path = BuildNmod(root, "com.example.pkg", AnyPluginDll());
            var mods = Path.Combine(root, "mods");

            var target = NamiPackage.Install(path, mods, hub);

            Assert.Equal(Path.Combine(mods, "com.example.pkg"), target);
            Assert.True(File.Exists(Path.Combine(target, "MyMod.dll")));
            Assert.True(File.Exists(Path.Combine(target, "native", "extra.dll")));
            // Non-dll package files (mod.json) survive extraction - future hot-reload scans
            // and re-installs work against a faithful directory copy.
            Assert.True(File.Exists(Path.Combine(target, "mod.json")));

            // Re-install over an existing directory must succeed (idempotent overwrite).
            NamiPackage.Install(path, mods, hub);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Nmod_RejectsZipWithoutManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "nami-nmod-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hub = new LogHub();
            var path = Path.Combine(root, "not-a-mod.nmod");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var dll = zip.CreateEntry("MyMod.dll").Open();
                using var src = File.OpenRead(AnyPluginDll());
                src.CopyTo(dll);
            }

            Assert.Null(NamiPackage.ReadManifest(path, hub));
            Assert.Throws<InvalidOperationException>(() => NamiPackage.Install(path, root, hub));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Nmod_RejectsPathTraversalEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "nami-nmod-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var hub = new LogHub();
            var path = Path.Combine(root, "evil.nmod");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var manifest = zip.CreateEntry("mod.json");
                using (var w = new StreamWriter(manifest.Open()))
                {
                    w.Write("""{"id":"evil.pkg","name":"evil","version":"1.0.0"}""");
                }

                using var dst = zip.CreateEntry("../evil.txt").Open();
                dst.Write(new byte[] { 1, 2, 3 });
            }

            Assert.Throws<InvalidOperationException>(() => NamiPackage.Install(path, root, hub));
            Assert.False(File.Exists(Path.Combine(root, "evil.txt"))); // must not escape
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------- per-plugin config

    [Fact]
    public void PluginConfig_FallsBackOnMissingOrWrongTypedKeys()
    {
        var section = new Dictionary<string, JsonElement>
        {
            ["greeting"] = JsonDocument.Parse("\"hi\"").RootElement,
            ["count"] = JsonDocument.Parse("42").RootElement,
            ["scale"] = JsonDocument.Parse("2.5").RootElement,
            ["enabled"] = JsonDocument.Parse("true").RootElement,
        };
        var config = new JsonPluginConfig(section);

        Assert.Equal("hi", config.GetString("greeting", "x"));
        Assert.Equal("x", config.GetString("missing", "x"));
        Assert.Equal("x", config.GetString("count", "x")); // wrong type
        Assert.Equal(42, config.GetInt("count", 0));
        Assert.Equal(7, config.GetInt("greeting", 7));     // wrong type
        Assert.Equal(2.5, config.GetDouble("scale", 0.0));
        Assert.True(config.GetBool("enabled", false));
        Assert.False(config.GetBool("count", false));
        Assert.True(config.Has("greeting"));
        Assert.False(config.Has("missing"));
    }

    [Fact]
    public void PluginConfig_MissingSectionBehavesAsEmpty()
    {
        Assert.Equal("d", NullPluginConfig.Instance.GetString("any", "d"));
        Assert.Empty(JsonPluginConfig.Empty.Entries);
    }

    [Fact]
    public void Chainloader_HandsEachPluginItsOwnConfigSection()
    {
        var root = Path.Combine(Path.GetTempPath(), "nami-cfg-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new NamiConfig
            {
                RootPath = root,
                PluginConfig = new Dictionary<string, Dictionary<string, JsonElement>>
                {
                    ["dev.nami.fixtures.alpha"] = new()
                    {
                        ["label"] = JsonDocument.Parse("\"alpha-section\"").RootElement,
                        ["n"] = JsonDocument.Parse("5").RootElement,
                    },
                }
            };
            var hub = new LogHub();
            var chainloader = new Chainloader(root, config, hub);
            chainloader.Initialize();

            var alphaDll = Path.Combine(AppContext.BaseDirectory, "AlphaPlugin.dll");
            var sdkDll = Path.Combine(AppContext.BaseDirectory, "Nami.Sdk.dll");
            File.Copy(alphaDll, Path.Combine(root, "mods", "AlphaPlugin.dll"));
            File.Copy(sdkDll, Path.Combine(root, "mods", "Nami.Sdk.dll"));

            chainloader.LoadAll();
            var alpha = chainloader.Plugins.Single(p => p.Manifest.Id == "dev.nami.fixtures.alpha");
            Assert.Equal("alpha-section", alpha.Context.Config.GetString("label", "?"));
            Assert.Equal(5, alpha.Context.Config.GetInt("n", 0));
            chainloader.Shutdown();
            chainloader.Dispose(); // unregister the global TideMetrics sink (static state)
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TideMetrics_RoutesToRegisteredSinkAndUnregisterIsGuarded()
    {
        var recorded = new List<double>();
        var sink = new CaptureTideSink(recorded);

        TideMetrics.Register(null); // clear any sink leaked by another test's Chainloader
        Assert.False(TideMetrics.HasSink);
        TideMetrics.Record(1.0); // no sink: must be a harmless no-op

        TideMetrics.Register(sink);
        Assert.True(TideMetrics.HasSink);
        TideMetrics.Record(2.5);
        TideMetrics.Record(0.25);

        // A second sink cannot unregister the first's registration.
        TideMetrics.Unregister(new CaptureTideSink(recorded));
        Assert.True(TideMetrics.HasSink);

        TideMetrics.Unregister(sink);
        Assert.False(TideMetrics.HasSink);
        TideMetrics.Record(9.0); // unregistered: no-op

        Assert.Equal(new[] { 2.5, 0.25 }, recorded);
    }

    [Fact]
    public void ModProfiler_ReceivesTideOpTimingsViaTheSinkInterface()
    {
        var profiler = new Nami.Core.Profiling.ModProfiler();
        Assert.IsAssignableFrom<ITideOpSink>(profiler);

        ((ITideOpSink)profiler).RecordTideOp(2.0);
        ((ITideOpSink)profiler).RecordTideOp(6.0);

        Assert.Equal(2, profiler.TideOpCount);
        Assert.Equal(4.0, profiler.TideOpAvgMs, 5);
        Assert.Contains("tide ops=2", profiler.ToSummaryLine());
    }

    private sealed class CaptureTideSink(List<double> into) : ITideOpSink
    {
        public void RecordTideOp(double milliseconds)
        {
            lock (into)
            {
                into.Add(milliseconds);
            }
        }
    }
}
