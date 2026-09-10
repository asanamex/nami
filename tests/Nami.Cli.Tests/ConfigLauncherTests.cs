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
                  "GAMEEXE": "C:\\Games\\MyGame\\MyGame.exe",
                  "quarantineenabled": false,
                  "LOGLEVEL": "Debug"
                }
                """);

            var config = NamiConfig.Load(dir);
            Assert.Equal(@"C:\Games\MyGame\MyGame.exe", config.GameExe);
            Assert.False(config.QuarantineEnabled);
            Assert.Equal("Debug", config.LogLevel);
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
            Assert.True(config.QuarantineEnabled);
            Assert.Equal("Info", config.LogLevel);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OldConfig_WithSteamKeys_LoadsFine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, NamiConfig.FileName), """
                {
                  "gameExe": "C:\\Games\\MyGame\\MyGame.exe",
                  "steamAppId": "1234560",
                  "steamRelaySkipInjection": true,
                  "quarantineThreshold": 9
                }
                """);

            var config = NamiConfig.Load(dir);
            Assert.Equal(@"C:\Games\MyGame\MyGame.exe", config.GameExe);
            Assert.Equal("1234560", config.SteamAppId);
            Assert.Equal(9, config.QuarantineThreshold);
            Assert.True(config.QuarantineEnabled);
            Assert.Equal("Info", config.LogLevel);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SteamAppId_RoundTrips_OnSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = new NamiConfig { RootPath = dir, SteamAppId = "2386580" };
            config.Save();

            var reloaded = NamiConfig.Load(dir);
            Assert.Equal("2386580", reloaded.SteamAppId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }


}
