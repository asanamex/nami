using System.Reflection;
using System.Runtime.Loader;

namespace Nami.Core.Plugins;

/// <summary>
/// Minimal <see cref="AssemblyLoadContext"/> used to inspect candidate plugin assemblies
/// (discovery) and then be unloaded so nothing from the probe leaks into the process.
/// </summary>
public sealed class ProbeLoadContext : AssemblyLoadContext
{
    public ProbeLoadContext() : base($"nami-probe-{Guid.NewGuid():N}", isCollectible: true)
    {
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // During discovery we only need to reflect plugin types, which requires resolving
        // the shared framework (Nami.Sdk and friends). Those may live in the default context
        // (tests) OR in the hostfxr component ALC (in-game) — so first look for an already
        // loaded copy anywhere in the process, then fall back to the default context.
        // Plugin-to-plugin deps are NOT resolved here; DependencyResolver validates those.
        var name = assemblyName.Name;
        if (name is not null &&
            (name.StartsWith("Nami.Sdk", StringComparison.Ordinal) ||
             name.StartsWith("Nami.Core", StringComparison.Ordinal) ||
             name.StartsWith("Nami.Tide", StringComparison.Ordinal) ||
             name.StartsWith("Nami.Wave", StringComparison.Ordinal) ||
             name.StartsWith("System.", StringComparison.Ordinal) ||
             name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
             name is "netstandard" or "mscorlib"))
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(asm.GetName().Name, name, StringComparison.Ordinal))
                    {
                        return asm;
                    }
                }
            }
            catch
            {
                // fall through to default
            }

            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
