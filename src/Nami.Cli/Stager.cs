using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>Nami version of the installed framework (from the artifact manifest; null for repo staging).</summary>
    public string? Version { get; init; }

    /// <summary>Artifact this root was installed from (path/URL; null for repo staging).</summary>
    public string? Source { get; init; }
}

/// <summary>Result of `nami pack`.</summary>
public sealed record PackResult(string FilePath, string Version, string Runtime, int FileCount, long SizeBytes);

/// <summary>
/// Stages a runnable Nami root (<c>&lt;game&gt;/nami</c>) from this repo's build artifacts, and
/// packs/installs the self-contained installer artifact (Nami-Install).
///
/// Two installation sources:
/// <list type="bullet">
/// <item>Repo staging (dev-facing): copies the managed runtime + the native injector and bundles
/// the matching .NET runtime, then writes nami.json. Requires a repo checkout (or --artifacts).</item>
/// <item>Artifact install (end-user-facing): <c>nami install --from &lt;artifact.zip|url&gt;</c> -
/// a single self-contained zip produced by <c>nami pack</c> (managed + native + bundled runtime,
/// SHA-256 manifest). Extracts with hash verification and never touches user content
/// (mods/, inex/, logs, boot-guard markers).</item>
/// </list>
/// </summary>
public static class Stager
{
    /// <summary>Manifest entry inside a Nami installer artifact (product/version/runtime/file hashes).</summary>
    public const string ManifestFileName = "manifest.json";

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
    /// Root-level files from retired layouts that must not exist. A stale
    /// <c>nami_loader.dll</c> next to the managed assemblies hijacks short-name
    /// P/Invoke resolution (app-base wins over the injected <c>native/</c> copy),
    /// surfacing as phantom missing-entry errors on new exports. Removed on every
    /// Stage and artifact install; reported by <c>nami doctor</c>.
    /// </summary>
    public static readonly string[] ObsoleteRootFiles = ["nami_loader.dll"];

    /// <summary>Returns the obsolete layout files currently present in <paramref name="root"/>.</summary>
    public static IReadOnlyList<string> FindObsoleteRootFiles(string root) =>
        ObsoleteRootFiles.Where(f => File.Exists(Path.Combine(root, f))).ToList();

    private sealed record ArtifactSources(string ManagedDir, string NativeDir, string DotnetDir);

    /// <summary>
    /// Stages a Nami root into <paramref name="gameDir"/> from this repo's build outputs.
    /// </summary>
    /// <param name="gameDir">Directory that contains the game executable.</param>
    /// <param name="repoRoot">Repo root (to find build outputs when no artifactsRoot given).</param>
    /// <param name="artifactsRoot">Optional pre-built artifacts root: must contain the managed
    /// files, a native/ dir with nami_boot.exe + nami_loader.dll, and a dotnet/ runtime tree.</param>
    public static StagedRoot Stage(string gameDir, string repoRoot, string? artifactsRoot = null)
    {
        var sources = ResolveSources(repoRoot, artifactsRoot);
        ValidateSources(sources, "stage a Nami root");
        if (artifactsRoot is null)
        {
            EnsureRepoSourcesFresh(repoRoot, sources);
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
            File.Copy(Path.Combine(sources.ManagedDir, f), dst, overwrite: true);
            created.Add(dst);
        }

        // Native injector + loader.
        var nativeOut = Path.Combine(root, "native");
        Directory.CreateDirectory(nativeOut);
        foreach (var f in new[] { "nami_boot.exe", "nami_loader.dll" })
        {
            var dst = Path.Combine(nativeOut, f);
            File.Copy(Path.Combine(sources.NativeDir, f), dst, overwrite: true);
            created.Add(dst);
        }
        RemoveObsoleteRootFiles(root);

        // Bundled runtime: prefer an artifacts/dotnet tree; otherwise copy the shared runtime
        // from the local .NET install so the staged root is self-contained.
        var runtimeSource = ResolveRuntime(sources.DotnetDir);
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

        WriteRootScaffold(root, created);
        return new StagedRoot { GameDir = gameDir, Root = root, Created = created };
    }

    /// <summary>
    /// Packs a self-contained installer artifact: managed runtime + native injector/loader +
    /// bundled .NET runtime, plus a <see cref="ManifestFileName"/> with SHA-256 hashes of every
    /// file. The zip layout is the nami root layout, so install is extract + config write.
    /// User content (mods/, inex/, logs, markers) is never part of the artifact.
    /// </summary>
    /// <param name="repoRoot">Repo root (to find build outputs when no artifactsRoot given).</param>
    /// <param name="outPath">Destination zip path.</param>
    /// <param name="artifactsRoot">Optional pre-built artifacts root (see <see cref="Stage"/>).</param>
    public static PackResult Pack(string repoRoot, string outPath, string? artifactsRoot = null)
    {
        var sources = ResolveSources(repoRoot, artifactsRoot);
        ValidateSources(sources, "pack a Nami artifact");
        if (artifactsRoot is null)
        {
            EnsureRepoSourcesFresh(repoRoot, sources);
        }

        var runtimeSource = ResolveRuntime(sources.DotnetDir);
        if (runtimeSource is null)
        {
            throw new InvalidOperationException(
                "cannot pack: no bundled .NET runtime found — expected an 'artifacts/dotnet' tree or a " +
                "local .NET install (dotnet --list-runtimes).");
        }

        // The version lives under host/fxr/<ver> in both the artifacts tree and the local-runtime bundle.
        var fxrDir = Path.Combine(runtimeSource, "host", "fxr");
        var runtimeVersion = Directory.Exists(fxrDir)
            ? Directory.EnumerateDirectories(fxrDir).Select(Path.GetFileName)
                .Where(v => v is not null).OrderByDescending(v => v).FirstOrDefault() ?? "unknown"
            : "unknown";

        // Collect (relative, absolute) pairs in the nami-root layout.
        var entries = new List<(string Rel, string Abs)>();
        foreach (var f in ManagedFiles)
        {
            entries.Add((f, Path.Combine(sources.ManagedDir, f)));
        }

        entries.Add(("native/nami_boot.exe", Path.Combine(sources.NativeDir, "nami_boot.exe")));
        entries.Add(("native/nami_loader.dll", Path.Combine(sources.NativeDir, "nami_loader.dll")));
        foreach (var file in Directory.EnumerateFiles(runtimeSource, "*", SearchOption.AllDirectories))
        {
            entries.Add(("dotnet/" + Path.GetRelativePath(runtimeSource, file).Replace('\\', '/'), file));
        }

        var version = typeof(Stager).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

        // Hash every file first so the manifest can be written into the same archive.
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rel, abs) in entries)
        {
            using var fs = File.OpenRead(abs);
            hashes[rel] = Convert.ToHexStringLower(SHA256.HashData(fs));
        }

        var manifest = new ArtifactManifest
        {
            Product = "nami",
            Version = version,
            Runtime = runtimeVersion,
            Files = hashes
        };

        var fullOut = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);
        long sizeBytes = 0;
        using (var zip = ZipFile.Open(fullOut, ZipArchiveMode.Create))
        {
            foreach (var (rel, abs) in entries)
            {
                zip.CreateEntryFromFile(abs, rel, CompressionLevel.Optimal);
                sizeBytes += new FileInfo(abs).Length;
            }

            var manifestEntry = zip.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(manifestEntry.Open());
            writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
        }

        return new PackResult(fullOut, version, runtimeVersion, entries.Count, sizeBytes);
    }

    /// <summary>
    /// Installs a Nami root from a self-contained installer artifact (local path or http(s) URL).
    /// Verifies every file against the manifest's SHA-256 hashes, extracts over the existing root
    /// (upgrade-safe: only framework files are touched; mods/, inex/, logs and boot-guard markers
    /// survive), and writes nami.json with the actual root path if absent.
    /// </summary>
    /// <param name="gameDir">Directory that contains the game executable.</param>
    /// <param name="artifact">Path or URL of a Nami installer artifact (see <see cref="Pack"/>).</param>
    public static StagedRoot InstallFromArtifact(string gameDir, string artifact)
    {
        var localPath = ResolveArtifact(artifact);
        if (!File.Exists(localPath))
        {
            throw new InvalidOperationException($"installer artifact not found: {artifact}");
        }

        // Read the manifest up front so a non-Nami zip fails before touching the root.
        ArtifactManifest manifest;
        using (var zip = ZipFile.OpenRead(localPath))
        {
            var manifestEntry = zip.GetEntry(ManifestFileName)
                ?? throw new InvalidOperationException(
                    $"'{artifact}' is not a Nami installer artifact (no {ManifestFileName} — create one with `nami pack`).");
            using var reader = new StreamReader(manifestEntry.Open());
            manifest = JsonSerializer.Deserialize<ArtifactManifest>(reader.ReadToEnd(), JsonOptions)
                ?? throw new InvalidOperationException($"'{artifact}' has a corrupt {ManifestFileName}.");
        }

        if (manifest.Product != "nami")
        {
            throw new InvalidOperationException(
                $"'{artifact}' is not a Nami installer artifact (product '{manifest.Product}').");
        }

        var root = Path.Combine(gameDir, "nami");
        Directory.CreateDirectory(root);
        var created = new List<string>();

        using (var zip = ZipFile.OpenRead(localPath))
        {
            foreach (var (rel, expectedHash) in manifest.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var entry = zip.GetEntry(rel)
                    ?? throw new InvalidOperationException(
                        $"artifact is corrupt: the manifest lists '{rel}' but the archive does not contain it.");
                var dst = SafeCombine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                // Extract to a temp file, verify the hash, then move into place - a tampered
                // entry never clobbers a working file.
                var tmp = dst + ".tmp";
                string actualHash;
                using (var src = entry.Open())
                using (var tmpFs = File.Create(tmp))
                {
                    src.CopyTo(tmpFs);
                }

                using (var tmpFs = File.OpenRead(tmp))
                {
                    actualHash = Convert.ToHexStringLower(SHA256.HashData(tmpFs));
                }

                if (actualHash != expectedHash)
                {
                    File.Delete(tmp);
                    throw new InvalidOperationException(
                        $"artifact integrity check failed for '{rel}' (expected {expectedHash}, got {actualHash}) — " +
                        "the artifact is corrupt or was tampered with.");
                }

                File.Move(tmp, dst, overwrite: true);
                created.Add(dst);
            }
        }

        RemoveObsoleteRootFiles(root);
        WriteRootScaffold(root, created);
        return new StagedRoot { GameDir = gameDir, Root = root, Created = created, Version = manifest.Version, Source = artifact };
    }

    /// <summary>Locates the repo root by walking up from the current directory (or null).</summary>
    public static string? FindRepoRoot()
    {
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

    private static ArtifactSources ResolveSources(string repoRoot, string? artifactsRoot)
    {
        var managedDir = artifactsRoot ?? RepoManagedDir(repoRoot);
        var nativeDir = Path.Combine(artifactsRoot ?? repoRoot, "native", "build");
        var dotnetDir = Path.Combine(artifactsRoot ?? repoRoot, "dotnet");
        if (artifactsRoot is not null)
        {
            nativeDir = Path.Combine(artifactsRoot, "native", "build");
            dotnetDir = Path.Combine(artifactsRoot, "dotnet");
        }

        return new ArtifactSources(managedDir, nativeDir, dotnetDir);
    }

    private static void ValidateSources(ArtifactSources sources, string action)
    {
        var missing = new List<string>();
        foreach (var f in ManagedFiles)
        {
            if (!File.Exists(Path.Combine(sources.ManagedDir, f)))
            {
                missing.Add(Path.Combine(sources.ManagedDir, f));
            }
        }

        foreach (var f in new[] { "nami_boot.exe", "nami_loader.dll" })
        {
            if (!File.Exists(Path.Combine(sources.NativeDir, f)))
            {
                missing.Add(Path.Combine(sources.NativeDir, f));
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"cannot {action}: missing build artifacts:\n  " + string.Join("\n  ", missing) +
                "\nBuild the repo first (dotnet build Nami.slnx && cmake --build native/build) " +
                "or pass --artifacts <root>.");
        }
    }

    /// <summary>
    /// Refuses repo-output staging when a fresher build exists elsewhere in the checkout.
    /// The resolved managed dir is the Release tree, but a Debug build is newer after any
    /// Debug session; staging the older Release outputs then boots stale code with no
    /// error. Native binaries get the same treatment against the C++ sources. Explicit
    /// <c>--artifacts</c> roots are trusted as-is and skip this check.
    /// </summary>
    private static void EnsureRepoSourcesFresh(string repoRoot, ArtifactSources sources)
    {
        var stale = new List<string>();
        foreach (var f in ManagedFiles)
        {
            var staged = Path.Combine(sources.ManagedDir, f);
            var project = Path.GetExtension(f) == ".dll" ? Path.GetFileNameWithoutExtension(f) : "Nami.Runtime";
            var debug = Path.Combine(repoRoot, "src", project, "bin", "Debug", "net10.0", f);
            if (File.Exists(debug) && File.GetLastWriteTimeUtc(debug) > File.GetLastWriteTimeUtc(staged))
            {
                stale.Add($"{f} (Release {File.GetLastWriteTimeUtc(staged):u} < Debug {File.GetLastWriteTimeUtc(debug):u})");
            }
        }

        var nativeSources = new[] { "loader", "core", "injector" }
            .Select(d => Path.Combine(repoRoot, "native", d));
        foreach (var f in new[] { "nami_boot.exe", "nami_loader.dll" })
        {
            var binary = Path.Combine(sources.NativeDir, f);
            var binaryTime = File.GetLastWriteTimeUtc(binary);
            var newer = nativeSources
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                    .Where(s => s.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ||
                                s.EndsWith(".h", StringComparison.OrdinalIgnoreCase)))
                .Where(s => File.GetLastWriteTimeUtc(s) > binaryTime)
                .Select(s => Path.GetRelativePath(repoRoot, s))
                .ToList();
            if (newer.Count > 0)
            {
                stale.Add($"{f} older than: {string.Join(", ", newer)}");
            }
        }

        if (stale.Count > 0)
        {
            throw new InvalidOperationException(
                "refusing to stage stale build outputs:\n  " + string.Join("\n  ", stale) +
                "\nRebuild Release outputs (dotnet build Nami.slnx -c Release && cmake --build native/build) " +
                "or pass --artifacts <root> to stage an explicit tree.");
        }
    }

    /// <summary>Deletes obsolete layout files from <paramref name="root"/> (see <see cref="ObsoleteRootFiles"/>).</summary>
    private static void RemoveObsoleteRootFiles(string root)
    {
        foreach (var f in FindObsoleteRootFiles(root))
        {
            File.Delete(Path.Combine(root, f));
        }
    }

    /// <summary>Creates mods/ + nami.json (with the actual root path) in a staged root.</summary>
    private static void WriteRootScaffold(string root, List<string> created)
    {
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
    }

    /// <summary>
    /// Bundled runtime source: the artifacts/dotnet tree if present, otherwise a copy of the
    /// local .NET shared runtime shaped like the bundled layout (dotnet/host/fxr + dotnet/shared).
    /// </summary>
    private static string? ResolveRuntime(string dotnetDir)
    {
        if (Directory.Exists(dotnetDir))
        {
            return dotnetDir;
        }

        return FindLocalDotnet();
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

    /// <summary>Resolves an artifact argument to a local file path, downloading http(s) URLs to a temp file.</summary>
    private static string ResolveArtifact(string artifact)
    {
        if (Uri.TryCreate(artifact, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeFile)
            {
                return uri.LocalPath;
            }

            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                var tmp = Path.Combine(Path.GetTempPath(), "nami-install", Path.GetFileName(uri.LocalPath));
                Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
                using var client = new HttpClient();
                using var response = client.GetAsync(uri).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using var src = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var dst = File.Create(tmp);
                src.CopyTo(dst);
                return tmp;
            }
        }

        return artifact;
    }

    /// <summary>Joins an artifact-relative path onto the root, refusing anything that escapes it.</summary>
    private static string SafeCombine(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"artifact entry '{rel}' escapes the install root — refusing to extract.");
        }

        return full;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Manifest inside a Nami installer artifact.</summary>
    private sealed class ArtifactManifest
    {
        public string Product { get; set; } = "";
        public string Version { get; set; } = "";
        public string Runtime { get; set; } = "";

        /// <summary>Archive-relative path → lowercase hex SHA-256 of the file content.</summary>
        public Dictionary<string, string> Files { get; set; } = new();
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