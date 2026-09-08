using Nami.Core.Configuration;

namespace Nami.Cli.Tests;

public sealed class NamiConfigLauncherTests
{
    [Fact]
    public void NewKeys_RoundTrip_AndAreCaseInsensitive()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, NamiConfig.FileName), """
                {
                  "gameexe": "C:\\Games\\MyGame\\MyGame.exe",
                  "STEAMAPPID": "1234560",
                  "steamRelaySkipInjection": true
                }
                """);

            var config = NamiConfig.Load(dir);
            Assert.Equal(@"C:\Games\MyGame\MyGame.exe", config.GameExe);
            Assert.Equal("1234560", config.SteamAppId);
            Assert.True(config.SteamRelaySkipInjection);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExplicitFalse_RoundTrips_OnSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // An explicitly-disabled flag must survive save/load (WhenWritingDefault
            // would silently drop `false` and re-enable it on next load).
            var config = new NamiConfig { RootPath = dir, QuarantineEnabled = false };
            config.Save();

            var reloaded = NamiConfig.Load(dir);
            Assert.False(reloaded.QuarantineEnabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExistingConfig_WithoutNewKeys_LoadsFine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, NamiConfig.FileName), """{ "quarantineThreshold": 9 }""");
            var config = NamiConfig.Load(dir);
            Assert.Equal(9, config.QuarantineThreshold);
            Assert.Null(config.GameExe);
            Assert.Null(config.SteamAppId);
            Assert.False(config.SteamRelaySkipInjection);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
