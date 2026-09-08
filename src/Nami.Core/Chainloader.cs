using System.Diagnostics;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Core.Plugins;
using Nami.Core.Profiling;
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

/// <summary>Outcome of a hot-reload operation.</summary>
/// <param name="Reloaded">Plugin ids that were reloaded into a new generation, in load order.</param>
/// <param name="Unloaded">Plugin ids that were unloaded (file removed / disabled).</param>
/// <param name="Failed">Plugin ids whose new generation failed to load (previous generation kept).</param>
public sealed record ReloadResult(
    IReadOnlyList<string> Reloaded,
    IReadOnlyList<string> Unloaded,
    IReadOnlyList<string> Failed)
{
    public bool AnyChanges => Reloaded.Count > 0 || Unloaded.Count > 0 || Failed.Count > 0;
}

/// <summary>Event payload for plugin reloads.</summary>
public sealed class HotReloadEventArgs(string pluginId, int oldGeneration, int newGeneration, bool success, string? error)
{
    public string PluginId { get; } = pluginId;
    public int OldGeneration { get; } = oldGeneration;
    public int NewGeneration { get; } = newGeneration;
    public bool Success { get; } = success;
    public string? Error { get; } = error;
}

/// <summary>A plugin that has been instantiated and attached to its context.</summary>
public sealed class LoadedPlugin
{
    internal LoadedPlugin(PluginManifest manifest, NamiPlugin instance, PluginContext context,
        PluginLoadContext loadContext, int generation, ModProfiler profiler)
    {
        Manifest = manifest;
        Instance = instance;
        Context = context;
        LoadContext = loadContext;
        Generation = generation;
        Profiler = profiler;
    }

    public PluginManifest Manifest { get; }
    public NamiPlugin Instance { get; }
    public PluginContext Context { get; }
    public PluginState State { get; internal set; } = PluginState.Discovered;
    public int ConsecutiveFailures { get; internal set; }
    public DateTimeOffset? QuarantinedAt { get; internal set; }
    public string? LastError { get; internal set; }

    /// <summary>How many times this plugin id has been loaded (1 = first load).</summary>
    public int Generation { get; internal set; }

    /// <summary>UTC timestamp of when this generation was loaded; hot reload compares mod file writes against it.</summary>
    public DateTimeOffset LoadedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Number of successful hot reloads of this plugin id.</summary>
    public int ReloadCount { get; internal set; }

    /// <summary>The unloadable load context owning this generation's assemblies.</summary>
    public PluginLoadContext LoadContext { get; }

    /// <summary>Built-in profiler metrics for this generation.</summary>
    public ModProfiler Profiler { get; }

    internal void Update(Chainloader chainloader)
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
            if (chainloader.Config.QuarantineEnabled &&
                ConsecutiveFailures >= chainloader.Config.QuarantineThreshold)
            {
                chainloader.Quarantine(this, ex);
            }
        }
    }
}

/// <summary>Runtime context handed to a plugin (implements the SDK's <see cref="IPluginContext"/>).</summary>
public sealed class PluginContext(PluginManifest manifest, LogHub hub, Chainloader chainloader,
    ModProfiler profiler, IPluginConfig config)
    : IPluginContext
{
    public PluginInfo Info { get; } = new(manifest.Id, manifest.Name, manifest.Version)
    {
        Authors = manifest.Authors,
        Description = manifest.Description
    };

    public ILog Log { get; } = new PluginLog(manifest.Id, hub);
    public IModMetrics Profiler { get; } = profiler;
    public IPluginConfig Config { get; } = config;
    internal Chainloader Chainloader { get; } = chainloader;

    public bool RequestReload() => Chainloader.RequestReload(manifest.Id);
}

/// <summary>
/// Loads plugins discovered in the mods directory into isolated, unloadable load contexts,
/// calls lifecycle methods, applies crash quarantine, and — when hot reload is enabled —
/// watches the mods directory so edited/rebuild plugins reload live into a new generation.
/// All structural changes flow through a command queue drained between update ticks, so
/// reloads never race a running <see cref="OnUpdate"/>.
/// </summary>
public sealed class Chainloader : IDisposable, Nami.Sdk.ITideOpSink
{
    private readonly string _modsDirectory;
    private readonly LogHub _hub;
    private readonly List<LoadedPlugin> _plugins = new();
    private readonly List<PluginLoadContext> _contexts = new();
    private readonly Queue<Action> _commands = new();
    private readonly Lock _gate = new();

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private long _tickCounter;
    private int _nextGeneration; // pre-incremented: first generation is 1
    private ModProfiler? _currentProfiler; // profiler of the plugin whose OnUpdate is running
    private bool _disposed;

    public NamiConfig Config { get; }
    public LogHub LogHub => _hub;
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public string ModsDirectory => _modsDirectory;
    public event Action<LoadedPlugin>? PluginQuarantined;
    public event Action<HotReloadEventArgs>? PluginReloaded;

    public Chainloader(string rootDirectory, NamiConfig? config = null, LogHub? hub = null)
    {
        Config = config ?? new NamiConfig { RootPath = rootDirectory };
        _hub = hub ?? new LogHub();
        _modsDirectory = Path.Combine(rootDirectory, "mods");
        Nami.Sdk.TideMetrics.Register(this);
    }

    /// <summary>
    /// Receives Tide op latencies from the bridge (via <see cref="Nami.Sdk.TideMetrics"/>)
    /// and attributes them to the plugin whose <c>OnUpdate</c> is currently running. Ops fired
    /// outside a plugin's update tick (e.g. the boot bridge self-test) are unattributed.
    /// </summary>
    void Nami.Sdk.ITideOpSink.RecordTideOp(double milliseconds) => _currentProfiler?.RecordTideOp(milliseconds);

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

    /// <summary>
    /// Glob match for <c>enabledPlugins</c> entries: <c>*</c> spans any run (including dots),
    /// <c>?</c> spans one char, case-insensitive; anything else is literal.
    /// </summary>
    internal static bool EnabledMatch(string pattern, string id)
    {
        var regex = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*", ".*", StringComparison.Ordinal)
                .Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return regex.IsMatch(id);
    }

    /// <summary>Loads and activates a single plugin. If it throws during construction or OnLoad, it is disabled, not fatal.</summary>
    public LoadedPlugin? LoadOne(PluginManifest manifest)
    {
        if (Config.EnabledPlugins.Count > 0 && !Config.EnabledPlugins.Any(pattern => EnabledMatch(pattern, manifest.Id)))
        {
            _hub.Log("chainloader", LogLevel.Info, $"Plugin '{manifest.Id}' is disabled by config");
            return null;
        }

        PluginLoadContext? loadContext = null;
        try
        {
            loadContext = new PluginLoadContext(manifest.AssemblyPath);
            lock (_gate)
            {
                _contexts.Add(loadContext);
            }

            var assembly = loadContext.LoadPluginAssembly();
            var type = PluginDiscoverer.FindPluginType(assembly)
                       ?? throw new InvalidOperationException("no [NamiPlugin] type found");

            if (Activator.CreateInstance(type) is not NamiPlugin instance)
            {
                throw new InvalidOperationException($"plugin type {type.FullName} is not a NamiPlugin");
            }

            var profiler = new ModProfiler();
            var pluginConfig = Config.PluginConfig is { } sections &&
                               sections.TryGetValue(manifest.Id, out var section)
                ? new JsonPluginConfig(section)
                : JsonPluginConfig.Empty;
            var context = new PluginContext(manifest, _hub, this, profiler, pluginConfig);
            instance.Attach(context);
            var generation = NextGeneration();
            var loaded = new LoadedPlugin(manifest, instance, context, loadContext, generation, profiler)
            {
                State = PluginState.Loaded
            };
            lock (_gate)
            {
                _plugins.Add(loaded);
            }

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
                lock (_gate)
                {
                    _contexts.Remove(loadContext);
                }

                loadContext.Unload();
            }

            return null;
        }
    }

    /// <summary>Advances every active plugin's update loop and drains pending reload commands.</summary>
    public void UpdateAll()
    {
        DrainCommands();
        var plugins = PluginsSnapshot();

        foreach (var plugin in plugins)
        {
            if (plugin.State != PluginState.Active)
            {
                continue;
            }

            _currentProfiler = Config.Profiler.Enabled ? plugin.Profiler : null;
            if (Config.Profiler.Enabled)
            {
                var sw = Stopwatch.StartNew();
                plugin.Update(this);
                sw.Stop();
                plugin.Profiler.RecordTick(sw.Elapsed.TotalMilliseconds);
            }
            else
            {
                plugin.Update(this);
            }
        }

        _currentProfiler = null;
        MaybeLogProfilerSummary();
    }

    private void MaybeLogProfilerSummary()
    {
        if (!Config.Profiler.Enabled)
        {
            return;
        }

        var intervalTicks = Math.Max(1, (int)(Config.Profiler.SummaryIntervalSeconds * 1000.0 / 16.0));
        if (Interlocked.Increment(ref _tickCounter) % intervalTicks != 0)
        {
            return;
        }

        foreach (var plugin in PluginsSnapshot())
        {
            if (plugin.State == PluginState.Active && plugin.Profiler.HasSamples)
            {
                _hub.Log("profiler", LogLevel.Info,
                    $"mod '{plugin.Manifest.Id}' gen={plugin.Generation} {plugin.Profiler.ToSummaryLine()}");
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
        PluginQuarantined?.Invoke(plugin);
        EnqueueCommand(() => UnloadPlugin(plugin));
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

    /// <summary>
    /// Fully unloads a plugin generation: OnUnload, release its Wave patches, drop the ALC,
    /// then attempt to collect it. The LoadedPlugin object is removed from the active set.
    /// </summary>
    private void UnloadPlugin(LoadedPlugin plugin)
    {
        TryUnload(plugin);
        if (plugin.State != PluginState.Quarantined)
        {
            plugin.State = PluginState.Disabled;
        }

        ReleaseWavePatches(plugin.Manifest.Id);
        lock (_gate)
        {
            _plugins.Remove(plugin);
            _contexts.Remove(plugin.LoadContext);
        }

        var weak = plugin.LoadContext.CollectWeakReference();
        _hub.Log("chainloader", LogLevel.Info,
            $"Unloaded '{plugin.Manifest.Id}' (gen {plugin.Generation}); old ALC alive={weak.IsAlive} (collectible after GC)");
    }

    // Wave is an optional subsystem (mods may not reference it); reflect so core has no
    // hard dependency on Nami.Wave.
    private static void ReleaseWavePatches(string owner)
    {
        try
        {
            var waveType = Type.GetType("Nami.Wave.Wave, Nami.Wave");
            waveType?.GetMethod("UnpatchAll")?.Invoke(null, new object[] { owner });
        }
        catch
        {
            // Wave absent or unpatch failed — never fatal for reload.
        }
    }

    // ------------------------------------------------------------------
    // Hot reload
    // ------------------------------------------------------------------

    /// <summary>Queues a reload of <paramref name="pluginId"/> at the next safe point. Returns true if queued.</summary>
    public bool RequestReload(string pluginId)
    {
        var exists = PluginsSnapshot().Any(p => p.Manifest.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            return false;
        }

        EnqueueCommand(() => ReloadCore(pluginId));
        return true;
    }

    /// <summary>Reloads a plugin (and its transitive dependents) immediately. For tests and the CLI.</summary>
    public ReloadResult Reload(string pluginId) => ReloadCore(pluginId);

    /// <summary>
    /// Implements a reload: unload the plugin's current generation plus every plugin that
    /// depends on it (transitively), then re-load the changed ids from disk in dependency
    /// order. If a new generation fails to load, the failure is reported and the plugin id
    /// is left unloaded (the old generation cannot be resurrected once its ALC is released)
    /// — but other ids in the same reload still load.
    /// </summary>
    private ReloadResult ReloadCore(string pluginId)
    {
        var all = PluginsSnapshot();
        var target = all.FirstOrDefault(p => p.Manifest.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            // Not currently loaded — for the watcher path this is a brand-new mod drop (or a
            // previously failed load): load it fresh if a manifest for the id exists on disk.
            var fresh = DiscoverPlugins()
                .FirstOrDefault(m => m.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
            if (fresh is null)
            {
                _hub.Log("chainloader", LogLevel.Warn, $"Reload: '{pluginId}' is not loaded and has no manifest on disk");
                return new ReloadResult(Array.Empty<string>(), Array.Empty<string>(), new[] { pluginId });
            }

            var newPlugin = LoadOne(fresh);
            if (newPlugin is null)
            {
                PluginReloaded?.Invoke(new HotReloadEventArgs(pluginId, 0, 0, false, "load failed (see log)"));
                return new ReloadResult(Array.Empty<string>(), Array.Empty<string>(), new[] { pluginId });
            }

            _hub.Log("chainloader", LogLevel.Info, $"Discovered new plugin '{fresh.Id}' gen {newPlugin.Generation}");
            PluginReloaded?.Invoke(new HotReloadEventArgs(fresh.Id, 0, newPlugin.Generation, true, null));
            return new ReloadResult(new[] { fresh.Id }, Array.Empty<string>(), Array.Empty<string>());
        }

        // Compute the reload set: the target + everything that depends on it, transitively.
        var byId = all.ToDictionary(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var dependents = FindTransitiveDependents(all, pluginId);
        var reloadIds = new[] { pluginId }.Concat(dependents).ToList();
        var oldGenerations = reloadIds
            .Select(id => byId.TryGetValue(id, out var p) ? (p.Manifest.Id, p.Generation) : (id, 0))
            .ToDictionary(t => t.Item1, t => t.Item2, StringComparer.OrdinalIgnoreCase);

        _hub.Log("chainloader", LogLevel.Info, $"Hot reload: {string.Join(", ", reloadIds)}");

        // Unload old generations (dependents first, then the target — reverse load order).
        var unloaded = new List<string>();
        foreach (var id in reloadIds.AsEnumerable().Reverse())
        {
            if (byId.TryGetValue(id, out var plugin))
            {
                UnloadPlugin(plugin);
                unloaded.Add(id);
            }
        }

        // Re-discover + resolve so new files (or removals) are honored, then load the ids
        // that still have a manifest on disk.
        var manifests = DiscoverPlugins().ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var resolution = DependencyResolver.Resolve(manifests.Values.ToList());
        var loaded = new List<string>();
        var failed = new List<string>();

        foreach (var manifest in resolution.LoadOrder)
        {
            if (!reloadIds.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue; // not part of this reload
            }

            var oldGen = oldGenerations.GetValueOrDefault(manifest.Id, 0);
            var result = LoadOne(manifest);
            if (result is not null)
            {
                result.ReloadCount = byId.TryGetValue(manifest.Id, out var prev) ? prev.ReloadCount + 1 : 1;
                loaded.Add(manifest.Id);
                _hub.Log("chainloader", LogLevel.Info,
                    $"Reloaded '{manifest.Id}' gen {oldGen} -> gen {result.Generation}");
                PluginReloaded?.Invoke(new HotReloadEventArgs(manifest.Id, oldGen, result.Generation, true, null));
            }
            else
            {
                failed.Add(manifest.Id);
                PluginReloaded?.Invoke(new HotReloadEventArgs(manifest.Id, oldGen, 0, false,
                    "load failed (see log)"));
            }
        }

        // Ids that were unloaded but no longer have a manifest on disk (file deleted).
        var gone = unloaded.Where(id => !manifests.ContainsKey(id)).ToList();
        if (gone.Count > 0)
        {
            _hub.Log("chainloader", LogLevel.Info, $"Removed: {string.Join(", ", gone)}");
        }

        return new ReloadResult(loaded, gone, failed);
    }

    private static List<string> FindTransitiveDependents(IReadOnlyList<LoadedPlugin> all, string pluginId)
    {
        var byId = all.ToDictionary(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var dependents = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pluginId };

        // Iterate until fixpoint: any plugin whose dependencies intersect the set joins it.
        bool changed;
        do
        {
            changed = false;
            foreach (var plugin in all)
            {
                if (seen.Contains(plugin.Manifest.Id))
                {
                    continue;
                }

                if (plugin.Manifest.Dependencies.Any(d => seen.Contains(d.Id)))
                {
                    seen.Add(plugin.Manifest.Id);
                    dependents.Add(plugin.Manifest.Id);
                    changed = true;
                }
            }
        } while (changed);

        // Dependents in load order = as they appear in the active list (deps-first).
        return all.Where(p => dependents.Contains(p.Manifest.Id)).Select(p => p.Manifest.Id).ToList();
    }

    /// <summary>Starts the file watcher so mod edits hot-reload automatically. Safe to call once.</summary>
    public void StartHotReload()
    {
        if (_watcher is not null || !Config.HotReload.Enabled || !Config.HotReload.AutoWatch)
        {
            return;
        }

        Directory.CreateDirectory(_modsDirectory);
        _watcher = new FileSystemWatcher(_modsDirectory, "*.dll")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        _watcher.Changed += OnModsFileEvent;
        _watcher.Created += OnModsFileEvent;
        _watcher.Deleted += OnModsFileEvent;
        _watcher.Renamed += OnModsFileEvent;
        _watcher.EnableRaisingEvents = true;

        _debounceTimer = new Timer(_ => DebouncedScan(), null, Timeout.Infinite, Timeout.Infinite);
        _hub.Log("chainloader", LogLevel.Info,
            $"Hot reload watching {_modsDirectory} (debounce {Config.HotReload.DebounceMs} ms)");
    }

    public void StopHotReload()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    private void OnModsFileEvent(object sender, FileSystemEventArgs e)
    {
        // Ignore our own temporary/backup writes; debounce so a build (many events) reloads once.
        var name = Path.GetFileName(e.Name ?? string.Empty);
        if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal))
        {
            return;
        }

        _debounceTimer?.Change(Config.HotReload.DebounceMs, Timeout.Infinite);
    }

    private void DebouncedScan()
    {
        var loadedIds = PluginsSnapshot().Select(p => p.Manifest.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifests = DiscoverPlugins();
        var manifestIds = manifests.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = manifestIds.Except(loadedIds).ToList();
        var removed = loadedIds.Except(manifestIds).ToList();
        var toReload = new List<string>();

        // Files whose timestamp changed since load → reload. Simplest robust proxy: any dll
        // whose last-write time is newer than when its plugin was loaded.
        var byId = manifests.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in PluginsSnapshot())
        {
            if (byId.TryGetValue(plugin.Manifest.Id, out var manifest) &&
                File.Exists(manifest.AssemblyPath))
            {
                var lastWrite = File.GetLastWriteTimeUtc(manifest.AssemblyPath);
                if (lastWrite > plugin.LoadedAtUtc + TimeSpan.FromMilliseconds(100))
                {
                    toReload.Add(plugin.Manifest.Id);
                }
            }
        }

        foreach (var id in removed)
        {
            EnqueueCommand(() => UnloadPluginById(id));
        }

        if (toReload.Count > 0 || added.Count > 0)
        {
            foreach (var id in toReload.Concat(added).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                EnqueueCommand(() => ReloadCore(id));
            }
        }
    }

    private void UnloadPluginById(string id)
    {
        var plugin = PluginsSnapshot().FirstOrDefault(p => p.Manifest.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (plugin is not null)
        {
            UnloadPlugin(plugin);
        }
    }

    private int NextGeneration() => Interlocked.Increment(ref _nextGeneration);

    private void EnqueueCommand(Action command)
    {
        lock (_gate)
        {
            _commands.Enqueue(command);
        }
    }

    private void DrainCommands()
    {
        while (true)
        {
            Action? command;
            lock (_gate)
            {
                if (_commands.Count == 0)
                {
                    return;
                }

                command = _commands.Dequeue();
            }

            try
            {
                command();
            }
            catch (Exception ex)
            {
                _hub.Log("chainloader", LogLevel.Error, $"Reload command failed: {ex.Message}");
            }
        }
    }

    private IReadOnlyList<LoadedPlugin> PluginsSnapshot()
    {
        lock (_gate)
        {
            return _plugins.ToArray();
        }
    }

    /// <summary>Calls OnUnload on every active plugin (best-effort) at shutdown.</summary>
    public void Shutdown()
    {
        StopHotReload();
        DrainCommands();
        foreach (var plugin in PluginsSnapshot())
        {
            if (plugin.State is PluginState.Active or PluginState.Faulted)
            {
                TryUnload(plugin);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Nami.Sdk.TideMetrics.Unregister(this);
        StopHotReload();
    }
}
