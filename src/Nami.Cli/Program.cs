using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        // nami [command] [gameDir]
        var command = args.Length > 0 ? args[0] : "help";
        var gameDir = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Directory.GetCurrentDirectory();

        try
        {
            return command.ToLowerInvariant() switch
            {
                "version" => Version(),
                "doctor" => Doctor(gameDir),
                "list" => List(gameDir),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nami: error: {ex.Message}");
            return 1;
        }
    }

    private static int Version()
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Console.WriteLine($"Nami {version} (mod loader for Unity games)");
        return 0;
    }

    private static int Doctor(string gameDir)
    {
        var root = NamiPaths.FindRoot(gameDir);
        if (root is null)
        {
            Console.WriteLine($"no Nami install found in '{gameDir}' (expected a {NamiConfig.FileName} or a 'nami' directory)");
            return 1;
        }

        Console.WriteLine($"Nami root: {root}");
        var config = NamiConfig.Load(root);
        Console.WriteLine($"mods dir : {Path.Combine(root, "mods")}");
        Console.WriteLine($"quarantine: {(config.QuarantineEnabled ? "enabled" : "disabled")} (threshold {config.QuarantineThreshold})");

        var modsDir = Path.Combine(root, "mods");
        var count = Directory.Exists(modsDir) ? Directory.EnumerateFiles(modsDir, "*.dll").Count() : 0;
        Console.WriteLine($"plugins  : {count} found");
        return 0;
    }

    private static int List(string gameDir)
    {
        var root = NamiPaths.FindRoot(gameDir);
        if (root is null)
        {
            Console.WriteLine($"no Nami install found in '{gameDir}'");
            return 1;
        }

        var hub = new LogHub { MinimumLevel = LogLevel.Warn };
        var chainloader = new Chainloader(root, NamiConfig.Load(root), hub);
        chainloader.Initialize();

        var manifests = chainloader.DiscoverPlugins();
        if (manifests.Count == 0)
        {
            Console.WriteLine("no plugins found");
            return 0;
        }

        Console.WriteLine($"{"ID",-40} {"VERSION",-12} {"NAME",-20}");
        foreach (var m in manifests.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{m.Id,-40} {m.Version,-12} {m.Name,-20}");
            if (m.Dependencies.Count > 0)
            {
                Console.WriteLine($"    depends on: {string.Join(", ", m.Dependencies)}");
            }
        }

        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            nami - a fast, isolated Unity mod loader

            usage: nami <command> [gameDir]

            commands:
              version              print the Nami version
              doctor [gameDir]     check a Nami install and report the environment
              list   [gameDir]     list installed plugins and their state
              help                 show this help
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"nami: unknown command '{command}'");
        return Help();
    }
}

/// <summary>Path resolution shared by the CLI and the runtime.</summary>
public static class NamiPaths
{
    /// <summary>Finds the Nami root for a game directory: a directory containing nami.json, or a nami/ subdirectory.</summary>
    public static string? FindRoot(string gameDir)
    {
        if (File.Exists(Path.Combine(gameDir, NamiConfig.FileName)))
        {
            return gameDir;
        }

        var sub = Path.Combine(gameDir, "nami");
        return Directory.Exists(sub) ? sub : null;
    }
}
