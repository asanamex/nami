using System.Reflection;
using System.Runtime.Loader;

namespace Nami.Core.Plugins;

/// <summary>
/// Loads and owns a single plugin assembly in its own unloadable load context,
/// and resolves shared references (the SDK and any Nami runtime assemblies) from the default context.
/// Assemblies are loaded from raw bytes so the on-disk files are never locked - a mod can be
/// rebuilt in place while the game runs, which is what makes hot reload possible.
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

    public Assembly LoadPluginAssembly() => LoadWithoutFileLock(_assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
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
            // Shared framework: reuse an already-loaded copy wherever it lives (the hostfxr
            // component ALC in-game, or the default context in tests) so plugin types unify
            // with the loader's. Fall back to the default context.
            try
            {
                // NB: see ProbeLoadContext - AppDomain.GetAssemblies() omits other
                // load contexts, so enumerate per-context for type unification.
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
        return resolved is null ? null : LoadWithoutFileLock(resolved);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private Assembly LoadWithoutFileLock(string path)
    {
        // NB: static Assembly.Load(byte[]) lands in the Default context - isolation and
        // unloadability silently lost. LoadFromStream binds to THIS context.
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

    private WeakReference? _weak;

    /// <summary>
    /// Captures a fresh weak reference to this context. Called by the chainloader after
    /// dropping the plugin from the active set, so collectibility can be reported:
    /// <c>IsAlive</c> flips false once the ALC and its assemblies have actually been collected.
    /// </summary>
    public WeakReference CollectWeakReference()
    {
        _weak = new WeakReference(this, trackResurrection: false);
        return _weak;
    }
}
