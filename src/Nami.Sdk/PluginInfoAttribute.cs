namespace Nami.Sdk;

/// <summary>
/// Declares plugin metadata on a <see cref="NamiPlugin"/> class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluginInfoAttribute(string id, string name, string version) : Attribute
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Version { get; } = version;
    public string? Authors { get; init; }
    public string? Description { get; init; }
}
