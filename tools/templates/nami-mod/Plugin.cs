using Nami.Sdk;

namespace NamiMod;

/// <summary>
/// Your Nami mod. The chainloader instantiates the class marked [NamiPlugin], calls OnLoad
/// once when the game starts, then OnUpdate roughly every 16 ms while the mod is active.
/// </summary>
[NamiPlugin]
[PluginInfo("PLUGIN_ID", "PLUGIN_NAME", "0.1.0", Description = "A Nami mod.")]
public sealed class MyMod : NamiPlugin
{
    private int _ticks;

    public override void OnLoad()
    {
        Context.Log.Info($"PLUGIN_NAME loaded on .NET {Environment.Version}");
    }

    public override void OnUpdate()
    {
        // Log every ~2 seconds (120 ticks x 16 ms).
        if (++_ticks % 120 == 0)
        {
            Context.Log.Info($"PLUGIN_NAME tick {_ticks}");
        }
    }
}
