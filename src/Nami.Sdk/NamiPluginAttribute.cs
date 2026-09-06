namespace Nami.Sdk;

/// <summary>
/// Marks a class as the entry point of a Nami plugin.
/// The type must derive from <see cref="NamiPlugin"/> and be public with a public parameterless constructor.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class NamiPluginAttribute : Attribute;
