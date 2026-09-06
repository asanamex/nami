using Nami.Sdk;

namespace Nami.Fixtures.Beta;

[NamiPlugin]
[PluginInfo("dev.nami.fixtures.beta", "Beta Fixture", "2.0.0", Description = "Test fixture: depends on alpha.")]
[PluginDependency("dev.nami.fixtures.alpha")]
public sealed class BetaPlugin : NamiPlugin
{
    public override void OnLoad()
    {
        Context.Log.Info("beta loaded");
    }
}
