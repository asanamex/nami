namespace Nami.Sdk;

/// <summary>
/// Metadata describing a plugin. Declared via <see cref="PluginInfoAttribute"/> on the plugin class.
/// </summary>
/// <param name="Id">Stable unique identifier, reverse-DNS style (e.g. <c>dev.example.MyMod</c>).</param>
/// <param name="Name">Display name.</param>
/// <param name="Version">SemVer 2.0 version string.</param>
public sealed record PluginInfo(string Id, string Name, string Version)
{
    /// <summary>Optional author name(s), comma separated.</summary>
    public string? Authors { get; init; }

    /// <summary>Human-readable description shown in <c>nami list</c>.</summary>
    public string? Description { get; init; }
}
