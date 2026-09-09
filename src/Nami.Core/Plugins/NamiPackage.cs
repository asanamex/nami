using System.IO.Compression;
using System.Text.Json;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Core.Plugins;

/// <summary>
/// A parsed <c>mod.json</c> manifest inside a <c>.nmod</c> package.
/// </summary>
public sealed class NmodManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public string? Authors { get; init; }
    public List<string> Dependencies { get; init; } = new();
    public List<string> Incompatibilities { get; init; } = new();

    /// <summary>The .nmod file this manifest was read from (set by the reader).</summary>
    public string? PackagePath { get; internal set; }
}

/// <summary>
/// Installs and reads <c>.nmod</c> packages: ZIP archives containing a root
/// <c>mod.json</c> manifest plus one or more plugin DLLs (and their native/data files).
/// Installation extracts to <c>mods/&lt;package-id&gt;/</c> - after that the package is a plain
/// on-disk plugin directory, so discovery, isolation, quarantine and hot reload work unchanged.
/// Loose DLLs remain fully supported; <c>.nmod</c> is the distribution format on top.
/// </summary>
public static class NamiPackage
{
    public const string Extension = ".nmod";
    private const string ManifestName = "mod.json";

    /// <summary>Reads the manifest from a .nmod file without extracting anything.</summary>
    public static NmodManifest? ReadManifest(string nmodPath, LogHub log)
    {
        try
        {
            using var archive = ZipFile.OpenRead(nmodPath);
            var entry = archive.GetEntry(ManifestName);
            if (entry is null)
            {
                log.Log("nmod", LogLevel.Warn, $"{Path.GetFileName(nmodPath)}: no {ManifestName}");
                return null;
            }

            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<NmodManifest>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                log.Log("nmod", LogLevel.Warn, $"{Path.GetFileName(nmodPath)}: manifest has no id");
                return null;
            }

            manifest.PackagePath = nmodPath;
            return manifest;
        }
        catch (Exception ex)
        {
            log.Log("nmod", LogLevel.Warn, $"{Path.GetFileName(nmodPath)}: unreadable ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Installs a .nmod into the mods directory: extracts to <c>mods/&lt;id&gt;/</c>,
    /// overwriting existing files. Returns the extraction directory.
    /// </summary>
    public static string Install(string nmodPath, string modsDirectory, LogHub log)
    {
        var manifest = ReadManifest(nmodPath, log)
                       ?? throw new InvalidOperationException($"'{Path.GetFileName(nmodPath)}' is not a valid .nmod (missing/broken {ManifestName})");

        var safeDir = string.Concat(manifest.Id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        if (string.IsNullOrWhiteSpace(safeDir))
        {
            throw new InvalidOperationException($"'{manifest.Id}' is not a usable package id");
        }

        var target = Path.Combine(modsDirectory, safeDir);
        Directory.CreateDirectory(modsDirectory);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        using (var archive = ZipFile.OpenRead(nmodPath))
        {
            foreach (var entry in archive.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!dest.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"entry '{entry.FullName}' escapes the package directory");
                }

                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var src = entry.Open();
                using var dst = File.Create(dest);
                src.CopyTo(dst);
            }
        }

        log.Log("nmod", LogLevel.Info,
            $"Installed '{manifest.Id}' {manifest.Version} -> {Path.GetRelativePath(modsDirectory, target)}");
        return target;
    }
}
