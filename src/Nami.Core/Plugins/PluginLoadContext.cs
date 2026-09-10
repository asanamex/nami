using System.Reflection;
using System.Runtime.Loader;

namespace Nami.Core.Plugins;

/// <summary>
/// Loads and owns a single plugin assembly in its own unloadable load context,
/// and resolves shared references (the SDK and any Nami runtime assemblies) from the default context.
/// Assemblies are loaded from raw bytes so the on-disk files are never locked - a mod can be
/// rebuilt in place while the game runs, which is what makes hot reload possible.
/// </summary>
/// <remarks>
/// Unload-path field audit (reclamation rooting): the CLR holds a collectible ALC strongly while
/// it unloads, so any host field still referencing loaded assemblies, types, or instances roots the
/// context forever. This type's fields were audited and need no clearing:
/// <list type="bullet">
/// <item><c>_resolver</c> (<see cref="AssemblyDependencyResolver"/>) retains path strings only: it
/// answers <c>ResolveAssemblyToPath</c>/<c>ResolveUnmanagedDllToPath</c> from the assembly path captured
/// at construction and never references a loaded <c>Assembly</c>, <c>Type</c>, or instance, so it
/// cannot root this context. It is deliberately retained (not nulled): <c>Load</c> may still run
/// during the unload window, and nulling it would trade a proven-safe retention for a crash.</item>
/// <item><c>_assemblyPath</c> is a plain path string: no GC reference to loader state.</item>
/// <item>No other instance fields exist: in particular there is no per-context weak tracker or
/// cached <c>Assembly</c>/<c>Type</c> (the former <c>CollectWeakReference</c> helper was removed;
/// weak tracking lives host-side, outside the measured object, so it cannot join the graph under
/// observation).</item>
/// </list>
/// Host-side discipline that completes the picture lives in the chainloader: retirement drops every
/// strong reference (record lists, snapshots, callback refs) before requesting unload through a
/// <c>NoInlining</c> helper that returns only the weak reference, and post-unload tracking is
/// weak-only. No <c>GC.Collect</c> runs on runtime paths.
/// </remarks>
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
}
