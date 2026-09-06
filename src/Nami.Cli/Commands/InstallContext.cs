using Nami.Core.Configuration;

namespace Nami.Cli.Commands;

/// <summary>Resolves the nami root and its config for a command's gameDir argument.</summary>
internal static class InstallContext
{
    public static string RequireRoot(string gameDir)
    {
        var root = NamiPaths.FindRoot(gameDir);
        if (root is null)
        {
            throw new InvalidOperationException(
                $"no Nami install found in '{gameDir}' (expected a {NamiConfig.FileName} or a 'nami' directory). " +
                "Run `nami install` or stage the nami root next to the game first.");
        }

        return root;
    }
}
