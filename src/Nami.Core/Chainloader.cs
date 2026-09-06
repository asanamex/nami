using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Core.Plugins;
using Nami.Sdk;

namespace Nami.Core;

/// <summary>Runtime state of a single loaded plugin.</summary>
public enum PluginState
{
    Discovered,
    Loaded,
    Active,
    Disabled,
    Quarantined,
    Faulted
}

/// <summary>A plugin that has been instantiated and attached to its context.</summary>
public sealed class LoadedPlugin
{
    internal LoadedPlugin(PluginManifest manifest, NamiPlugin instance, PluginContext context)
    {
        Manifest = manifest;
        Instance = instance;
        Context = context;
    }

    public PluginManifest Manifest { get; }
    public NamiPlugin Instance { get; }
    public PluginContext Context { get; }
    public PluginState State { get; internal set; } = PluginState.Discovered;
    public int ConsecutiveFailures { get; internal set; }
    public DateTimeOffset? QuarantinedAt { get; internal set; }
    public string? LastError { get; internal set; }

    public void Update()
    {
        try
        {
            Instance.OnUpdate();
            ConsecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            ConsecutiveFailures++;
            LastError = ex.Message;
            Context.Log.Error($"OnUpdate threw ({ConsecutiveFailures} consecutive): {ex}");
            if (ConsecutiveFailures >= Context.Chainloader.Config.QuarantineThreshold)
            {
                Context.Chainloader.Quarantine(this, ex);
            }
        }
    }
}

/// <summary>Runtime context handed to a plugin (implements the SDK's <see cref="IPluginContext"/>).</summary>
public sealed class PluginContext(PluginManifest manifest, LogHub hub, Chainloader chainloader) : IPluginContext
{
    public PluginInfo Info { get; } = new(manifest.Id, manifest.Name, manifest.Version)
    {
        Authors = manifest.Authors,
        Description = manifest.Description
    };

    public ILog Log { get; } = new PluginLog(manifest.Id, hub);
    internal Chainloader Chainloader { get; } = chainloader;
}

/// <summary>
/// Loads plugins discovered in the mods directory into isolated, unloadable load contexts,
/// calls lifecycle methods, and applies crash quarantine so one bad plugin cannot take the game down.
/// </summary>
public sealed class Chainloader
{
    private readonly string _modsDirectory;
    private readonly LogHub _hub;
    private readonly List<LoadedPlugin> _plugins = new();
    private readonly List<PluginLoadContext> _contexts = new();

    public NamiConfig Config { get; }
    public LogHub LogHub => _hub;
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public string ModsDirectory => _modsDirectory;
    public event Action<LoadedPlugin>? PluginQuarantined;

    public Chainloader(string rootDirectory, NamiConfig? config = null, LogHub? hub = null)
    {
        Config = config ?? new NamiConfig { RootPath = rootDirectory };
        _hub = hub ?? new LogHub();
        _modsDirectory = Path.Combine(rootDirectory, "mods");
    }

    public void Initialize()
    {
        Directory.CreateDirectory(_modsDirectory);
    }

    /// <summary>Discovers plugin manifests in the mods directory without loading them.</summary>
    public IReadOnlyList<PluginManifest> DiscoverPlugins() => PluginDiscoverer.Discover(_modsDirectory, _hub);

    /// <summary>
    /// Discovers, resolves, loads and activates plugins in dependency order.
    /// Returns the set of plugins that failed to load.
    /// </summary>
    public IReadOnlyList<PluginManifest> LoadAll()
    {
        Initialize();
        var manifests = DiscoverPlugins();
        var resolution = DependencyResolver.Resolve(manifests);

        foreach (var (id, reason) in resolution.Skipped)
        {
            _hub.Log("chainloader", LogLevel.Warn, $"Skipping plugin '{id}': {reason}");
        }

        foreach (var manifest in resolution.LoadOrder)
        {
            LoadOne(manifest);
        }

        return _plugins.Select(p => p.Manifest).ToList();
    }

    /// <summary>Loads and activates a single plugin. If it throws during construction or OnLoad, it is disabled, not fatal.</summary>
    public LoadedPlugin? LoadOne(PluginManifest manifest)
    {
        if (!Config.EnabledPlugins.Contains(manifest.Id) && Config.EnabledPlugins.Count > 0)
        {
            _hub.Log("chainloader", LogLevel.Info, $"Plugin '{manifest.Id}' is disabled by config");
            return null;
        }

        PluginLoadContext? loadContext = null;
        try
        {
            loadContext = new PluginLoadContext(manifest.AssemblyPath);
            _contexts.Add(loadContext);
            var assembly = loadContext.LoadPluginAssembly();
            var type = PluginDiscoverer.FindPluginType(assembly)
                       ?? throw new InvalidOperationException("no [NamiPlugin] type found");

            if (Activator.CreateInstance(type) is not NamiPlugin instance)
            {
                throw new InvalidOperationException($"plugin type {type.FullName} is not a NamiPlugin");
            }

            var context = new PluginContext(manifest, _hub, this);
            instance.Attach(context);
            var loaded = new LoadedPlugin(manifest, instance, context) { State = PluginState.Loaded };
            _plugins.Add(loaded);

            _hub.Log("chainloader", LogLevel.Info, $"Loaded {manifest.Id} {manifest.Version} ({Path.GetFileName(manifest.AssemblyPath)})");

            try
            {
                instance.OnLoad();
                loaded.State = PluginState.Active;
            }
            catch (Exception ex)
            {
                loaded.State = PluginState.Faulted;
                loaded.LastError = ex.Message;
                _hub.Log("chainloader", LogLevel.Error, $"Plugin '{manifest.Id}' failed during OnLoad: {ex}");
            }

            return loaded;
        }
        catch (Exception ex)
        {
            _hub.Log("chainloader", LogLevel.Error, $"Failed to load plugin '{manifest.Id}': {ex}");
            if (loadContext is not null)
            {
                _contexts.Remove(loadContext);
                loadContext.Unload();
            }

            return null;
        }
    }

    /// <summary>Advances every active plugin's update loop.</summary>
    public void UpdateAll()
    {
        foreach (var plugin in _plugins.ToArray())
        {
            if (plugin.State == PluginState.Active)
            {
                plugin.Update();
            }
        }
    }

    /// <summary>Disables a plugin that has exceeded the quarantine threshold, keeping the game running.</summary>
    public void Quarantine(LoadedPlugin plugin, Exception cause)
    {
        plugin.State = PluginState.Quarantined;
        plugin.QuarantinedAt = DateTimeOffset.UtcNow;
        _hub.Log("chainloader", LogLevel.Error,
            $"Quarantining plugin '{plugin.Manifest.Id}' after {plugin.ConsecutiveFailures} consecutive failures: {cause.Message}");
        TryUnload(plugin);
        PluginQuarantined?.Invoke(plugin);
    }

    private void TryUnload(LoadedPlugin plugin)
    {
        try
        {
            plugin.Instance.OnUnload();
        }
        catch (Exception ex)
        {
            _hub.Log("chainloader", LogLevel.Warn, $"OnUnload threw for '{plugin.Manifest.Id}': {ex.Message}");
        }
    }

    /// <summary>Calls OnUnload on every active plugin (best-effort) at shutdown.</summary>
    public void Shutdown()
    {
        foreach (var plugin in _plugins.ToArray())
        {
            if (plugin.State is PluginState.Active or PluginState.Faulted)
            {
                TryUnload(plugin);
            }
        }
    }
}
