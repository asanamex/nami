using Nami.Core.Configuration;

namespace Nami.Cli.Commands;

/// <summary>
/// `nami create [offline|steam] [gameDir]` — writes launchNami.exe + run-with-nami.bat into the
/// nami root so the game can be launched with Nami by double-clicking, without the CLI open.
/// </summary>
internal static class CreateCommand
{
    public static int Run(string gameDir, string? mode)
    {
        var root = InstallContext.RequireRoot(gameDir);
        var config = NamiConfig.Load(root);
        var isSteam = mode is "steam";

        var gameDirFull = Path.GetDirectoryName(root) ?? gameDir;
        var gameExe = LaunchCommand.ResolveGameExe(gameDirFull, config);

        if (isSteam && string.IsNullOrEmpty(config.SteamAppId))
        {
            Console.WriteLine("warning: no steam app id is set — the shortcut will run the game with Nami injected");
            Console.WriteLine("         (offline behavior). Set one with `nami launch set --steam-id <appid>` first.");
        }

        ShortcutGenerator.WriteShortcuts(root, gameExe, mode ?? "offline");

        var shim = Path.Combine(root, ShortcutGenerator.ShimFileName);
        var bat = Path.Combine(root, ShortcutGenerator.BatFileName);
        Console.WriteLine($"created {shim}");
        Console.WriteLine($"created {bat}");
        Console.WriteLine();
        Console.WriteLine(isSteam && config.SteamAppId is not null
            ? "double-click launchNami.exe to run the game with Nami, then relay to a clean Steam session after exit."
            : "double-click launchNami.exe (or run-with-nami.bat) to launch the game with Nami.");
        return 0;
    }
}
