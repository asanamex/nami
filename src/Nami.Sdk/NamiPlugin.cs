namespace Nami.Sdk;

/// <summary>
/// Base class for all Nami plugins.
/// A plugin instance is created per loaded mod, isolated in its own load context.
/// </summary>
public abstract class NamiPlugin
{
    private IPluginContext? _context;

    /// <summary>
    /// The runtime context handed to this plugin by the chainloader.
    /// Calling any member before <see cref="OnLoad"/> throws.
    /// </summary>
    public IPluginContext Context => _context ?? throw new InvalidOperationException(
        "Plugin context is not available before OnLoad().");

    /// <summary>Metadata for this plugin, as declared by the chainloader.</summary>
    public PluginInfo Info => Context.Info;

    /// <summary>
    /// Called by the chainloader once, before <see cref="OnLoad"/>, to bind this plugin's runtime context.
    /// </summary>
    public void Attach(IPluginContext context) => _context = context;

    /// <summary>Called once, after the plugin is loaded and its dependencies are satisfied.</summary>
    public virtual void OnLoad() { }

    /// <summary>Called once when the plugin is disabled (game exit, quarantine, or unload).</summary>
    public virtual void OnUnload() { }

    /// <summary>Called every frame while the plugin is active. Prefer a game-loop hook for production mods.</summary>
    public virtual void OnUpdate() { }
}
