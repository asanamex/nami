using Nami.Sdk;

namespace Nami.Samples;

[NamiPlugin]
[PluginInfo("dev.nami.samples.hello", "Hello Nami", "0.1.0", Description = "Proof-of-life sample: logs through the Nami runtime.")]
public sealed class HelloNamiPlugin : NamiPlugin
{
    private int _ticks;

    public override void OnLoad()
    {
        Context.Log.Info("HelloNami loaded inside the Nami CoreCLR runtime!");
    }

    public override void OnUpdate()
    {
        if (++_ticks % 120 == 0)
        {
            Context.Log.Info($"HelloNami update tick {_ticks} (running on .NET {Environment.Version})");
        }
    }
}
