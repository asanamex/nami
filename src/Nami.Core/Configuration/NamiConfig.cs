using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nami.Core.Configuration;

/// <summary>
/// Loads and saves Nami's central <c>nami.json</c> configuration with a simple, permissive schema.
/// Unknown fields are preserved on save. The loader tolerates a missing or malformed file by using defaults.
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
    /// CoreCLR (call UnityEngine.Debug.Log etc. through the embedding API). Defaults to false:
    /// the CoreCLR↔Mono GC interop is not yet stable, and a crash here would take the game down.
    /// </summary>
    public bool EnableMonoBridge { get; set; }

    /// <summary>Absolute path to the game executable (set by `nami launch set`; empty = auto-detect).</summary>
    public string? GameExe { get; set; }

    /// <summary>Steam app id used by `nami launch steam` to relay to a clean Steam session (optional).</summary>
    public string? SteamAppId { get; set; }

    /// <summary>
    /// When true, `nami launch steam` (with a SteamAppId set) launches the game directly WITHOUT
    /// Nami injection, then relaunches through Steam after it exits — for online/anti-cheat games.
    /// </summary>
    public bool SteamRelaySkipInjection { get; set; }

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
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(path, json);
    }
}
