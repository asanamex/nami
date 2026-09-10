using Nami.Core.Configuration;

namespace Nami.Cli.Commands;

/// <summary>
/// `nami create [offline|steam] [gameDir]` - writes launchNami.exe + run-with-nami.bat into the
/// nami root so the game can be launched with Nami by double-clicking, without the CLI open.
/// </summary>
internal static class CreateCommand
{
    public static int Run(string gameDir, string? mode)
    {
        var root = InstallContext.RequireRoot(gameDir);
        var config = NamiConfig.Load(root);
        var gameDirFull = Path.GetDirectoryName(root) ?? gameDir;
        var gameExe = LaunchCommand.ResolveGameExe(gameDirFull, config);

        if (mode is "steam" && config.SteamAppId is null
            && !File.Exists(Path.Combine(Path.GetDirectoryName(gameExe) ?? gameDirFull, "steam_appid.txt")))
        {
            Console.WriteLine("warning: no steam app id is set — `nami launch steam` needs one (`nami launch set --steam-id <appid>`).");
        }

        ShortcutGenerator.WriteShortcuts(root, gameExe, mode ?? "offline");

        var shim = Path.Combine(root, ShortcutGenerator.ShimFileName);
        var bat = Path.Combine(root, ShortcutGenerator.BatFileName);
        Console.WriteLine($"created {shim}");
        Console.WriteLine($"created {bat}");
        Console.WriteLine();
        Console.WriteLine("double-click launchNami.exe (or run-with-nami.bat) to launch the game with Nami.");
        return 0;
    }
}
