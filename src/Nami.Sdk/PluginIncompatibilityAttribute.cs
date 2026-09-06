namespace Nami.Sdk;

/// <summary>
/// Declares a plugin that must NOT be loaded alongside this one (by its <see cref="PluginInfoAttribute.Id"/>).
/// If both are present, one of them is skipped with a warning.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class PluginIncompatibilityAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}
