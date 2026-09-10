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
    private const string SteamCommonDirX86 = @"C:\Program Files (x86)\Steam\steamapps\common";
    private const string SteamCommonDir64 = @"C:\Program Files\Steam\steamapps\common";
    private const int BrowserPageSize = 14;

    private sealed record Options(string? GameDir, string? Exe, string? SteamId, bool Yes, bool NoLaunch);

    private sealed class UsageException(string message) : Exception(message);

    private sealed class CancelException : Exception;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
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
            Ui.Line("");
            Ui.Info("cancelled — nothing was changed.");
            Pause(options);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Ui.Error($"setup failed: {ex.Message}");
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
        Welcome(version);

        var payloadDir = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("cannot locate the setup payload.");
        if (!File.Exists(Path.Combine(payloadDir, Stager.ManifestFileName)) ||
            !File.Exists(Path.Combine(payloadDir, "native", "nami_boot.exe")))
        {
            throw new InvalidOperationException(
                "installer payload not found beside InstallNami.exe — copy it next to the extracted release zip and run it again.");
        }

        Ui.Step(1, 5, "Find your game");
        var (gameDir, detectedSteamId) = ResolveGameDir(options);
        Ui.Ok($"game folder: {gameDir}");

        Ui.Step(2, 5, "Confirm the game executable");
        Ui.Info("Nami needs to know exactly which .exe is the game (not a crash handler or updater).");
        var gameExe = ResolveGameExe(gameDir, options);
        Ui.Ok($"game exe: {gameExe}");

        Ui.Step(3, 5, "Steam or offline?");
        Ui.Info("Steam games run with full Steam context (in-game status, overlay). Offline games just run.");
        var (steamAppId, steamMode) = ResolveSteamApp(gameDir, gameExe, options, detectedSteamId);
        Ui.Ok(steamMode ? $"mode: Steam (app {steamAppId})" : "mode: offline");

        Ui.Step(4, 5, "Install Nami");
        Ui.Info("Copying the framework next to your game. Your game files are never touched —");
        Ui.Info("everything Nami owns lives in the new nami/ folder.");
        var staged = Stager.InstallFromDirectory(gameDir, payloadDir);
        Ui.Ok($"installed {staged.Created.Count} framework files into {staged.Root}");

        var config = NamiConfig.Load(staged.Root);
        config.GameExe = gameExe;
        config.SteamAppId = steamAppId;
        config.Save();
        if (steamAppId is not null)
        {
            var synced = LaunchCommand.EnsureSteamAppContext(gameExe, config);
            Ui.Ok($"steam context: app {synced} (steam_appid.txt is in place)");
        }

        WriteShortcuts(staged.Root, gameExe, steamMode);

        Ui.Step(5, 5, "Done — shortcuts ready");
        var canLaunch = !steamMode || Launcher.SteamClientRunning();
        if (steamMode && !canLaunch)
        {
            Ui.Warn("Steam client is not running — start Steam, then launch from launchNami.exe.");
        }

        if (canLaunch && !options.NoLaunch && (options.Yes || AskYesNo("Launch the game now?", defaultYes: true)))
        {
            Ui.Info($"launching '{gameExe}' with Nami injected. Watch the lines below —");
            Ui.Info("they narrate every stage of the launch.");
            Console.WriteLine(Launcher.LaunchInjected(gameExe, staged.Root, new ProcessRunner()));
        }

        Ui.Rule();
        Ui.Ok("done. Double-click launchNami.exe (or run-with-nami.bat) whenever you want mods.");
        Ui.Info("Uninstall any time by deleting the nami/ folder.");
        Ui.Info("Repair or upgrade by re-running the InstallNami.exe inside nami/.");
        Ui.Info("For anti-cheat games: install, but launch clean from Steam instead of injecting.");
        Pause(options);
        return 0;
    }

    private static void Welcome(string version)
    {
        Ui.Banner($"NAMI SETUP {version}");
        Ui.Line("Nami is a fast, isolated mod loader for Unity games.");
        Ui.Line("This wizard installs it next to your game in about a minute:");
        Ui.Line("  1. Find your game   2. Confirm its .exe   3. Steam or offline");
        Ui.Line("  4. Install          5. Play (optionally right away)");
        Ui.Line("Nothing is uploaded anywhere. To stop at any point, press Esc or close this window.");
        Ui.Rule();
    }

    private static (string GameDir, string? DetectedSteamId) ResolveGameDir(Options options)
    {
        if (options.GameDir is not null)
        {
            if (!Directory.Exists(options.GameDir))
            {
                throw new InvalidOperationException($"game directory not found: {options.GameDir}");
            }

            return (options.GameDir, null);
        }

        while (true)
        {
            Ui.Line("Where is the game?");
            Ui.Line("  1. Pick a folder (a window opens)");
            Ui.Line("  2. Load Steam games (choose from your installed list)");
            Ui.Line("  Esc. Cancel");
            var key = Ui.ReadKey("Pick 1, 2, or Esc: ");
            if (key == ConsoleKey.D1 || key == ConsoleKey.NumPad1)
            {
                var picked = BrowseForGameDir();
                if (picked is null)
                {
                    continue;
                }

                return (picked, null);
            }

            if (key == ConsoleKey.D2 || key == ConsoleKey.NumPad2)
            {
                var steam = BrowseSteamGames();
                if (steam is not null)
                {
                    return steam.Value;
                }

                continue;
            }

            if (key == ConsoleKey.Escape)
            {
                throw new CancelException();
            }
        }
    }

    private static string? BrowseForGameDir()
    {
        Ui.Info("A folder window is opening — click the folder that contains the game exe, then OK.");
        using var dialog = new FolderBrowserDialog
        {
            Description = "Pick the folder containing the game exe",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            Ui.Info("folder picker closed without a choice.");
            return null;
        }

        if (!Directory.Exists(dialog.SelectedPath))
        {
            Ui.Warn($"folder not found: {dialog.SelectedPath}");
            return null;
        }

        return dialog.SelectedPath;
    }

    private static (string GameDir, string? DetectedSteamId)? BrowseSteamGames()
    {
        var common = SteamCommonDir();
        if (common is null)
        {
            Ui.Error("no Steam library found at the default location.");
            Ui.Info(@"Steam is usually at C:\Program Files (x86)\Steam — pick option 1 instead.");
            return null;
        }

        List<string> games;
        try
        {
            games = Directory.GetDirectories(common)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Ui.Error($"could not list Steam games: {ex.Message}");
            return null;
        }

        if (games.Count == 0)
        {
            Ui.Warn($"no games installed under {common} — install one via Steam first.");
            return null;
        }

        Ui.Info($"found {games.Count} Steam game(s) — default library only.");
        Ui.Info("Move with W/S (or arrow keys), Enter selects, Esc goes back.");
        var selected = 0;
        while (true)
        {
            RenderGameList(games, selected);
            var key = Console.ReadKey(intercept: true).Key;
            if (key is ConsoleKey.W or ConsoleKey.UpArrow)
            {
                selected = Math.Clamp(selected - 1, 0, games.Count - 1);
            }
            else if (key is ConsoleKey.S or ConsoleKey.DownArrow)
            {
                selected = Math.Clamp(selected + 1, 0, games.Count - 1);
            }
            else if (key == ConsoleKey.Enter)
            {
                var folder = Path.Combine(common, games[selected]);
                var appId = FindSteamAppId(Path.GetDirectoryName(common)!, games[selected]);
                if (appId is not null)
                {
                    Ui.Ok($"selected {games[selected]} (Steam app {appId})");
                }

                return (folder, appId);
            }
            else if (key == ConsoleKey.Escape)
            {
                return null;
            }
        }
    }

    private static string? SteamCommonDir()
    {
        if (Directory.Exists(SteamCommonDirX86))
        {
            return SteamCommonDirX86;
        }

        return Directory.Exists(SteamCommonDir64) ? SteamCommonDir64 : null;
    }

    private static void RenderGameList(List<string> games, int selected)
    {
        Console.Clear();
        Ui.Banner("YOUR STEAM GAMES");
        var top = Math.Clamp(selected - BrowserPageSize / 2, 0, Math.Max(0, games.Count - BrowserPageSize));
        for (var i = top; i < Math.Min(top + BrowserPageSize, games.Count); i++)
        {
            var marker = i == selected ? Ui.Paint(ConsoleColor.Green, "> ") : "  ";
            var name = i == selected ? Ui.Paint(ConsoleColor.White, games[i]) : games[i];
            Console.WriteLine($"{marker}{i + 1}. {name}");
        }

        Ui.Line($"— {selected + 1}/{games.Count} —  W/S or arrows move · Enter selects · Esc back —");
    }

    /// <summary>
    /// Matches a Steam library folder to its app id via appmanifest_*.acf (`installdir` key).
    /// Best-effort: skips unreadable or malformed manifests.
    /// </summary>
    private static string? FindSteamAppId(string steamappsDir, string folderName)
    {
        string[] manifests;
        try
        {
            manifests = Directory.GetFiles(steamappsDir, "appmanifest_*.acf");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var manifest in manifests)
        {
            try
            {
                string? appId = null;
                string? installDir = null;
                foreach (var line in File.ReadLines(manifest))
                {
                    var parts = line.Split('"');
                    if (parts.Length < 4)
                    {
                        continue;
                    }

                    if (parts[1].Equals("appid", StringComparison.OrdinalIgnoreCase))
                    {
                        appId = parts[3];
                    }
                    else if (parts[1].Equals("installdir", StringComparison.OrdinalIgnoreCase))
                    {
                        installDir = parts[3];
                    }
                }

                if (installDir is not null && installDir.Equals(folderName, StringComparison.OrdinalIgnoreCase) &&
                    appId is not null && ulong.TryParse(appId, out _))
                {
                    return appId;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Unreadable manifest; try the next one.
            }
        }

        return null;
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
        if (detected is not null && (options.Yes || AskYesNo($"Is {Path.GetFileName(detected)} the game?", defaultYes: true)))
        {
            Ui.Info("Auto-detect picks the largest .exe that is not a known helper (crash");
            Ui.Info("handler, updater, …). If the game still boots clean later, come back and");
            Ui.Info("pick the exe by hand with InstallNami --exe <name>.");
            return detected;
        }

        if (detected is null && options.Yes)
        {
            throw new InvalidOperationException(
                $"could not auto-detect the game executable in '{gameDir}' — re-run without --yes to pick it.");
        }

        if (detected is null)
        {
            Ui.Info("Nothing looked like a game exe, so here are all the candidates.");
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
            Ui.Warn(ex.Message);
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
            Ui.Warn(ex.Message);
            return null;
        }
    }

    private static (string? AppId, bool SteamMode) ResolveSteamApp(
        string gameDir, string gameExe, Options options, string? detectedSteamId)
    {
        var steamFile = Path.Combine(Path.GetDirectoryName(gameExe) ?? gameDir, "steam_appid.txt");
        if (File.Exists(steamFile))
        {
            var fileId = File.ReadAllText(steamFile).Trim();
            if (ulong.TryParse(fileId, out _))
            {
                Ui.Info($"steam_appid.txt already says {fileId} — keeping it.");
                return (fileId, true);
            }

            Ui.Warn("existing steam_appid.txt is not a valid app id — ignoring it.");
        }

        if (options.SteamId is not null)
        {
            if (!ulong.TryParse(options.SteamId, out _))
            {
                throw new InvalidOperationException($"invalid steam app id: '{options.SteamId}'");
            }

            return (options.SteamId, true);
        }

        if (detectedSteamId is not null)
        {
            if (options.Yes || AskYesNo($"Steam detected this as app {detectedSteamId} — play it through Steam?", defaultYes: true))
            {
                return (detectedSteamId, true);
            }

            return (null, false);
        }

        if (!options.Yes && AskYesNo("Is this a Steam game?", defaultYes: false))
        {
            while (true)
            {
                Console.Write("Steam app id (just digits — find it in the store URL; empty = offline): ");
                var input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    Ui.Info("no id given — installing as offline. You can switch later with");
                    Ui.Info("InstallNami --steam-id <id> or nami launch set --steam-id <id>.");
                    return (null, false);
                }

                if (ulong.TryParse(input, out _))
                {
                    return (input, true);
                }

                Ui.Warn($"'{input}' is not digits — app ids are numbers only, e.g. 2386580.");
            }
        }

        Ui.Info("offline it is. If this is actually a Steam title, re-run with --steam-id <id>.");
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
        Ui.Ok($"shortcuts: {ShortcutGenerator.ShimFileName} + {ShortcutGenerator.BatFileName} (double-click either to play)");
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

            Ui.Warn("type y or n (or just Enter for the capital-letter default).");
        }
    }

    private static void Pause(Options options)
    {
        if (options.Yes)
        {
            return;
        }

        Ui.Line("Press any key to close...");
        Console.ReadKey(intercept: true);
    }

    /// <summary>Tiny colored-console helper. Colors are skipped when output is redirected.</summary>
    private static class Ui
    {
        public static string Paint(ConsoleColor color, string text)
        {
            if (Console.IsOutputRedirected)
            {
                return text;
            }

            var code = (int)color < 8 ? 30 + (int)color : 90 + ((int)color - 8);
            return $"\x1b[{code}m{text}\x1b[0m";
        }

        public static void Line(string text) => Console.WriteLine(text);

        public static void Banner(string text)
        {
            var bar = new string('=', text.Length + 8);
            Console.WriteLine(Paint(ConsoleColor.Cyan, bar));
            Console.WriteLine(Paint(ConsoleColor.Cyan, $"==  {text}  =="));
            Console.WriteLine(Paint(ConsoleColor.Cyan, bar));
        }

        public static void Rule() => Console.WriteLine(Paint(ConsoleColor.DarkGray, new string('-', 60)));

        public static void Step(int n, int total, string title)
        {
            Console.WriteLine();
            Console.WriteLine(Paint(ConsoleColor.Cyan, $"── Step {n}/{total}: {title} ──"));
        }

        public static void Ok(string text) => Console.WriteLine(Paint(ConsoleColor.Green, $"[ OK ] {text}"));

        public static void Info(string text) => Console.WriteLine(Paint(ConsoleColor.Gray, $"       {text}"));

        public static void Warn(string text) => Console.WriteLine(Paint(ConsoleColor.Yellow, $"[ !! ] {text}"));

        public static void Error(string text) => Console.Error.WriteLine(Paint(ConsoleColor.Red, text));

        public static ConsoleKey ReadKey(string prompt)
        {
            Console.Write(prompt);
            Console.WriteLine("  (press a key)");
            return Console.ReadKey(intercept: true).Key;
        }
    }
}
