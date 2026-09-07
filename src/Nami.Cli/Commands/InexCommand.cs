namespace Nami.Cli.Commands;

/// <summary>
/// `nami inex` — manages the legacy lane (nami-inex): a real BepInEx 5.x runtime booted
/// inside the game's own Mono by Nami's injector (no Doorstop proxy).
/// Layout: `nami/inex/BepInEx/{core,plugins,patchers,config}` + `nami/inex/enabled` sentinel.
/// The sentinel is the switch the native loader reads: payload staged but no sentinel
/// means a pure Nami boot.
/// </summary>
internal static class InexCommand
{
    public const string DirName = "inex";
    public const string SentinelName = "enabled";
    public const string PreloaderRelPath = "BepInEx/core/BepInEx.Preloader.dll";

    private static readonly string[] PayloadDirs = ["core", "plugins", "patchers", "config"];

    public static string InexDir(string namiRoot) => Path.Combine(namiRoot, DirName);

    public static string SentinelPath(string namiRoot) => Path.Combine(InexDir(namiRoot), SentinelName);

    public static string PreloaderPath(string namiRoot) => Path.Combine(InexDir(namiRoot), "BepInEx", "core", "BepInEx.Preloader.dll");

    public static int Run(string gameDir, string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : null;
        return sub switch
        {
            "install" => Install(gameDir, args.Skip(1).ToArray()),
            "enable" => Enable(gameDir),
            "disable" => Disable(gameDir),
            "status" => Status(gameDir),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("""
            usage: nami inex <command> [args...] [gameDir]

              install <srcDir>   copy a BepInEx 5.x tree (core [+ plugins/patchers/config])
                                 into nami/inex (cache/ never copied; stays disabled)
              enable             boot the legacy runtime on next launch (writes the sentinel)
              disable            boot Nami-only on next launch (removes the sentinel)
              status             report payload + sentinel + last-run evidence
            """);
        return 1;
    }

    private static string? RequireRoot(string gameDir)
    {
        var root = NamiPaths.FindRoot(gameDir);
        if (root is null)
        {
            Console.Error.WriteLine($"no Nami install found in '{gameDir}' — run `nami install` first");
        }

        return root;
    }

    // nami inex install <srcDir> [gameDir]
    private static int Install(string gameDir, string[] args)
    {
        if (args.Length == 0 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: nami inex install <srcDir> [gameDir]  (srcDir holds BepInEx/core/...)");
            return 1;
        }

        var root = RequireRoot(gameDir);
        if (root is null)
        {
            return 1;
        }

        var src = Path.GetFullPath(args[0]);
        var preloader = Path.Combine(src, "core", "BepInEx.Preloader.dll");
        if (!File.Exists(preloader))
        {
            Console.Error.WriteLine($"not a BepInEx 5.x tree (missing core/BepInEx.Preloader.dll): {src}");
            return 1;
        }

        var dest = Path.Combine(InexDir(root), "BepInEx");
        var copied = 0;
        foreach (var dir in PayloadDirs)
        {
            var from = Path.Combine(src, dir);
            if (!Directory.Exists(from))
            {
                continue;
            }

            CopyDir(from, Path.Combine(dest, dir), ref copied);
        }

        Directory.CreateDirectory(Path.Combine(dest, "plugins"));
        Console.WriteLine($"staged legacy payload: {dest} ({copied} files; cache/ skipped)");
        Console.WriteLine("BepInEx mods go in nami/inex/BepInEx/plugins — then `nami inex enable`.");
        Console.WriteLine("Disable Doorstop in the game folder (doorstop_config.ini: enabled=false); Nami injects instead.");
        return 0;
    }

    private static int Enable(string gameDir)
    {
        var root = RequireRoot(gameDir);
        if (root is null)
        {
            return 1;
        }

        if (!File.Exists(PreloaderPath(root)))
        {
            Console.Error.WriteLine("no legacy payload staged — run `nami inex install <srcDir>` first");
            return 1;
        }

        Directory.CreateDirectory(InexDir(root));
        File.WriteAllText(SentinelPath(root), string.Empty);
        Console.WriteLine("legacy lane enabled: BepInEx boots on next launch (with Nami alongside).");
        return 0;
    }

    private static int Disable(string gameDir)
    {
        var root = RequireRoot(gameDir);
        if (root is null)
        {
            return 1;
        }

        var sentinel = SentinelPath(root);
        if (File.Exists(sentinel))
        {
            File.Delete(sentinel);
        }

        Console.WriteLine("legacy lane disabled: next launch is Nami-only (payload kept).");
        return 0;
    }

    private static int Status(string gameDir)
    {
        var root = RequireRoot(gameDir);
        if (root is null)
        {
            return 1;
        }

        var payload = File.Exists(PreloaderPath(root));
        var enabled = File.Exists(SentinelPath(root));
        Console.WriteLine($"legacy payload : {(payload ? "staged" : "absent")}");
        Console.WriteLine($"legacy lane    : {(enabled ? "enabled" : "disabled")}");
        if (payload && !enabled)
        {
            Console.WriteLine("hint: `nami inex enable` to boot it (Doorstop stays off; Nami injects).");
        }

        // Last-run evidence (never fatal to report).
        var inexLog = Path.Combine(root, "native", "nami-inex.log");
        if (File.Exists(inexLog))
        {
            var tail = File.ReadLines(inexLog).TakeLast(3).ToArray();
            Console.WriteLine("last inex run  :");
            foreach (var line in tail)
            {
                Console.WriteLine($"  {line}");
            }
        }

        var bepinLog = Path.Combine(InexDir(root), "BepInEx", "LogOutput.log");
        Console.WriteLine(File.Exists(bepinLog)
            ? $"bepinex log    : present ({new FileInfo(bepinLog).Length} bytes)"
            : "bepinex log    : absent (legacy runtime has not completed a boot)");
        return 0;
    }

    private static void CopyDir(string from, string to, ref int copied)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            copied++;
        }
    }
}
