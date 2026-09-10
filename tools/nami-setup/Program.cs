using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Nami.Cli;
using Nami.Cli.Commands;
using Nami.Core.Configuration;

namespace Nami.Setup;

/// <summary>
/// Double-click installer for the release zip: pick a game folder, confirm the exe, answer
/// the Steam question, and get a runnable nami root plus shortcuts. Payload = the extracted
/// zip contents beside this exe (manifest.json + native/ + dotnet/).
/// </summary>
internal static class Program
{
    private sealed record Options(string? GameDir, string? Exe, string? SteamId, bool Yes, bool NoLaunch);

    private sealed class UsageException(string message) : Exception(message);

    private sealed class CancelException : Exception;

    [STAThread]
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = ParseArgs(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("usage: InstallNami [gameDir] [--exe <path>] [--steam-id <id>] [--yes] [--no-launch]");
            return 2;
        }

        try
        {
            return Run(options);
        }
        catch (CancelException)
        {
            Console.WriteLine("cancelled.");
            Pause(options);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"setup failed: {ex.Message}");
            Pause(options);
            return 1;
        }
    }

    private static Options ParseArgs(string[] args)
    {
        string? gameDir = null;
        string? exe = null;
        string? steamId = null;
        var yes = false;
        var noLaunch = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--exe":
                    exe = TakeValue(args, ref i, "--exe");
                    break;
                case "--steam-id":
                    steamId = TakeValue(args, ref i, "--steam-id");
                    break;
                case "--yes":
                    yes = true;
                    break;
                case "--no-launch":
                    noLaunch = true;
                    break;
                default:
                    if (args[i].StartsWith('-') || gameDir is not null)
                    {
                        throw new UsageException($"unknown argument: '{args[i]}'");
                    }

                    gameDir = args[i];
                    break;
            }
        }

        return new Options(gameDir, exe, steamId, yes, noLaunch);
    }

    private static string TakeValue(string[] args, ref int i, string flag)
    {
        if (++i >= args.Length)
        {
            throw new UsageException($"missing value for {flag}");
        }

        return args[i];
    }

    private static int Run(Options options)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        Console.WriteLine($"Nami setup {version}");
        Console.WriteLine();

        var payloadDir = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("cannot locate the setup payload.");
        if (!File.Exists(Path.Combine(payloadDir, Stager.ManifestFileName)) ||
            !File.Exists(Path.Combine(payloadDir, "native", "nami_boot.exe")))
        {
            throw new InvalidOperationException(
                "installer payload not found beside InstallNami.exe — copy it next to the extracted release zip and run it again.");
        }

        var gameDir = ResolveGameDir(options);
        var root = Path.Combine(gameDir, "nami");
        var gameExe = ResolveGameExe(gameDir, options);
        var (steamAppId, steamMode) = ResolveSteamApp(gameDir, gameExe, options);

        Console.WriteLine($"installing into {gameDir}...");
        var staged = Stager.InstallFromDirectory(gameDir, payloadDir);
        foreach (var created in staged.Created)
        {
            Console.WriteLine($"  created {created}");
        }

        var config = NamiConfig.Load(root);
        config.GameExe = gameExe;
        config.SteamAppId = steamAppId;
        config.Save();
        Console.WriteLine($"game exe set: {gameExe}");
        if (steamAppId is not null)
        {
            var synced = LaunchCommand.EnsureSteamAppContext(gameExe, config);
            Console.WriteLine($"steam context: app {synced}");
        }

        WriteShortcuts(root, gameExe, steamMode);

        var canLaunch = !steamMode || Launcher.SteamClientRunning();
        if (steamMode && !canLaunch)
        {
            Console.WriteLine("Steam client is not running — start Steam, then launch from launchNami.exe.");
        }

        if (canLaunch && !options.NoLaunch && (options.Yes || AskYesNo("Launch the game now?", defaultYes: true)))
        {
            Console.WriteLine($"launching '{gameExe}' with Nami...");
            Console.WriteLine(Launcher.LaunchInjected(gameExe, root, new ProcessRunner()));
        }

        Console.WriteLine();
        Console.WriteLine("done. Double-click launchNami.exe (or run-with-nami.bat) to play with Nami.");
        Console.WriteLine("Uninstall any time by deleting the nami/ folder; re-run the in-root InstallNami.exe to repair or upgrade.");
        Pause(options);
        return 0;
    }

    private static string ResolveGameDir(Options options)
    {
        var arg = options.GameDir;
        while (true)
        {
            var picked = arg ?? BrowseForGameDir();
            arg = null;
            if (picked is null)
            {
                throw new CancelException();
            }

            if (!Directory.Exists(picked))
            {
                if (options.Yes)
                {
                    throw new InvalidOperationException($"game directory not found: {picked}");
                }

                Console.WriteLine($"folder not found: {picked}");
                continue;
            }

            if (Directory.Exists(Path.Combine(picked, "nami")) && !options.Yes &&
                !AskYesNo("Nami is already installed here — upgrade in place?", defaultYes: true))
            {
                continue;
            }

            return picked;
        }
    }

    private static string? BrowseForGameDir()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Pick the folder containing the game exe",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private static string ResolveGameExe(string gameDir, Options options)
    {
        if (options.Exe is not null)
        {
            try
            {
                return GameLocator.ResolveExplicit(gameDir, options.Exe);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is ArgumentException)
            {
                throw new InvalidOperationException(ex.Message);
            }
        }

        var detected = GameLocator.AutoDetect(gameDir);
        if (detected is not null && (options.Yes || AskYesNo($"Use {Path.GetFileName(detected)}?", defaultYes: true)))
        {
            return detected;
        }

        if (detected is null && options.Yes)
        {
            throw new InvalidOperationException(
                $"could not auto-detect the game executable in '{gameDir}' — re-run without --yes to pick it.");
        }

        while (true)
        {
            var candidates = GameLocator.Candidates(gameDir);
            if (candidates.Count == 0)
            {
                Console.WriteLine("no executables found here.");
            }
            else
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    var sizeMB = new FileInfo(candidates[i]).Length / (1024 * 1024);
                    Console.WriteLine($"  {i + 1}. {Path.GetFileName(candidates[i])} ({sizeMB} MB)");
                }
            }

            Console.Write("Pick a number, type a path, or leave empty to cancel: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                throw new CancelException();
            }

            string path;
            if (int.TryParse(input, out var n) && n >= 1 && n <= candidates.Count)
            {
                path = candidates[n - 1];
            }
            else
            {
                path = input;
            }

            if (TryAcceptExe(gameDir, path) is { } accepted)
            {
                return accepted;
            }
        }
    }

    private static string? TryAcceptExe(string gameDir, string path)
    {
        try
        {
            GameLocator.ValidateGameExe(path);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message);
            if (!AskYesNo("Use it anyway?", defaultYes: false))
            {
                return null;
            }
        }

        try
        {
            return GameLocator.ResolveExplicit(gameDir, path, force: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is ArgumentException)
        {
            Console.WriteLine(ex.Message);
            return null;
        }
    }

    private static (string? AppId, bool SteamMode) ResolveSteamApp(string gameDir, string gameExe, Options options)
    {
        var steamFile = Path.Combine(Path.GetDirectoryName(gameExe) ?? gameDir, "steam_appid.txt");
        if (File.Exists(steamFile))
        {
            var fileId = File.ReadAllText(steamFile).Trim();
            if (ulong.TryParse(fileId, out _))
            {
                Console.WriteLine($"steam app id {fileId} (from steam_appid.txt)");
                return (fileId, true);
            }

            Console.WriteLine("warning: existing steam_appid.txt is not a valid app id — ignoring it.");
        }

        if (options.SteamId is not null)
        {
            if (!ulong.TryParse(options.SteamId, out _))
            {
                throw new InvalidOperationException($"invalid steam app id: '{options.SteamId}'");
            }

            return (options.SteamId, true);
        }

        if (!options.Yes && AskYesNo("Is this a Steam game?", defaultYes: false))
        {
            while (true)
            {
                Console.Write("Steam app id (digits, empty = offline): ");
                var input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    return (null, false);
                }

                if (ulong.TryParse(input, out _))
                {
                    return (input, true);
                }

                Console.WriteLine($"invalid steam app id: '{input}'");
            }
        }

        return (null, false);
    }

    private static void WriteShortcuts(string root, string gameExe, bool steamMode)
    {
        // The shim lives embedded in Nami.Cli (same binary `nami create` extracts); manifest
        // resources survive single-file publish, so read it from its home assembly.
        using var shim = typeof(ShortcutGenerator).Assembly.GetManifestResourceStream(ShortcutGenerator.ShimResourceName)
            ?? throw new InvalidOperationException(
                "launcher shim missing from Nami.Cli — rebuild Nami.Cli after publishing tools/launch-shim.");
        using var output = File.Create(Path.Combine(root, ShortcutGenerator.ShimFileName));
        shim.CopyTo(output);

        File.WriteAllText(
            Path.Combine(root, ShortcutGenerator.BatFileName),
            ShortcutGenerator.BatContent(gameExe, root, steamMode ? "steam" : "offline"),
            Encoding.Default);
        Console.WriteLine($"created {Path.Combine(root, ShortcutGenerator.ShimFileName)}");
        Console.WriteLine($"created {Path.Combine(root, ShortcutGenerator.BatFileName)}");
    }

    private static bool AskYesNo(string question, bool defaultYes)
    {
        var hint = defaultYes ? "Y/n" : "y/N";
        while (true)
        {
            Console.Write($"{question} [{hint}] ");
            var input = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(input))
            {
                return defaultYes;
            }

            if (input is "y" or "yes")
            {
                return true;
            }

            if (input is "n" or "no")
            {
                return false;
            }
        }
    }

    private static void Pause(Options options)
    {
        if (options.Yes)
        {
            return;
        }

        Console.WriteLine("Press any key to close...");
        Console.ReadKey(intercept: true);
    }
}
