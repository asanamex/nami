using System.Diagnostics;

namespace Nami.Cli;

/// <summary>Runs a process and captures its exit code (injectable for tests).</summary>
public interface IProcessRunner
{
    int Run(string fileName, string arguments, string workingDirectory);
}

/// <summary>Default runner: spawns the process and waits for it to exit.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public int Run(string fileName, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException($"failed to start {fileName}");
        process.WaitForExit();
        return process.ExitCode;
    }
}

/// <summary>
/// Builds and runs the native injection command line and maps its result to a friendly message.
/// `nami_boot.exe &lt;game.exe&gt; &lt;loader.dll&gt;` launches the game suspended, injects the
/// loader, and resumes; the loader hosts the runtime itself.
/// </summary>
public static class Launcher
{
    /// <summary>Files that must be present for a runnable Nami install.</summary>
    public static readonly string[] RequiredRootFiles =
    {
        "Nami.Runtime.dll", "Nami.Runtime.runtimeconfig.json", "Nami.Core.dll", "Nami.Sdk.dll"
    };

    public static string BootExePath(string namiRoot) => Path.Combine(namiRoot, "native", "nami_boot.exe");

    public static string LoaderDllPath(string namiRoot) => Path.Combine(namiRoot, "native", "nami_loader.dll");

    /// <summary>Returns the missing required file names (loader, managed runtime, bundled dotnet).</summary>
    public static IReadOnlyList<string> MissingRootFiles(string namiRoot)
    {
        var missing = new List<string>();
        if (!File.Exists(BootExePath(namiRoot)))
        {
            missing.Add("native\\nami_boot.exe");
        }

        if (!File.Exists(LoaderDllPath(namiRoot)))
        {
            missing.Add("native\\nami_loader.dll");
        }

        missing.AddRange(RequiredRootFiles.Where(f => !File.Exists(Path.Combine(namiRoot, f))));

        var fxr = Path.Combine(namiRoot, "dotnet", "host", "fxr");
        if (!Directory.Exists(fxr))
        {
            missing.Add("dotnet\\host\\fxr\\ (bundled .NET runtime)");
        }

        return missing;
    }

    /// <summary>Builds the exact nami_boot command line for a game + nami root.</summary>
    public static string BuildBootArguments(string gameExe, string namiRoot) =>
        $"\"{gameExe}\" \"{LoaderDllPath(namiRoot)}\"";

    /// <summary>
    /// Launches the game with Nami injected (offline mode) and waits for the game to exit.
    /// Returns a user-facing message; exits 0 when nami_boot succeeded.
    /// </summary>
    public static string LaunchInjected(string gameExe, string namiRoot, IProcessRunner runner)
    {
        var missing = MissingRootFiles(namiRoot);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"the Nami install is incomplete (missing: {string.Join(", ", missing)}). " +
                "Re-stage the nami root next to the game.");
        }

        var args = BuildBootArguments(gameExe, namiRoot);
        var exit = runner.Run(BootExePath(namiRoot), args, Path.GetDirectoryName(gameExe) ?? namiRoot);
        return exit == 0
            ? "game exited (Nami was injected)"
            : $"nami_boot exited with code {exit} — see {Path.Combine(namiRoot, "nami.log")} and native\\nami-loader.log";
    }

    /// <summary>Launches a URI via the shell (steam://). Injectable for tests.</summary>
    internal static Action<string> OpenUri = uri => Process.Start(new ProcessStartInfo
    {
        FileName = uri,
        UseShellExecute = true
    });

    /// <summary>Detects a running Steam client. Injectable for tests.</summary>
    internal static Func<bool> SteamClientRunning = () => Process.GetProcessesByName("steam").Length > 0;

    /// <summary>Ensures the Steam client is running, booting it via steam:// if needed.</summary>
    internal static void EnsureSteamRunning(int timeoutSeconds = 30)
    {
        if (SteamClientRunning())
        {
            return;
        }

        try
        {
            OpenUri("steam://");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Steam doesn't appear to be installed or running - start Steam, then retry", ex);
        }

        for (var i = 0; i < timeoutSeconds; i++)
        {
            System.Threading.Thread.Sleep(1000);
            if (SteamClientRunning())
            {
                return;
            }
        }

        throw new InvalidOperationException($"Steam client did not appear within {timeoutSeconds}s - start Steam, then retry");
    }
}
