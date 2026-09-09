namespace Nami.Cli.Commands;

/// <summary>
/// `nami install [gameDir] [--from &lt;artifact.zip|url&gt;]` - installs a Nami root next to a game.
///
/// Without `--from`: stages from this repo's build outputs (managed runtime + native injector +
/// bundled .NET runtime) - the developer-facing flow.
///
/// With `--from`: installs the self-contained Nami-Install artifact (see `nami pack`) - the
/// end-user flow. The artifact is hash-verified against its manifest and extracts over an
/// existing root without touching mods/, inex/, logs or boot-guard markers.
/// </summary>
internal static class InstallCommand
{
    public static int Run(string gameDir, string? artifact = null)
    {
        if (!Directory.Exists(gameDir))
        {
            Console.Error.WriteLine($"game directory not found: {gameDir}");
            return 1;
        }

        try
        {
            StagedRoot staged;
            if (artifact is not null)
            {
                staged = Stager.InstallFromArtifact(gameDir, artifact);
                Console.WriteLine($"installed Nami {staged.Version} from {artifact}");
            }
            else
            {
                // Locate the repo root by walking up from the current directory (or use
                // --artifacts if provided; kept simple: repo-relative outputs).
                var repoRoot = Stager.FindRepoRoot();
                if (repoRoot is null)
                {
                    Console.Error.WriteLine("could not locate the Nami repo root — run `nami install` from a repo checkout, " +
                                            "or copy build outputs manually (see docs).");
                    return 1;
                }

                staged = Stager.Stage(gameDir, repoRoot);
            }

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
}