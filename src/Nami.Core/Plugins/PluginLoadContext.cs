using System.Reflection;
using System.Runtime.Loader;

namespace Nami.Core.Plugins;

/// <summary>
/// Loads and owns a single plugin assembly in its own unloadable load context,
/// and resolves shared references (the SDK and any Nami runtime assemblies) from the default context.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _assemblyPath;

    public PluginLoadContext(string assemblyPath) : base(
        $"nami-plugin-{Path.GetFileNameWithoutExtension(assemblyPath)}-{Guid.NewGuid():N}",
        isCollectible: true)
    {
        _assemblyPath = assemblyPath;
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    public Assembly LoadPluginAssembly() => LoadFromAssemblyPath(_assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is not null &&
            (name.StartsWith("Nami.Sdk", StringComparison.Ordinal) ||
             name.StartsWith("Nami.Core", StringComparison.Ordinal) ||
             name.StartsWith("System.", StringComparison.Ordinal) ||
             name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
             name is "netstandard" or "mscorlib"))
        {
            // Shared framework: reuse an already-loaded copy wherever it lives (the hostfxr
            // component ALC in-game, or the default context in tests) so plugin types unify
            // with the loader's. Fall back to the default context.
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
                // fall through
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

        var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolved is null ? null : LoadFromAssemblyPath(resolved);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
