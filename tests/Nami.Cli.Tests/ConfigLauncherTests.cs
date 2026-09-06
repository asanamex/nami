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
    public void Defaults_OmitNewKeys_OnSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = new NamiConfig { RootPath = dir };
            config.Save();

            var json = File.ReadAllText(Path.Combine(dir, NamiConfig.FileName));
            Assert.DoesNotContain("gameExe", json);
            Assert.DoesNotContain("steamAppId", json);
            Assert.DoesNotContain("steamRelaySkipInjection", json);
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
