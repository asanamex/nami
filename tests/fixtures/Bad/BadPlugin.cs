using Nami.Sdk;

namespace Nami.Fixtures.Bad;

/// <summary>Fixture plugin that throws on every update - used to prove quarantine disables it without killing the game.</summary>
[NamiPlugin]
[PluginInfo("dev.nami.fixtures.bad", "Bad Fixture", "0.1.0", Description = "Test fixture: always throws on update.")]
public sealed class BadPlugin : NamiPlugin
{
    public override void OnLoad()
    {
        Context.Log.Info("bad loaded");
    }

    public override void OnUpdate() => throw new InvalidOperationException("bad plugin always fails");
}
