namespace Nami.Sdk;

/// <summary>
/// Declares a plugin load-time dependency on another plugin (by its <see cref="PluginInfoAttribute.Id"/>).
/// The dependency is loaded and its <see cref="NamiPlugin.OnLoad"/> called before this plugin loads.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class PluginDependencyAttribute(string id) : Attribute
{
    public string Id { get; } = id;

    /// <summary>Minimum required version of the dependency (SemVer). Null accepts any.</summary>
    public string? MinimumVersion { get; init; }
}
