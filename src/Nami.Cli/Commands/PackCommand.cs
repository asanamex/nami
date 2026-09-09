namespace Nami.Cli.Commands;

/// <summary>
/// `nami pack [out.zip] [--artifacts &lt;root&gt;]` - builds the self-contained installer artifact
/// (Nami-Install): managed runtime + native injector/loader + bundled .NET runtime in one zip,
/// with a SHA-256 manifest. End users install it with `nami install &lt;game&gt; --from &lt;artifact.zip|url&gt;`.
/// </summary>
internal static class PackCommand
{
    public static int Run(string? outPath, string? artifactsRoot)
    {
        var repoRoot = Stager.FindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("could not locate the Nami repo root — run `nami pack` from a repo checkout, " +
                                    "or pass --artifacts <root> with the build outputs.");
            return 1;
        }

        try
        {
            var result = Stager.Pack(repoRoot, outPath ?? $"nami-{typeof(Stager).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}.zip", artifactsRoot);
            Console.WriteLine($"packed Nami {result.Version} (bundled .NET {result.Runtime}) -> {result.FilePath}");
            Console.WriteLine($"  {result.FileCount} files, {result.SizeBytes / 1024.0 / 1024.0:F1} MB — self-contained installer artifact");
            Console.WriteLine();
            Console.WriteLine("next steps:");
            Console.WriteLine($"  nami install \"<game>\" --from {result.FilePath}");
            Console.WriteLine("  (or host the zip and point players at: nami install \"<game>\" --from <url>)");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}