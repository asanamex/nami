using System.Runtime.InteropServices;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Sdk;
using Tide = Nami.Tide;

namespace Nami.Runtime;

/// <summary>
/// Managed bootstrap of the Nami runtime inside a game process.
///
/// This is the piece BepInEx does not have on Mono: plugins do NOT run on the game's
/// ancient embedded Mono. The native core first hosts a modern .NET (CoreCLR) inside the
/// game process, then calls <see cref="Boot.Run"/> here — and from here Tide reaches BACK
/// into the game's Mono runtime (calls execute on the game's main thread), so mods can
/// touch the game.
/// </summary>
public static class Boot
{
    /// <summary>Called by the native core once CoreCLR is up. Never returns.</summary>
    public static void Run(string namiRoot, IntPtr monoModule)
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Info };
        var logPath = Path.Combine(namiRoot, "nami.log");
        using var fileSink = new FileSink(logPath);
        hub.AddSink(fileSink);

        hub.Log("boot", LogLevel.Info, $"Nami managed runtime booting (nami_root={namiRoot})");
        hub.Log("boot", LogLevel.Info, $"clr={Environment.Version} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture}");

        // Cross-runtime bridge: OPT-IN. Tide connects mods to the game's Mono runtime.
        // A native crash here cannot be caught by managed code, so it stays behind the
        // explicit `enableMonoBridge` flag until it is proven stable on more games.
        var config = NamiConfig.Load(namiRoot);
        if (config.EnableMonoBridge)
        {
            hub.Log("boot", LogLevel.Info, "attaching Tide bridge...");
            try
            {
                if (Tide.IsAvailable)
                {
                    var ok = Tide.UnityLog("hello from Nami's .NET runtime via Tide");
                    hub.Log("boot", LogLevel.Info, ok
                        ? "Tide bridge OK: Unity Debug.Log executed on the game main thread"
                        : "Tide bridge present but UnityLog failed");
                }
                else
                {
                    hub.Log("boot", LogLevel.Error, "Tide unavailable: nami_loader not loaded");
                }
            }
            catch (Exception ex)
            {
                hub.Log("boot", LogLevel.Error, $"tide failed: {ex}");
            }
        }

        // Load the user's mods through the isolated chainloader.
        try
        {
            var chainloader = new Chainloader(namiRoot, config, hub);
            var loaded = chainloader.LoadAll();
            hub.Log("boot", LogLevel.Info, $"chainloader activated: {loaded.Count} plugin(s) loaded");

            // Live reload: watch the mods directory so rebuilt/edited mods swap into a new
            // generation without restarting the game (opt out via hotReload.enabled=false).
            chainloader.StartHotReload();

            // Spin: keep the managed runtime (and any plugin update loops) alive on this thread.
            while (true)
            {
                chainloader.UpdateAll();
                Thread.Sleep(16);
            }
        }
        catch (Exception ex)
        {
            hub.Log("boot", LogLevel.Fatal, $"chainloader failed: {ex}");
            throw;
        }
    }
}
