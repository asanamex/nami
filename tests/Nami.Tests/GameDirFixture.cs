using Nami.Core.Configuration;

namespace Nami.Tests;

/// <summary>Builds a temporary fake game directory and exposes its Nami layout.</summary>
public sealed class GameDirFixture : IDisposable
{
    public string Root { get; }

    public GameDirFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "nami-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string ModsDir => Path.Combine(Root, "mods");

    /// <summary>Writes a nami.json with the given overrides.</summary>
    public void WriteConfig(bool quarantineEnabled = true, int threshold = 3,
        List<string>? enabledPlugins = null)
    {
        var config = new NamiConfig
        {
            RootPath = Root,
            QuarantineEnabled = quarantineEnabled,
            QuarantineThreshold = threshold,
            EnabledPlugins = enabledPlugins ?? new List<string>()
        };
        config.Save();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; Windows may hold file locks briefly.
        }
    }
}
