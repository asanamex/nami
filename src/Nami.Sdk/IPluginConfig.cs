namespace Nami.Sdk;

/// <summary>
/// Per-plugin configuration view: the mod's own section of <c>nami.json</c>
/// (<c>pluginConfig.&lt;pluginId&gt;</c>). Missing keys (or a missing section entirely) fall
/// back to the caller-supplied default; unknown types fall back too rather than throwing.
/// </summary>
public interface IPluginConfig
{
    /// <summary>All raw entries of this plugin's section (empty when absent).</summary>
    IReadOnlyDictionary<string, System.Text.Json.JsonElement> Entries { get; }

    /// <summary>Reads a string value, or <paramref name="fallback"/> when absent/not a string.</summary>
    string GetString(string key, string fallback);

    /// <summary>Reads an integer value, or <paramref name="fallback"/> when absent/not numeric.</summary>
    int GetInt(string key, int fallback);

    /// <summary>Reads a double value, or <paramref name="fallback"/> when absent/not numeric.</summary>
    double GetDouble(string key, double fallback);

    /// <summary>Reads a boolean value, or <paramref name="fallback"/> when absent/not boolean.</summary>
    bool GetBool(string key, bool fallback);

    /// <summary>True when the key exists in this plugin's section.</summary>
    bool Has(string key);
}

/// <summary>Empty config handed to plugins that have no <c>pluginConfig</c> section.</summary>
public sealed class NullPluginConfig : IPluginConfig
{
    public static readonly NullPluginConfig Instance = new();

    public IReadOnlyDictionary<string, System.Text.Json.JsonElement> Entries { get; } =
        new Dictionary<string, System.Text.Json.JsonElement>();

    public string GetString(string key, string fallback) => fallback;
    public int GetInt(string key, int fallback) => fallback;
    public double GetDouble(string key, double fallback) => fallback;
    public bool GetBool(string key, bool fallback) => fallback;
    public bool Has(string key) => false;
}
