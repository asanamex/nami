namespace Nami.Cli.Commands;

/// <summary>
/// `nami install [gameDir]` — stages a runnable Nami root next to a game from this repo's
/// build outputs (managed runtime + native injector + bundled .NET runtime).
/// The self-contained downloadable installer (bundling the runtime into a single artifact for
/// end users) is the future "Nami-Install" product; this stages from a local build.
/// </summary>
internal static class InstallCommand
{
    public static int Run(string gameDir)
    {
        if (!Directory.Exists(gameDir))
        {
            Console.Error.WriteLine($"game directory not found: {gameDir}");
            return 1;
        }

        // Locate the repo root by walking up from the executing assembly's location, or use
        // --artifacts if provided (kept simple: repo-relative outputs).
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("could not locate the Nami repo root — run `nami install` from a repo checkout, " +
                                    "or copy build outputs manually (see docs).");
            return 1;
        }

        try
        {
            var staged = Stager.Stage(gameDir, repoRoot);
            Console.WriteLine($"staged Nami root: {staged.Root}");
            foreach (var created in staged.Created)
            {
                Console.WriteLine($"  created {created}");
            }

            Console.WriteLine();
            Console.WriteLine("next steps:");
            Console.WriteLine($"  nami launch set <game>.exe \"{gameDir}\"");
            Console.WriteLine($"  nami launch \"{gameDir}\"");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? FindRepoRoot()
    {
        // Walk up from the current directory looking for Nami.slnx (works when run from a
        // repo checkout, e.g. the repo root or a subfolder).
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Nami.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
