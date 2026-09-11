using System.Diagnostics;
using Nami.Core.Configuration;

namespace Nami.Cli.Commands;

/// <summary>
/// `nami launch` - remembers the game executable (`set`), then launches the game with Nami
/// injected (`offline`, default or `steam`; steam ensures the Steam client is running first).
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

    /// <summary>Handles `nami launch [offline|steam] [--debug] [gameDir]`.</summary>
    public static int Run(string gameDir, string? mode, bool debug = false)
    {
        var root = InstallContext.RequireRoot(gameDir);
        var config = NamiConfig.Load(root);

        var gameDirFull = Path.GetDirectoryName(root) ?? gameDir;
        var gameExe = ResolveGameExe(gameDirFull, config);

        if (mode is "steam")
        {
            Launcher.EnsureSteamRunning();
            var appId = EnsureSteamAppContext(gameExe, config);
            Console.WriteLine($"steam context: app {appId}");
        }

        return LaunchInjected(gameExe, root, debug);
    }

    /// <summary>
    /// Resolves the Steam app id for steam mode (configured value first, then steam_appid.txt
    /// next to the game) and syncs it back to steam_appid.txt so the injected game boots with
    /// Steam context. Throws when no app id is known or the game directory is not writable.
    /// </summary>
    public static string EnsureSteamAppContext(string gameExe, NamiConfig config)
    {
        var dir = Path.GetDirectoryName(gameExe)
            ?? throw new InvalidOperationException($"could not locate the game directory for '{gameExe}'.");
        var file = Path.Combine(dir, "steam_appid.txt");

        string? fileId = null;
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file).Trim();
            fileId = string.IsNullOrEmpty(text) ? null : text;
        }

        var appId = config.SteamAppId ?? fileId
            ?? throw new InvalidOperationException(
                "no Steam app id is set — run `nami launch set --steam-id <appid>` first.");
        if (!ulong.TryParse(appId, out _))
        {
            throw new InvalidOperationException(
                $"invalid steam app id: '{appId}'. Run `nami launch set --steam-id <appid>`.");
        }

        if (fileId != appId)
        {
            try
            {
                File.WriteAllText(file, appId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"could not write Steam app id to '{file}': {ex.Message}. " +
                    "Steam mode needs steam_appid.txt next to the game.");
            }

            Console.WriteLine($"steam app id {appId} written to steam_appid.txt");
        }

        return appId;
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

    private static int LaunchInjected(string gameExe, string root, bool debug)
    {
        Console.WriteLine($"launching '{gameExe}' with Nami...");
        var message = Launcher.LaunchInjected(gameExe, root, new ProcessRunner());
        if (!debug)
        {
            Console.WriteLine(message);
            return 0;
        }

        // Debug mode: the injector already returned (the game keeps running), so stay
        // attached streaming nami.log until the game process exits. Ctrl+C detaches
        // without touching the game.
        Console.WriteLine("injected — streaming logs until the game exits (Ctrl+C detaches)...");
        StreamLogs(
            Path.Combine(root, "nami.log"),
            Path.GetFileNameWithoutExtension(gameExe),
            Console.Out);
        Console.WriteLine("game process ended.");
        return 0;
    }

    /// <summary>
    /// Prints new <c>nami.log</c> content every 250 ms until a process named
    /// <paramref name="processName"/> has been seen and exits (gives up if it never
    /// appears within two minutes). Tolerates the log rotating underfoot.
    /// </summary>
    internal static void StreamLogs(
        string logPath, string processName, TextWriter output,
        Func<string, bool>? isRunning = null, TimeSpan? appearGrace = null)
    {
        var running = isRunning ?? (Func<string, bool>)(static name => Process.GetProcessesByName(name).Length > 0);
        var grace = appearGrace ?? TimeSpan.FromMinutes(2);
        var start = DateTimeOffset.UtcNow;
        var seen = false;
        long position = 0;

        while (true)
        {
            var alive = false;
            try
            {
                alive = running(processName);
            }
            catch
            {
                // Process enumeration is best-effort; a transient failure
                // must never kill the stream.
            }

            seen |= alive;
            if (seen && !alive)
            {
                return;
            }

            if (!seen && DateTimeOffset.UtcNow - start > grace)
            {
                output.WriteLine($"(game process '{processName}' never appeared; stopping log stream)");
                return;
            }

            try
            {
                var length = new FileInfo(logPath).Length;
                if (length < position)
                {
                    position = 0; // fresh boot truncated the log
                }

                if (length > position)
                {
                    using var stream = new FileStream(
                        logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    stream.Seek(position, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream);
                    output.Write(reader.ReadToEnd());
                    position = stream.Position;
                }
            }
            catch (IOException)
            {
                // Log locked mid-rotate or not created yet; retry next tick.
            }
            catch (UnauthorizedAccessException)
            {
                // Directory not yet readable; retry next tick.
            }

            Thread.Sleep(250);
        }
    }

}
