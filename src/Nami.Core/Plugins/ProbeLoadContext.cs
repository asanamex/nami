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

    /// <summary>
    /// Loads the candidate assembly from raw bytes so the file on disk is never locked -
    /// discovery must not prevent a mod being rebuilt or deleted in place (hot reload).
    /// NB: Assembly.Load(byte[]) would land in the Default context (isolation and
    /// unloadability silently lost); LoadFromStream binds to THIS context.
    /// </summary>
    public Assembly LoadAssemblyNoLock(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pdbPath = Path.ChangeExtension(path, ".pdb");
        using var asmStream = new MemoryStream(bytes, writable: false);
        if (!File.Exists(pdbPath))
        {
            return LoadFromStream(asmStream);
        }
        using var pdbStream = new MemoryStream(File.ReadAllBytes(pdbPath), writable: false);
        return LoadFromStream(asmStream, pdbStream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // During discovery we only need to reflect plugin types, which requires resolving
        // the shared framework (Nami.Sdk and friends). Those may live in the default context
        // (tests) OR in the hostfxr component ALC (in-game) - so first look for an already
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
                // NB: AppDomain.GetAssemblies() omits assemblies living in other
                // load contexts (the hostfxr component ALC in-game) - enumerate
                // per-context instead, or shared Nami.* types never unify.
                foreach (var alc in AssemblyLoadContext.All)
                {
                    foreach (var asm in alc.Assemblies)
                    {
                        if (string.Equals(asm.GetName().Name, name, StringComparison.Ordinal))
                        {
                            return asm;
                        }
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
