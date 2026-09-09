using System.Text.Json;
using Nami.Core.Logging;
using Nami.Core.Plugins;
using Nami.Sdk;

namespace Nami.Cli.Commands;

/// <summary>
/// `nami nmod info|install` - .nmod package handling (the distribution format on top of
/// loose plugin DLLs; see NamiPackage). `info` reads a package's manifest without
/// extracting; `install` verifies + extracts into the game root's mods/ directory with
/// incompatibility refusal and dependency warnings.
/// </summary>
internal static class NmodCommand
{
    public static int Run(string gameDir, string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant();
        switch (sub)
        {
            case "info":
                return Info(args.Skip(1).ToArray());
            case "install":
                return Install(gameDir, args.Skip(1).ToArray());
            case null or "help":
                Usage();
                return sub is null ? 0 : 1;
            default:
                Console.Error.WriteLine($"nami nmod: unknown subcommand '{sub}'");
                Usage();
                return 1;
        }
    }

    private static int Info(string[] args)
    {
        // nami nmod info <file.nmod>
        var file = args.FirstOrDefault();
        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine($"nami nmod info: file not found: '{file}'");
            Usage();
            return 1;
        }

        var hub = new LogHub { MinimumLevel = LogLevel.Warn };
        var manifest = NamiPackage.ReadManifest(file, hub);
        if (manifest is null)
        {
            Console.Error.WriteLine($"'{file}' is not a valid .nmod package (missing or broken mod.json)");
            return 1;
        }

        Console.WriteLine($"id            : {manifest.Id}");
        Console.WriteLine($"name          : {manifest.Name}");
        Console.WriteLine($"version       : {manifest.Version}");
        if (!string.IsNullOrWhiteSpace(manifest.Description))
        {
            Console.WriteLine($"description   : {manifest.Description}");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Authors))
        {
            Console.WriteLine($"authors       : {manifest.Authors}");
        }

        Console.WriteLine($"dependencies  : {(manifest.Dependencies.Count == 0 ? "(none)" : string.Join(", ", manifest.Dependencies))}");
        Console.WriteLine($"incompatible  : {(manifest.Incompatibilities.Count == 0 ? "(none)" : string.Join(", ", manifest.Incompatibilities))}");
        Console.WriteLine($"package       : {Path.GetFullPath(file)}");
        return 0;
    }

    private static int Install(string gameDir, string[] args)
    {
        // nami nmod install <file.nmod> [gameDir]
        var file = args.FirstOrDefault();
        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine($"nami nmod install: package not found: '{file}'");
            Usage();
            return 1;
        }

        var root = NamiPaths.FindRoot(gameDir);
        if (root is null)
        {
            Console.Error.WriteLine($"no Nami install found in '{gameDir}' — run `nami install \"{gameDir}\"` first");
            return 1;
        }

        var modsDir = Path.Combine(root, "mods");
        var hub = new LogHub { MinimumLevel = LogLevel.Warn };

        // Validate + load the manifest BEFORE touching the mods directory.
        var manifest = NamiPackage.ReadManifest(file, hub);
        if (manifest is null)
        {
            Console.Error.WriteLine($"'{file}' is not a valid .nmod package (missing or broken mod.json)");
            return 1;
        }

        // Refuse packages incompatible with something already installed.
        var installed = InstalledIds(modsDir);
        foreach (var id in manifest.Incompatibilities)
        {
            if (installed.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"nami nmod install: '{manifest.Id}' is incompatible with installed '{id}' — remove that package first");
                return 1;
            }
        }

        try
        {
            NamiPackage.Install(file, modsDir, hub);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nami nmod install: {ex.Message}");
            return 1;
        }

        // Dependency warnings: loose DLL plugins carry their manifest in the assembly and are
        // not enumerable here, so this checks dependencies against installed .nmod packages
        // (and the freshly installed one) and reports honestly what it cannot verify.
        var nowInstalled = installed;
        nowInstalled.Add(manifest.Id);
        foreach (var dep in manifest.Dependencies)
        {
            if (!nowInstalled.Contains(dep, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"warning: '{manifest.Id}' depends on '{dep}' — not found among installed .nmod packages; " +
                                        "if it is a loose DLL plugin, ensure it is installed or the plugin may fail to load");
            }
        }

        Console.WriteLine($"installed '{manifest.Name}' {manifest.Version} -> mods/{Sanitize(manifest.Id)}/");
        return 0;
    }

    private static HashSet<string> InstalledIds(string modsDir)
    {
        // Installed .nmod packages keep mod.json as a LOOSE file under mods/<id>/ (not a
        // .nmod archive), so parse it directly rather than via NamiPackage.ReadManifest.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(modsDir))
        {
            return ids;
        }

        foreach (var manifestFile in Directory.EnumerateFiles(modsDir, "mod.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestFile));
                if (doc.RootElement.TryGetProperty("id", out var id) &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    ids.Add(id.GetString()!);
                }
            }
            catch
            {
                // An unreadable loose manifest is not an installed package id.
            }
        }

        return ids;
    }

    private static string Sanitize(string id) =>
        string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    private static void Usage()
    {
        Console.WriteLine("""
            usage: nami nmod <command> [args...] [gameDir]

              info     <file.nmod>               print a package's manifest (no extraction)
              install  <file.nmod> [gameDir]     install a package into the game root's mods/
              help                               show this help
            """);
    }
}
