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
                "pack" => Pack(rest),
                "launch" => Launch(rest),
                "create" => Create(rest),
                "run" => Run(rest),
                "doctor" => Doctor(rest),
                "list" => List(rest),
                "interop" => Interop(rest),
                "inex" => Inex(rest),
                "nmod" => Nmod(rest),
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
        // nami install [gameDir] [--from <artifact.zip|url>]
        string? artifact = null;
        var rest = args.ToList();
        for (var i = 0; i < rest.Count - 1; i++)
        {
            if (rest[i] == "--from")
            {
                artifact = rest[i + 1];
                rest.RemoveRange(i, 2);
                break;
            }
        }

        var gameDir = ParseGameDir(rest.ToArray(), out _);
        return InstallCommand.Run(gameDir, artifact);
    }

    private static int Pack(string[] args)
    {
        // nami pack [out.zip] [--artifacts <root>]
        string? artifactsRoot = null;
        var rest = args.ToList();
        for (var i = 0; i < rest.Count - 1; i++)
        {
            if (rest[i] == "--artifacts")
            {
                artifactsRoot = rest[i + 1];
                rest.RemoveRange(i, 2);
                break;
            }
        }

        var outPath = rest.Count > 0 ? rest[0] : null;
        return PackCommand.Run(outPath, artifactsRoot);
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

    private static int Run(string[] args)
    {
        // nami run <mod.csproj> [gameDir]  (gameDir must be an existing directory)
        var gameDir = Directory.GetCurrentDirectory();
        var positional = args;
        if (args.Length > 1 && Directory.Exists(args[^1]))
        {
            gameDir = Path.GetFullPath(args[^1]);
            positional = args[..^1];
        }

        return RunCommand.Run(gameDir, positional);
    }

    /// <summary>
    /// nami interop - offline IL2CPP typed-projection tooling (dev-time; v24-38 metadata,
    /// single-byte XOR de-obfuscation transparent). Reads <c>global-metadata.dat</c>
    /// directly; never touches a running game.
    /// </summary>
    private static int Interop(string[] args)
    {
        // nami interop images [gameDir]
        // nami interop dump [imageName] [gameDir]
        // nami interop generate <imageName> [out.cs] [gameDir]
        // nami interop header [gameDir]
        var sub = args.FirstOrDefault()?.ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        var gameDir = Directory.GetCurrentDirectory();
        var positional = rest;
        if (rest.Length > 0 && Directory.Exists(rest[^1]))
        {
            gameDir = Path.GetFullPath(rest[^1]);
            positional = rest[..^1];
        }

        var metadataPath = FindMetadata(gameDir);
        if (metadataPath is null)
        {
            Console.Error.WriteLine($"nami interop: no global-metadata.dat found under '{gameDir}' (is this an IL2CPP game?)");
            return 1;
        }

        try
        {
            if (sub == "header")
            {
                // Diagnostic: dump the raw (offset,size) pairs (which regions exist and how
                // large they are). Never parses structs, so it works on unsupported versions.
                var version = Nami.Interop.Il2CppMetadata.ReadVersion(metadataPath);
                var pairs = Nami.Interop.Il2CppMetadata.DumpHeaderPairs(metadataPath);
                Console.WriteLine($"metadata: {metadataPath} (version {version}, {pairs.Count} pairs)");
                for (var i = 0; i < pairs.Count; i++)
                {
                    Console.WriteLine($"  pair {i + 1,2}: offset={pairs[i].Offset,10} size={pairs[i].Size,9}");
                }

                return 0;
            }

            var metadata = Nami.Interop.Il2CppMetadata.Load(metadataPath);
            Console.WriteLine($"metadata: {metadataPath} (version {metadata.MetadataVersion})");

            switch (sub)
            {
                case "images":
                {
                    foreach (var image in metadata.Images())
                    {
                        Console.WriteLine($"  {image.Name}");
                    }

                    return 0;
                }

                case "dump":
                {
                    var imageName = positional.FirstOrDefault() ?? "Assembly-CSharp.dll";
                    var types = metadata.TypesInImage(imageName);
                    Console.WriteLine($"{imageName}: {types.Count} top-level type(s)");
                    foreach (var type in types.OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  {type.FullName}");
                        foreach (var method in metadata.MethodsOf(type).OrderBy(m => m.Name, StringComparer.Ordinal))
                        {
                            Console.WriteLine($"      {(method.IsStatic ? "static " : "")}{method.Name}({method.ParameterCount})");
                        }
                    }

                    return 0;
                }

                case "generate":
                {
                    if (positional.Length == 0)
                    {
                        Console.Error.WriteLine("usage: nami interop generate <imageName> [out.cs] [gameDir]");
                        return 1;
                    }

                    var imageName = positional[0];
                    var outPath = positional.Length > 1 ? positional[1] : Path.Combine(Directory.GetCurrentDirectory(), "GameInterop.g.cs");
                    var source = Nami.Interop.ProjectionWriter.Generate(metadata, imageName);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                    File.WriteAllText(outPath, source);
                    Console.WriteLine($"wrote {outPath} ({metadata.TypesInImage(imageName).Count} top-level type(s) from {imageName})");
                    return 0;
                }

                default:
                    Console.WriteLine("""
                        usage: nami interop <command> [args...] [gameDir]

                          images                     list the metadata's images (assemblies)
                          dump [imageName]           print an image's public type/method surface
                          generate <imageName> [out.cs]
                                                     emit a compile-time-typed projection source file
                          header                     dump raw header (offset,size) pairs for diagnostics
                        """);
                    return sub is null ? 0 : 1;
            }
        }
        catch (Nami.Interop.MetadataFormatException ex)
        {
            Console.Error.WriteLine($"nami interop: {ex.Message}");
            Console.Error.WriteLine("hint: run `nami interop header [gameDir]` to dump the raw layout for this file");
            return 1;
        }
    }

    /// <summary>
    /// Locates global-metadata.dat, preferring the canonical IL2CPP output path so multi-game
    /// directories resolve deterministically instead of depending on enumeration order.
    /// </summary>
    private static string? FindMetadata(string gameDir)
    {
        var candidates = Directory.EnumerateFiles(gameDir, "global-metadata.dat", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var canonical = candidates.FirstOrDefault(p =>
            p.Replace('\\', '/').Contains("/il2cpp_data/metadata/", StringComparison.OrdinalIgnoreCase));
        var picked = canonical ?? candidates[0];
        Console.Error.WriteLine(
            $"nami interop: {candidates.Count} metadata files found; using '{picked}'" +
            (canonical is null ? " (no canonical il2cpp_data/Metadata path among them)" : string.Empty));
        return picked;
    }

    /// <summary>
    /// nami inex - legacy lane (nami-inex): stage/enable/disable a real BepInEx 5.x runtime
    /// that Nami boots inside the game's own Mono (no Doorstop proxy).
    /// </summary>
    private static int Inex(string[] args)
    {
        // nami inex <sub> [subArgs...] [gameDir]  (gameDir must be an existing directory)
        var gameDir = Directory.GetCurrentDirectory();
        var rest = args;
        if (args.Length > 1 && Directory.Exists(args[^1]))
        {
            gameDir = Path.GetFullPath(args[^1]);
            rest = args[..^1];
        }

        return InexCommand.Run(gameDir, rest);
    }

    /// <summary>
    /// nami nmod info|install - .nmod package distribution format on top of loose DLLs
    /// (see NamiPackage). Trailing [gameDir] must be an existing directory.
    /// </summary>
    private static int Nmod(string[] args)
    {
        // nami nmod <sub> [subArgs...] [gameDir]
        var gameDir = Directory.GetCurrentDirectory();
        var rest = args;
        if (args.Length > 1 && Directory.Exists(args[^1]))
        {
            gameDir = Path.GetFullPath(args[^1]);
            rest = args[..^1];
        }

        return NmodCommand.Run(gameDir, rest);
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

        var inexPayload = File.Exists(InexCommand.PreloaderPath(root));
        var inexEnabled = File.Exists(InexCommand.SentinelPath(root));
        Console.WriteLine($"inex     : {(inexPayload ? "payload staged" : "no payload")}" +
                          (inexPayload ? (inexEnabled ? " (enabled)" : " (disabled)") : ""));

        var safeMode = File.Exists(Path.Combine(root, "safe-mode"));
        var bootPending = File.Exists(Path.Combine(root, "boot-pending"));
        Console.WriteLine($"bootguard: {(safeMode
            ? "SAFE MODE — previous boot crashed; native stages skipped (auto-clears after clean boots; delete nami/safe-mode to restore now)"
            : "normal")}" +
                          (bootPending ? " (boot-pending — a Nami boot is in progress or the last boot crashed mid-boot)" : ""));
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
                Console.WriteLine($"    depends on: {string.Join(", ", m.Dependencies.Select(d => d.MinimumVersion is null ? d.Id : $"{d.Id}>={d.MinimumVersion}"))}");
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
              install  [gameDir] [--from <artifact.zip|url>]
                                      install a Nami root next to a game (from the build
                                      outputs, or from a self-contained installer artifact
                                      produced by `nami pack`)
              pack     [out.zip] [--artifacts <root>]
                                      build the self-contained installer artifact (managed +
                                      native + bundled .NET runtime, hash-verified manifest)
              launch   set <game.exe> [--steam-id <appid>] [--force] [gameDir]
                                      remember which executable is the game
              launch   [offline|steam] [gameDir]
                                      run the game with Nami injected (default: offline;
                                      steam relays to a clean Steam session after exit)
              create   [offline|steam] [gameDir]
                                      write launchNami.exe + run-with-nami.bat into the nami root
              run      <mod.csproj> [gameDir]
                                      build a mod, stage it into nami/mods, and launch the game
              doctor   [gameDir]      check a Nami install and report the environment
              list     [gameDir]      list installed plugins and their state
              interop  images|dump|generate|header [args...] [gameDir]
                                      offline IL2CPP typed-projection tooling (dev-time)
              inex     install|enable|disable|status [args...] [gameDir]
                                      legacy BepInEx lane (boots BepInEx 5.x in game Mono)
              nmod     info|install [args...] [gameDir]
                                      .nmod package distribution (manifest info / install)
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
