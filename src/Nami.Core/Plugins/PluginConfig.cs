using System.Text.Json;
using Nami.Sdk;

namespace Nami.Core.Plugins;

/// <summary>
/// <see cref="IPluginConfig"/> backed by the plugin's <c>pluginConfig.&lt;id&gt;</c> section of
/// <c>nami.json</c>. All getters are fall-back semantics: a missing key, wrong JSON type, or a
/// missing section returns the supplied default instead of throwing (player-edited JSON must
/// never take a mod or the game down).
/// </summary>
public sealed class JsonPluginConfig : IPluginConfig
{
    private readonly Dictionary<string, JsonElement> _entries;

    public static readonly JsonPluginConfig Empty = new(new Dictionary<string, JsonElement>());

    public JsonPluginConfig(Dictionary<string, JsonElement> entries) => _entries = entries;

    public IReadOnlyDictionary<string, JsonElement> Entries => _entries;

    public bool Has(string key) => _entries.ContainsKey(key);

    public string GetString(string key, string fallback) =>
        _entries.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    public int GetInt(string key, int fallback)
    {
        if (_entries.TryGetValue(key, out var v) &&
            (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)))
        {
            return i;
        }

        return fallback;
    }

    public double GetDouble(string key, double fallback) =>
        _entries.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : fallback;

    public bool GetBool(string key, bool fallback) =>
        _entries.TryGetValue(key, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;
}
