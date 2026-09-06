using Nami.Sdk;

namespace Nami.Fixtures.Alpha;

[NamiPlugin]
[PluginInfo("dev.nami.fixtures.alpha", "Alpha Fixture", "1.0.0", Description = "Test fixture: no dependencies.")]
public sealed class AlphaPlugin : NamiPlugin
{
    public override void OnLoad()
    {
        Context.Log.Info("alpha loaded");
    }
}
