using System.Diagnostics;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Bench;

/// <summary>
/// Headline benchmark: time the managed chainloader (discovery + load + update sweep)
/// over N synthetic plugins and report per-1000-plugin cost. Doubles as a regression
/// gate (env-overridable budgets, generous defaults): nonzero exit on breach.
/// The in-game comparative harness against BepInEx/MelonLoader on real Unity fixtures
/// is a documented manual protocol (see docs/plan.md M5) — neither loader runs in CI.
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
        var msPerPlugin = loadMs / Math.Max(1, loaded);
        var msPerFrame = updateMs / UpdateFrames;
        Console.WriteLine($"plugins loaded       : {loaded}");
        Console.WriteLine($"load time            : {loadMs,8:F1} ms  ({msPerPlugin,6:F2} ms/plugin)");
        Console.WriteLine($"update sweep x{UpdateFrames}    : {updateMs,8:F1} ms  ({msPerFrame,6:F3} ms/frame @ {loaded} plugins)");
        Console.WriteLine($"working set          : {rss,8:F0} MB");

        // Regression gates: generous absolute budgets (slow/loaded machines must still
        // pass); override via environment for tight per-machine tracking.
        static double Gate(string name, double def) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : def;
        var loadBudget = Gate("NAMI_GATE_LOAD_MS_PER_PLUGIN", 100);
        var frameBudget = Gate("NAMI_GATE_UPDATE_MS_PER_FRAME", 0.5);
        var failures = new List<string>();
        void Check(bool ok, string message)
        {
            Console.WriteLine($"{(ok ? "GATE PASS" : "GATE FAIL")} {message}");
            if (!ok)
            {
                failures.Add(message);
            }
        }

        Check(msPerPlugin <= loadBudget, $"load {msPerPlugin:F2}ms/plugin <= {loadBudget}ms");
        Check(msPerFrame <= frameBudget, $"update {msPerFrame:F3}ms/frame <= {frameBudget}ms");

        foreach (var f in failures)
        {
            Console.Error.WriteLine($"bench gate failed: {f}");
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best-effort
        }

        return failures.Count == 0 ? 0 : 1;
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
