using System.Runtime.InteropServices;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Runtime;

/// <summary>
/// Managed bootstrap of the Nami runtime inside a game process.
///
/// This is the piece BepInEx does not have on Mono: plugins do NOT run on the game's
/// ancient embedded Mono. The native core first hosts a modern .NET (CoreCLR) inside the
/// game process, then calls <see cref="Boot.Run"/> here — and from here we reach BACK into
/// the game's Mono runtime through raw embedding-API function pointers
/// (<see cref="MonoBridge"/>), proving a true cross-runtime bridge.
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

        // Cross-runtime bridge: OPT-IN (experimental). The CoreCLR↔Mono GC interop is not yet
        // stable — a native crash here cannot be caught by managed code, so it is disabled
        // unless the user explicitly enables it in nami.json.
        var config = NamiConfig.Load(namiRoot);
        if (config.EnableMonoBridge)
        {
            hub.Log("boot", LogLevel.Info, "attaching mono bridge...");
            try
            {
                if (MonoBridge.Attach(monoModule, hub))
                {
                    MonoBridge.Log("hello from the Nami CoreCLR runtime via the Mono bridge");
                }
            }
            catch (Exception ex)
            {
                hub.Log("boot", LogLevel.Error, $"mono bridge attach failed: {ex}");
            }
        }

        // Load the user's mods through the isolated chainloader.
        try
        {
            var chainloader = new Chainloader(namiRoot, config, hub);
            var loaded = chainloader.LoadAll();
            hub.Log("boot", LogLevel.Info, $"chainloader activated: {loaded.Count} plugin(s) loaded");

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
