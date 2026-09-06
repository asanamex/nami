namespace Nami.Cli.Commands;

/// <summary>
/// `nami install` — stages a Nami root next to a game.
/// Stub in this milestone: the full self-contained installer (bundling/downloading the .NET
/// runtime + managed artifacts) is planned for when Nami ships as a downloadable product.
/// </summary>
internal static class InstallCommand
{
    public static int Run(string gameDir)
    {
        Console.WriteLine($"install target : {gameDir}");
        Console.WriteLine("install is not implemented yet — this milestone ships the launcher flow around it.");
        Console.WriteLine();
        Console.WriteLine("Until then, stage the nami root by hand (see README \"Try it against a real game\"):");
        Console.WriteLine("  copy the managed build output + native/build/nami_boot.exe + nami_loader.dll");
        Console.WriteLine("  into a '<game>/nami' folder, then run:");
        Console.WriteLine("    nami launch set <game>.exe");
        Console.WriteLine("    nami launch");
        return 0;
    }
}
