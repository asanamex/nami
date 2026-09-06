using Nami.Sdk;

namespace Nami.Fixtures.Gamma;

[NamiPlugin]
[PluginInfo("dev.nami.fixtures.gamma", "Gamma Fixture", "1.0.0", Description = "Test fixture: depends on beta (transitively alpha).")]
[PluginDependency("dev.nami.fixtures.beta")]
public sealed class GammaPlugin : NamiPlugin
{
    public override void OnLoad()
    {
        Context.Log.Info("gamma loaded");
    }
}
