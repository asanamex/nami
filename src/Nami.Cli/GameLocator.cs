using System.Runtime.InteropServices;

namespace Nami.Cli;

/// <summary>
/// Resolves which executable in a game directory is the actual game, either from an explicit
/// `nami launch set` choice or by auto-detection (largest candidate .exe).
/// </summary>
public static class GameLocator
{
    /// <summary>
    /// File names (case-insensitive) that are never the game itself: crash handlers, updaters,
    /// browser/CEF helpers, anti-cheat loaders, installers and the like.
    /// </summary>
    public static readonly string[] KnownNonGameExes =
    {
        "UnityCrashHandler", "UnityCrashHandler64", "UnityWebBrowser", "UnityWebBrowser.Engine.Cef",
        "UnityWebBrowser.Engine.Shared", "UnityWebBrowser.Engine", "UnityBugReporter",
        "CrashReport", "CrashReportClient", "Redist", "Uninstall", "Uninstaller", "Setup",
        "installer", "updater", "Updater", "EAC", "EasyAntiCheat", "BattlEye", "BEService",
        "steam", "steamclient", "dxdiag", "vc_redist", "vcredist", "UEPrereqSetup",
        "UEPrereqHandler", "DotNetInstaller", "Launcher", "GameLauncher", "start_protected_game",
        "doomlauncher", "GalaxyClient", "EpicGamesLauncher", "Notepad"
    };

    /// <summary>True when the file name (without extension) is a known non-game helper exe.</summary>
    public static bool IsKnownNonGameExe(string fileNameWithoutExtension) =>
        KnownNonGameExes.Contains(fileNameWithoutExtension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a user-supplied path: full path, or a bare file name searched in the game dir and its immediate subdirs.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist in the game directory.</exception>
    /// <exception cref="ArgumentException">The path is not an .exe or is a known non-game helper.</exception>
    public static string ResolveExplicit(string gameDir, string gameExe, bool force = false)
    {
        string full;
        if (Path.IsPathRooted(gameExe))
        {
            full = Path.GetFullPath(gameExe);
        }
        else if (File.Exists(Path.Combine(gameDir, gameExe)))
        {
            full = Path.GetFullPath(Path.Combine(gameDir, gameExe));
        }
        else
        {
            // The game exe often lives in a subfolder (e.g. <game>/bin/MyGame.exe): search
            // one level down before giving up.
            full = Directory.EnumerateFiles(gameDir, gameExe, SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetDirectoryName(p)!.Count(c => c == Path.DirectorySeparatorChar))
                .FirstOrDefault() ?? Path.GetFullPath(Path.Combine(gameDir, gameExe));
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"game executable not found: {full}");
        }

        ValidateGameExe(full, force);
        return full;
    }

    /// <summary>Validates that a path is an .exe file and (unless forced) not a known non-game helper.</summary>
    /// <exception cref="ArgumentException">The path fails validation.</exception>
    public static void ValidateGameExe(string fullPath, bool force = false)
    {
        if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{fullPath}' is not an .exe file");
        }

        if (!force && IsKnownNonGameExe(Path.GetFileNameWithoutExtension(fullPath)))
        {
            throw new ArgumentException(
                $"'{Path.GetFileName(fullPath)}' looks like a crash handler / updater / anti-cheat helper, not the game. " +
                "Use `nami launch set --force <exe>` to override.");
        }

        if (!LooksLikePeExecutable(fullPath))
        {
            throw new ArgumentException($"'{fullPath}' does not look like a Windows executable");
        }
    }

    /// <summary>
    /// Auto-detects the game executable: the largest .exe directly in the game dir, skipping known
    /// non-game helpers; falls back to a heuristic walk of subdirectories when the top level has no
    /// candidate. Returns null when nothing plausible is found.
    /// </summary>
    public static string? AutoDetect(string gameDir)
    {
        var top = LargestExe(Directory.EnumerateFiles(gameDir, "*.exe"));
        if (top is not null)
        {
            return top;
        }

        // Heuristic fallback: some games keep the real exe under a subfolder (e.g. <game>/bin/).
        // Bound the walk to keep it fast on huge trees and avoid the game's own data folders.
        string? best = null;
        long bestSize = 0;
        foreach (var dir in Directory.EnumerateDirectories(gameDir))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("_Data", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("nami", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    if (IsKnownNonGameExe(Path.GetFileNameWithoutExtension(exe)))
                    {
                        continue;
                    }

                    var size = new FileInfo(exe).Length;
                    if (size > bestSize)
                    {
                        best = exe;
                        bestSize = size;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Not readable; skip this subdirectory.
            }
        }

        return best;
    }

    private static string? LargestExe(IEnumerable<string> exes)
    {
        string? best = null;
        long bestSize = 0;
        foreach (var exe in exes)
        {
            if (IsKnownNonGameExe(Path.GetFileNameWithoutExtension(exe)))
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(exe).Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (size > bestSize)
            {
                best = exe;
                bestSize = size;
            }
        }

        return best;
    }

    /// <summary>Cheap PE check: the file starts with MZ and has a valid e_lfanew pointing at PE\0\0.</summary>
    public static bool LooksLikePeExecutable(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[64];
            var read = fs.Read(header);
            if (read < 64 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                return false;
            }

            var peOffset = MemoryMarshal.Read<int>(header[0x3C..]);
            if (peOffset <= 0 || peOffset > fs.Length - 4)
            {
                return false;
            }

            fs.Position = peOffset;
            Span<byte> sig = stackalloc byte[4];
            if (fs.Read(sig) < 4)
            {
                return false;
            }

            return sig[0] == (byte)'P' && sig[1] == (byte)'E' && sig[2] == 0 && sig[3] == 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
