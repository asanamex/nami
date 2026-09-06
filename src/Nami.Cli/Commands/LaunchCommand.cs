using Nami.Core.Configuration;

namespace Nami.Cli.Commands;

/// <summary>
/// `nami launch` — remembers the game executable (`set`), then launches the game with Nami
/// injected (`offline`, default) or relays through Steam after the game exits (`steam`).
/// </summary>
internal static class LaunchCommand
{
    /// <summary>Handles `nami launch set &lt;game.exe&gt; [--steam-id &lt;appid&gt;] [--force] [gameDir]`.</summary>
    public static int Set(string gameDir, string[] args)
    {
        var root = InstallContext.RequireRoot(gameDir);
        var config = NamiConfig.Load(root);

        string? gameExe = null;
        string? steamId = null;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--force":
                    force = true;
                    break;
                case "--steam-id":
                    steamId = args[++i];
                    break;
                default:
                    gameExe = args[i];
                    break;
            }
        }

        if (gameExe is null && steamId is null)
        {
            Console.Error.WriteLine("usage: nami launch set <game.exe> [--steam-id <appid>] [--force] [gameDir]");
            return 2;
        }

        var gameDirFull = Path.GetDirectoryName(root) ?? gameDir;
        if (gameExe is not null)
        {
            var resolved = GameLocator.ResolveExplicit(gameDirFull, gameExe, force);
            config.GameExe = resolved;
            Console.WriteLine($"game exe set: {resolved}");
        }

        if (steamId is not null)
        {
            if (!ulong.TryParse(steamId, out _))
            {
                Console.Error.WriteLine($"invalid steam app id: '{steamId}'");
                return 2;
            }

            config.SteamAppId = steamId;
            Console.WriteLine($"steam app id set: {steamId}");
        }

        config.Save();
        return 0;
    }

    /// <summary>Handles `nami launch [offline|steam] [gameDir]`.</summary>
    public static int Run(string gameDir, string? mode)
    {
        var root = InstallContext.RequireRoot(gameDir);
        var config = NamiConfig.Load(root);
        var isSteam = mode is "steam";

        var gameDirFull = Path.GetDirectoryName(root) ?? gameDir;
        var gameExe = ResolveGameExe(gameDirFull, config);

        if (isSteam && string.IsNullOrEmpty(config.SteamAppId))
        {
            Console.WriteLine("note: no steam app id is set — `nami launch steam` falls back to a normal");
            Console.WriteLine("      Nami-injected launch. Set one with `nami launch set --steam-id <appid>`");
            Console.WriteLine("      to get the Steam relay (clean unmodded session after the game exits).");
            return LaunchInjected(gameExe, root);
        }

        if (isSteam && config.SteamRelaySkipInjection)
        {
            Console.WriteLine($"launching '{gameExe}' without Nami (steamRelaySkipInjection), then relaying to Steam...");
            var code = RunDirect(gameExe, gameDirFull);
            Console.WriteLine("game exited — starting the clean Steam session...");
            SteamRelay(config.SteamAppId!);
            return code;
        }

        var message = LaunchInjected(gameExe, root);
        if (isSteam && config.SteamAppId is not null)
        {
            Console.WriteLine("game exited — starting the clean Steam session...");
            SteamRelay(config.SteamAppId);
        }

        Console.WriteLine(message);
        return 0;
    }

    /// <summary>Resolves the game exe: configured value first, then auto-detection (largest .exe).</summary>
    public static string ResolveGameExe(string gameDir, NamiConfig config)
    {
        if (!string.IsNullOrEmpty(config.GameExe))
        {
            if (!File.Exists(config.GameExe))
            {
                throw new InvalidOperationException(
                    $"configured game executable no longer exists: {config.GameExe}. " +
                    "Re-run `nami launch set <game>.exe`.");
            }

            return config.GameExe;
        }

        return GameLocator.AutoDetect(gameDir)
               ?? throw new InvalidOperationException(
                   $"could not auto-detect the game executable in '{gameDir}'. " +
                   "Run `nami launch set <game>.exe`.");
    }

    private static int LaunchInjected(string gameExe, string root)
    {
        Console.WriteLine($"launching '{gameExe}' with Nami...");
        var message = Launcher.LaunchInjected(gameExe, root, new ProcessRunner());
        Console.WriteLine(message);
        return 0;
    }

    private static int RunDirect(string gameExe, string workingDir)
    {
        var runner = new ProcessRunner();
        return runner.Run(gameExe, "", workingDir);
    }

    private static void SteamRelay(string steamAppId)
    {
        try
        {
            Launcher.OpenUri($"steam://rungameid/{steamAppId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not start Steam: {ex.Message}");
        }
    }
}
