using System.Text;

namespace Nami.Cli;

/// <summary>
/// Generates the player-facing shortcuts inside the nami root: a small prebuilt launcher shim
/// (launchNami.exe, extracted from an embedded resource) plus a zero-dependency .bat fallback.
/// Both live inside &lt;root&gt; so the game folder stays pristine and uninstall = delete nami/.
/// </summary>
public static class ShortcutGenerator
{
    /// <summary>Embedded resource name of the prebuilt launcher shim (see tools/launch-shim).</summary>
    public const string ShimResourceName = "Nami.Cli.launchNami.exe";

    public const string ShimFileName = "launchNami.exe";
    public const string BatFileName = "run-with-nami.bat";

    /// <summary>Writes launchNami.exe (from the embedded shim) and run-with-nami.bat into the nami root.</summary>
    public static void WriteShortcuts(string namiRoot, string gameExe, string mode)
    {
        var assembly = typeof(ShortcutGenerator).Assembly;
        using var stream = assembly.GetManifestResourceStream(ShimResourceName)
            ?? throw new InvalidOperationException($"embedded shim '{ShimResourceName}' is missing — rebuild Nami.Cli");

        var shimPath = Path.Combine(namiRoot, ShimFileName);
        using (var output = File.Create(shimPath))
        {
            stream.CopyTo(output);
        }

        WriteBat(Path.Combine(namiRoot, BatFileName), gameExe, namiRoot, mode);
    }

    internal static string BatContent(string gameExe, string namiRoot, string mode)
    {
        // The shim knows the paths already (it is written beside the loader and derives the root);
        // the .bat is a plain console fallback that calls the injector directly.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("rem Launches the game through Nami. Created by `nami create`.");
        sb.AppendLine($"set GAME={gameExe}");
        sb.AppendLine($"set NAMI=%~dp0");
        sb.AppendLine($"\"%NAMI%native\\nami_boot.exe\" \"%GAME%\" \"%NAMI%native\\nami_loader.dll\"");
        if (mode.Equals("steam", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("rem (steam relay is handled by launchNami.exe; this batch runs the Nami-injected game)");
        }

        sb.AppendLine("pause");
        return sb.ToString();
    }

    private static void WriteBat(string path, string gameExe, string namiRoot, string mode)
    {
        var content = BatContent(gameExe, namiRoot, mode);
        File.WriteAllText(path, content, Encoding.Default); // keep cmd.exe happy on legacy codepages
    }
}
