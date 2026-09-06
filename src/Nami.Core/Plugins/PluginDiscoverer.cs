using System.Reflection;
using Nami.Sdk;

namespace Nami.Core.Plugins;

/// <summary>Metadata about a discovered plugin, read without executing its code.</summary>
public sealed class PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string AssemblyPath { get; init; }
    public string? Authors { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
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

        foreach (var dll in Directory.EnumerateFiles(modsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
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
            var assembly = probeContext.LoadFromAssemblyPath(path);
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
                .Select(d => d.Id)
                .Where(d => !string.IsNullOrWhiteSpace(d))
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
