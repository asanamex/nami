using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nami.Core.Configuration;

/// <summary>
/// Loads and saves Nami's central <c>nami.json</c> configuration with a simple, permissive schema.
/// Unknown fields are NOT preserved: the loader has no <c>JsonExtensionData</c> member, so unknown
/// keys are dropped on save. The loader tolerates a missing or malformed file by using defaults.
/// Keys are written camelCase (and read case-insensitively), e.g. <c>enableMonoBridge</c>.
/// </summary>
public sealed class NamiConfig
{
    public const string FileName = "nami.json";

    /// <summary>Root directory Nami treats as its home (defaults to <c>&lt;game&gt;/nami</c>).</summary>
    public string RootPath { get; set; } = "";

    /// <summary>If true, a plugin that throws in its <see cref="Sdk.NamiPlugin.OnUpdate"/> repeatedly is auto-disabled.</summary>
    public bool QuarantineEnabled { get; set; } = true;

    /// <summary>Throws per frame tolerated before quarantine disables a plugin.</summary>
    public int QuarantineThreshold { get; set; } = 5;

    /// <summary>Log verbosity for the console sink.</summary>
    public string LogLevel { get; set; } = "Info";

    /// <summary>Glob of enabled plugin ids; empty means all discovered plugins load.</summary>
    public List<string> EnabledPlugins { get; set; } = new();

    /// <summary>
    /// EXPERIMENTAL: attempt to bridge into the game's native Mono runtime from our hosted
    /// CoreCLR (call UnityEngine.Debug.Log etc. through the embedding API). Defaults to false.
    /// Honest gate: <c>Boot.Run</c> propagates this into <c>Tide.SetBridgeEnabled</c>, so when
    /// false <c>Tide.IsAvailable</c> is false and every mod Tide op throws
    /// <c>TideException</c> with <c>Code=-3</c> instead of touching native (no AV possible
    /// by construction). The boot self-test runs only when this is true.
    /// </summary>
    public bool EnableMonoBridge { get; set; }

    /// <summary>Absolute path to the game executable (set by `nami launch set`; empty = auto-detect).</summary>
    public string? GameExe { get; set; }

    /// <summary>Steam app id giving `nami launch steam` its Steam context (written to steam_appid.txt next to the game).</summary>
    public string? SteamAppId { get; set; }

    /// <summary>Built-in per-plugin performance profiler (tick timings, Tide-op latency).</summary>
    public ProfilerConfig Profiler { get; set; } = new();

    /// <summary>Hot reload: watches the mods directory and reloads changed plugin DLLs into a new generation.</summary>
    public HotReloadConfig HotReload { get; set; } = new();

    /// <summary>
    /// Per-plugin configuration sections: <c>"pluginConfig": { "&lt;pluginId&gt;": { ... } }</c>.
    /// Each plugin sees its own section (camelCase-safe: plugin-id keys are preserved verbatim).
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? PluginConfig { get; set; }

    public static NamiConfig Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return new NamiConfig { RootPath = directory };
        }

        try
        {
            var config = JsonSerializer.Deserialize<NamiConfig>(File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                });
            if (config is null)
            {
                return new NamiConfig { RootPath = directory };
            }

            config.RootPath = directory;
            return config;
        }
        catch (JsonException)
        {
            // Corrupt config: fall back to defaults rather than refusing to boot.
            return new NamiConfig { RootPath = directory };
        }
    }

    public void Save()
    {
        var path = Path.Combine(RootPath, FileName);
        Directory.CreateDirectory(RootPath);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            // Never omit defaults: an explicitly-disabled flag (false) must survive a
            // save/load roundtrip, and a fully-populated file documents every key.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(path, json);
    }
}

/// <summary>Settings for the built-in per-plugin profiler.</summary>
public sealed class ProfilerConfig
{
    /// <summary>Measure OnUpdate durations per plugin and log periodic summaries.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between "profiler" log summaries (per-plugin ticks/avg/p95/max lines).</summary>
    public double SummaryIntervalSeconds { get; set; } = 30.0;
}

/// <summary>Settings for mod hot reload (file-watch based live reload of plugin DLLs).</summary>
public sealed class HotReloadConfig
{
    /// <summary>Master switch: enables the reload machinery (watching, queue, generations).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Start watching the mods directory automatically when the chainloader boots.</summary>
    public bool AutoWatch { get; set; } = true;

    /// <summary>Debounce window (ms) collapsing bursts of file events (a build) into one reload scan.</summary>
    public int DebounceMs { get; set; } = 500;
}
