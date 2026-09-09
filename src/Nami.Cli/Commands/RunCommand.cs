using System.Diagnostics;
using Nami.Core.Configuration;

namespace Nami.Cli.Commands;

/// <summary>
/// `nami run &lt;mod.csproj&gt; [gameDir]` - the modder's loop: build the mod, drop it into the
/// staged root's mods/ folder, and launch the game with Nami. Requires an existing Nami root
/// (see `nami install`/`nami stage`), a configured game exe, and the mod to reference
/// Nami.Sdk (optionally Nami.Tide).
/// </summary>
internal static class RunCommand
{
    public static int Run(string gameDir, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: nami run <mod.csproj> [gameDir]");
            return 2;
        }

        var project = Path.GetFullPath(args[0]);
        if (!File.Exists(project))
        {
            Console.Error.WriteLine($"mod project not found: {project}");
            return 1;
        }

        var root = InstallContext.RequireRoot(gameDir);
        var config = NamiConfig.Load(root);
        var gameDirFull = Path.GetDirectoryName(root) ?? gameDir;
        var gameExe = LaunchCommand.ResolveGameExe(gameDirFull, config);

        // 1. Build the mod.
        Console.WriteLine($"building {project} ...");
        var build = RunDotNet($"build \"{project}\" -c Release", gameDirFull);
        if (build != 0)
        {
            Console.Error.WriteLine("mod build failed");
            return build;
        }

        var modsDir = Path.Combine(root, "mods");
        Directory.CreateDirectory(modsDir);

        // 2. Copy the produced DLL (and its deps) into mods/.
        var outputDir = FindOutputDir(project);
        if (outputDir is null)
        {
            Console.Error.WriteLine("could not locate the mod's build output");
            return 1;
        }

        var copied = StageModOutput(outputDir, modsDir);
        Console.WriteLine($"staged {copied} assembly(ies) into {modsDir}");

        // 3. Launch.
        Console.WriteLine($"launching '{gameExe}' with Nami...");
        var message = Launcher.LaunchInjected(gameExe, root, new ProcessRunner());
        Console.WriteLine(message);
        return 0;
    }

    /// <summary>Copies a mod's build output DLLs into a nami mods dir (skipping Nami.* framework assemblies).</summary>
    internal static int StageModOutput(string outputDir, string modsDir)
    {
        Directory.CreateDirectory(modsDir);
        var copied = 0;
        foreach (var dll in Directory.EnumerateFiles(outputDir, "*.dll"))
        {
            var name = Path.GetFileName(dll);
            // Nami framework assemblies are provided by the loader; copying them would shadow
            // the shared copies (chainloader resolves Nami.* from the already-loaded context).
            if (name.StartsWith("Nami.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(dll, Path.Combine(modsDir, name), overwrite: true);
            copied++;
        }

        return copied;
    }

    internal static string? FindOutputDir(string projectPath)
    {
        // The output is <proj>/bin/Release/<tfm>/ - return the first TFM dir under bin/Release.
        var bin = Path.Combine(Path.GetDirectoryName(projectPath)!, "bin", "Release");
        if (!Directory.Exists(bin))
        {
            return null;
        }

        return Directory.EnumerateDirectories(bin).FirstOrDefault();
    }

    private static int RunDotNet(string arguments, string workingDir)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("failed to start dotnet");
        process.WaitForExit();
        return process.ExitCode;
    }
}
