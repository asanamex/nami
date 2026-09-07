using Nami;
using Nami.Sdk;

namespace NamiMod;

/// <summary>
/// Optional example of calling INTO the game through Tide. Every call runs on the game's
/// main thread. Requires the bridge to be enabled in nami.json: { "enableMonoBridge": true }
/// </summary>
public static class TideExample
{
    public static void TouchTheGame(ILog log)
    {
        if (!Tide.IsAvailable)
        {
            log.Info("Tide is not available (not running under Nami in a game) — skipping game access");
            return;
        }

        // UnityLog shows up in Unity's own Player.log.
        Tide.UnityLog("hello from a Nami mod via Tide");

        // Typed static access (generic Get<T>/Set<T> map the CLR type for you).
        var app = GameClass.Resolve("UnityEngine.CoreModule", "UnityEngine", "Application");
        bool runInBackground = app.Get<bool>("runInBackground");
        log.Info($"Application.runInBackground = {runInBackground}");
    }
}
