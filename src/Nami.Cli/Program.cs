using Nami.Cli.Commands;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        // nami <command> [args...] [gameDir]
        var command = args.Length > 0 ? args[0] : "help";
        var rest = args.Skip(1).ToArray();

        try
        {
            return command.ToLowerInvariant() switch
            {
                "version" => Version(),
                "install" => Install(rest),
                "launch" => Launch(rest),
                "create" => Create(rest),
                "doctor" => Doctor(rest),
                "list" => List(rest),
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

    private static int Install(string[] args)
    {
        var gameDir = ParseGameDir(args, out var positional);
        return InstallCommand.Run(gameDir);
    }

    private static int Launch(string[] args)
    {
        // nami launch set <game.exe> [--steam-id <id>] [--force] [gameDir]
        if (args.Length > 0 && args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            var setArgs = args.Skip(1).ToArray();
            var gameDir = ParseGameDir(setArgs, out var positional);
            return LaunchCommand.Set(gameDir, positional);
        }

        // nami launch [offline|steam] [gameDir]
        var mode = args.Length > 0 && args[0] is "offline" or "steam" ? args[0] : null;
        var launchGameDir = mode is null ? ParseGameDir(args, out _) : ParseGameDir(args.Skip(1).ToArray(), out _);
        return LaunchCommand.Run(launchGameDir, mode);
    }

    private static int Create(string[] args)
    {
        // nami create [offline|steam] [gameDir]
        var mode = args.Length > 0 && args[0] is "offline" or "steam" ? args[0] : null;
        var gameDir = mode is null ? ParseGameDir(args, out _) : ParseGameDir(args.Skip(1).ToArray(), out _);
        return CreateCommand.Run(gameDir, mode);
    }

    private static int Doctor(string[] args)
    {
        var gameDir = ParseGameDir(args, out _);
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

        var gameDirFull = Path.GetDirectoryName(root) ?? gameDir;
        if (!string.IsNullOrEmpty(config.GameExe))
        {
            Console.WriteLine($"game exe : {config.GameExe}{(config.SteamAppId is not null ? $" (steam id {config.SteamAppId})" : "")}");
        }
        else
        {
            var detected = GameLocator.AutoDetect(gameDirFull);
            Console.WriteLine($"game exe : {(detected is not null ? $"auto-detect -> {detected}" : "none found — run `nami launch set <game>.exe`")}" +
                              (config.SteamAppId is not null ? $" (steam id {config.SteamAppId})" : ""));
        }

        var missing = Launcher.MissingRootFiles(root);
        Console.WriteLine(missing.Count == 0 ? "launcher  : complete" : $"launcher  : MISSING {string.Join(", ", missing)}");
        return 0;
    }

    private static int List(string[] args)
    {
        var gameDir = ParseGameDir(args, out _);
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

    /// <summary>
    /// Pulls the trailing [gameDir] (if the remaining args contain one positional that is an
    /// existing directory) out of the args and returns the positional args that are left.
    /// </summary>
    private static string ParseGameDir(string[] args, out string[] positional)
    {
        // A trailing existing-directory argument is the gameDir; everything else is positional.
        if (args.Length > 0)
        {
            var last = args[^1];
            if (!last.StartsWith('-') && Directory.Exists(last))
            {
                positional = args[..^1];
                return Path.GetFullPath(last);
            }
        }

        positional = args;
        return Directory.GetCurrentDirectory();
    }

    private static int Help()
    {
        Console.WriteLine("""
            nami - a fast, isolated Unity mod loader

            usage: nami <command> [args...]

            commands:
              version                 print the Nami version
              install  [gameDir]      stage a Nami root next to a game (roadmap stub)
              launch   set <game.exe> [--steam-id <appid>] [--force] [gameDir]
                                      remember which executable is the game
              launch   [offline|steam] [gameDir]
                                      run the game with Nami injected (default: offline;
                                      steam relays to a clean Steam session after exit)
              create   [offline|steam] [gameDir]
                                      write launchNami.exe + run-with-nami.bat into the nami root
              doctor   [gameDir]      check a Nami install and report the environment
              list     [gameDir]      list installed plugins and their state
              help                    show this help
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
