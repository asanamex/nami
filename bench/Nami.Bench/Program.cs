using System.Diagnostics;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Bench;

/// <summary>
/// Headline benchmark: time the managed chainloader (discovery + load + update sweep)
/// over N synthetic plugins and report per-1000-plugin cost. The comparative harness
/// against BepInEx/MelonLoader on real Unity fixtures lands in M5.
/// </summary>
internal static class Program
{
    private const int PluginCount = 100;
    private const int UpdateFrames = 1000;

    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "nami-bench", Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);

        // Synthesize N minimal plugin assemblies ahead of the timed section.
        Console.WriteLine($"synthesizing {PluginCount} plugin assemblies...");
        SynthesizePlugins(mods, PluginCount);

        var hub = new LogHub { MinimumLevel = LogLevel.Fatal };
        var chainloader = new Chainloader(root, new NamiConfig { RootPath = root }, hub);

        var sw = Stopwatch.StartNew();
        chainloader.LoadAll();
        sw.Stop();
        var loadMs = sw.Elapsed.TotalMilliseconds;

        var updateSw = Stopwatch.StartNew();
        for (var i = 0; i < UpdateFrames; i++)
        {
            chainloader.UpdateAll();
        }

        updateSw.Stop();
        var updateMs = updateSw.Elapsed.TotalMilliseconds;
        var rss = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);

        chainloader.Shutdown();

        var loaded = chainloader.Plugins.Count;
        Console.WriteLine($"plugins loaded       : {loaded}");
        Console.WriteLine($"load time            : {loadMs,8:F1} ms  ({loadMs / Math.Max(1, loaded),6:F2} ms/plugin)");
        Console.WriteLine($"update sweep x{UpdateFrames}    : {updateMs,8:F1} ms  ({updateMs / UpdateFrames,6:F3} ms/frame @ {loaded} plugins)");
        Console.WriteLine($"working set          : {rss,8:F0} MB");

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best-effort
        }

        return 0;
    }

    private static void SynthesizePlugins(string modsDir, int count)
    {
        var sdkPath = typeof(NamiPlugin).Assembly.Location;
        var sharedRefs = new[] { typeof(object).Assembly.Location, sdkPath };
        var baseDir = Path.Combine(Path.GetTempPath(), "nami-bench", "synth");

        for (var i = 0; i < count; i++)
        {
            var id = $"bench.plugin.{i:D4}";
            var src = $$"""
                using Nami.Sdk;

                [NamiPlugin]
                [PluginInfo("{{id}}", "Bench Plugin {{i}}", "1.0.0")]
                public sealed class BenchPlugin{{i}} : NamiPlugin
                {
                    public override void OnUpdate() { }
                }
                """;
            var dir = Path.Combine(baseDir, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Plugin.cs"), src);

            var proj = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>BenchPlugin{i}</AssemblyName>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Nami.Sdk"><HintPath>{sdkPath}</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(dir, "Plugin.csproj"), proj);
        }

        // Build all synthesized projects in parallel.
        Parallel.For(0, count, i =>
        {
            var dir = Path.Combine(baseDir, $"bench.plugin.{i:D4}");
            var psi = new ProcessStartInfo("dotnet", "build -c Release -v q --nologo")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"synthesized plugin {i} failed to build");
            }
        });

        for (var i = 0; i < count; i++)
        {
            var dll = Path.Combine(baseDir, $"bench.plugin.{i:D4}", "bin", "Release", "net10.0", $"BenchPlugin{i}.dll");
            File.Copy(dll, Path.Combine(modsDir, $"BenchPlugin{i}.dll"));
        }
    }
}
