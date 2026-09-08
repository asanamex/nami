using System.Reflection;
using Nami.Sdk;

namespace Nami.Core.Plugins;

/// <summary>A load-time dependency on another plugin, with an optional minimum version.</summary>
/// <param name="Id">The required plugin's id.</param>
/// <param name="MinimumVersion">Minimum accepted version (SemVer); null accepts any.</param>
public sealed record PluginDependency(string Id, string? MinimumVersion);

/// <summary>Metadata about a discovered plugin, read without executing its code.</summary>
public sealed class PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string AssemblyPath { get; init; }
    public string? Authors { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = Array.Empty<PluginDependency>();
    public IReadOnlyList<string> Incompatibilities { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Scans a mods directory for Nami plugins. Supports loose plugin DLLs in M0;
/// <c>.nmod</c> packages arrive with the packaging milestone.
/// </summary>
public static class PluginDiscoverer
{
    public static IReadOnlyList<PluginManifest> Discover(string modsDirectory, Logging.LogHub log)
    {
        var result = new List<PluginManifest>();
        if (!Directory.Exists(modsDirectory))
        {
            return result;
        }

        // Recurse so package-installed mods (mods/<package-id>/ from .nmod extraction) are
        // discovered too. Non-plugin DLLs (dependencies, native hosts) return null from the
        // probe and are skipped silently.
        foreach (var dll in Directory.EnumerateFiles(modsDirectory, "*.dll", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = ProbeAssembly(dll);
                if (manifest is not null)
                {
                    result.Add(manifest);
                }
            }
            catch (Exception ex)
            {
                log.Log("discovery", LogLevel.Warn, $"Skipping {Path.GetFileName(dll)}: {ex.Message}");
            }
        }

        return result;
    }

    private static PluginManifest? ProbeAssembly(string path)
    {
        // Probe-load in an isolated, unloadable context and reflect metadata only.
        var probeContext = new ProbeLoadContext();
        try
        {
            var assembly = probeContext.LoadAssemblyNoLock(path);
            var pluginType = FindPluginType(assembly);
            if (pluginType is null)
            {
                return null;
            }

            var info = pluginType.GetCustomAttribute<PluginInfoAttribute>();
            var id = info?.Id ?? assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var dependencies = pluginType.GetCustomAttributes<PluginDependencyAttribute>()
                .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                .Select(d => new PluginDependency(d.Id, string.IsNullOrWhiteSpace(d.MinimumVersion) ? null : d.MinimumVersion))
                .ToArray();
            var incompatibilities = pluginType.GetCustomAttributes<PluginIncompatibilityAttribute>()
                .Select(d => d.Id)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToArray();

            return new PluginManifest
            {
                Id = id,
                Name = info?.Name ?? id,
                Version = info?.Version ?? "0.0.0",
                AssemblyPath = path,
                Authors = info?.Authors,
                Description = info?.Description,
                Dependencies = dependencies,
                Incompatibilities = incompatibilities
            };
        }
        finally
        {
            probeContext.Unload();
        }
    }

    internal static Type? FindPluginType(System.Reflection.Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(NamiPlugin).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetCustomAttribute<NamiPluginAttribute>() is not null)
            {
                return type;
            }
        }

        return null;
    }
}
