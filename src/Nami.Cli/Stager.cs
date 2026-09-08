using System.IO.Compression;
using Nami.Core.Configuration;

namespace Nami.Cli;

/// <summary>
/// Result of a staged Nami root.
/// </summary>
public sealed class StagedRoot
{
    public required string GameDir { get; init; }
    public required string Root { get; init; }
    public required IReadOnlyList<string> Created { get; init; }
}

/// <summary>
/// Stages a runnable Nami root (<c>&lt;game&gt;/nami</c>) from this repo's build artifacts.
///
/// This is the developer-facing install: it copies the managed runtime + the native injector
/// and bundles the matching .NET runtime, then writes nami.json. It does NOT download
/// anything — the caller must have built the repo (or point at an artifacts root).
/// </summary>
public static class Stager
{
    /// <summary>Well-known repo-relative source dirs used when no explicit artifacts root is given.</summary>
    public static string RepoManagedDir(string repoRoot) =>
        Path.Combine(repoRoot, "src", "Nami.Runtime", "bin", "Release", "net10.0");

    public static string RepoNativeDir(string repoRoot) =>
        Path.Combine(repoRoot, "native", "build");

    public static string RepoDotnetDir(string repoRoot) =>
        Path.Combine(repoRoot, "artifacts", "dotnet");

    public static readonly string[] ManagedFiles =
    {
        "Nami.Runtime.dll", "Nami.Runtime.deps.json", "Nami.Runtime.runtimeconfig.json",
        "Nami.Core.dll", "Nami.Sdk.dll", "Nami.Tide.dll", "Nami.Wave.dll"
    };

    /// <summary>
    /// Stages a Nami root into <paramref name="gameDir"/>.
    /// </summary>
    /// <param name="gameDir">Directory that contains the game executable.</param>
    /// <param name="repoRoot">Repo root (to find build outputs when no artifactsRoot given).</param>
    /// <param name="artifactsRoot">Optional pre-built artifacts root: must contain the managed
    /// files, a native/ dir with nami_boot.exe + nami_loader.dll, and a dotnet/ runtime tree.</param>
    public static StagedRoot Stage(string gameDir, string repoRoot, string? artifactsRoot = null)
    {
        var managedDir = artifactsRoot ?? RepoManagedDir(repoRoot);
        var nativeDir = Path.Combine(artifactsRoot ?? repoRoot, "native", "build");
        var dotnetDir = Path.Combine(artifactsRoot ?? repoRoot, "dotnet");

        // If the caller gave a single artifacts root, derive native+dotnet from it.
        if (artifactsRoot is not null)
        {
            nativeDir = Path.Combine(artifactsRoot, "native", "build");
            dotnetDir = Path.Combine(artifactsRoot, "dotnet");
        }

        var missing = new List<string>();
        foreach (var f in ManagedFiles)
        {
            if (!File.Exists(Path.Combine(managedDir, f)))
            {
                missing.Add(Path.Combine(managedDir, f));
            }
        }

        foreach (var f in new[] { "nami_boot.exe", "nami_loader.dll" })
        {
            if (!File.Exists(Path.Combine(nativeDir, f)))
            {
                missing.Add(Path.Combine(nativeDir, f));
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "cannot stage a Nami root: missing build artifacts:\n  " + string.Join("\n  ", missing) +
                "\nBuild the repo first (dotnet build Nami.slnx && cmake --build native/build) " +
                "or pass --artifacts <root>.");
        }

        var root = Path.Combine(gameDir, "nami");
        var created = new List<string>();
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
        }

        // Managed runtime files at the root.
        foreach (var f in ManagedFiles)
        {
            var dst = Path.Combine(root, f);
            File.Copy(Path.Combine(managedDir, f), dst, overwrite: true);
            created.Add(dst);
        }

        // Native injector + loader.
        var nativeOut = Path.Combine(root, "native");
        Directory.CreateDirectory(nativeOut);
        foreach (var f in new[] { "nami_boot.exe", "nami_loader.dll" })
        {
            var dst = Path.Combine(nativeOut, f);
            File.Copy(Path.Combine(nativeDir, f), dst, overwrite: true);
            created.Add(dst);
        }

        // Bundled runtime: prefer an artifacts/dotnet tree; otherwise copy the shared runtime
        // from the local .NET install so the staged root is self-contained.
        var runtimeSource = Directory.Exists(dotnetDir) ? dotnetDir : FindLocalDotnet();
        if (runtimeSource is null)
        {
            throw new InvalidOperationException(
                "no bundled .NET runtime found — expected an 'artifacts/dotnet' tree or a local .NET " +
                "install (dotnet --list-runtimes). Copy one into the nami root manually to proceed.");
        }

        var dotnetOut = Path.Combine(root, "dotnet");
        if (Directory.Exists(dotnetOut))
        {
            Directory.Delete(dotnetOut, recursive: true);
        }

        CopyDirectory(runtimeSource, dotnetOut);
        created.Add(dotnetOut);

        // mods dir + config.
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);
        created.Add(mods);

        var configPath = Path.Combine(root, NamiConfig.FileName);
        if (!File.Exists(configPath))
        {
            var config = new NamiConfig { RootPath = root };
            config.Save();
            created.Add(configPath);
        }

        return new StagedRoot { GameDir = gameDir, Root = root, Created = created };
    }

    /// <summary>Finds a .NET shared runtime to bundle (highest installed 10.x).</summary>
    private static string? FindLocalDotnet()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot))
        {
            dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        }

        var fxr = Path.Combine(dotnetRoot, "host", "fxr");
        if (!Directory.Exists(fxr))
        {
            return null;
        }

        var version = Directory.EnumerateDirectories(fxr)
            .Select(Path.GetFileName)
            .Where(v => v is not null && v.StartsWith("10.", StringComparison.Ordinal))
            .OrderByDescending(v => v)
            .FirstOrDefault();
        if (version is null)
        {
            return null;
        }

        // Build a runtime tree shaped like the bundled layout: dotnet/host/fxr/<v> + dotnet/shared/.../<v>.
        var shared = Path.Combine(dotnetRoot, "shared");
        var target = Path.Combine(Path.GetTempPath(), "nami-dotnet-bundle", version!);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        CopyDirectory(Path.Combine(fxr, version), Path.Combine(target, "host", "fxr", version));
        CopyDirectory(Path.Combine(shared, "Microsoft.NETCore.App", version), Path.Combine(target, "shared", "Microsoft.NETCore.App", version));
        return target;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination), overwrite: true);
        }
    }
}
