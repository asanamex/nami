using System.Reflection;
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
        if (Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var level))
        {
            hub.MinimumLevel = level;
        }
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

        // Pre-load every Nami.* runtime assembly the loader ships so plugins resolve them as
        // shared (the plugin ALC's shared branch reuses an already-loaded copy from any
        // context). Tide is loaded implicitly by the self-test above; Wave is referenced by
        // the runtime but never touched, so it must be loaded explicitly here — otherwise a
        // plugin referencing Nami.Wave gets a FileNotFoundException at OnLoad.
        foreach (var sharedName in new[] { "Nami.Core", "Nami.Sdk", "Nami.Tide", "Nami.Wave" })
        {
            try
            {
                _ = Assembly.Load(sharedName);
            }
            catch (Exception ex)
            {
                hub.Log("boot", LogLevel.Error, $"failed to preload {sharedName}: {ex.Message}");
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

            // Boot-guard: the native boot phase ends here. The loader wrote <root>/boot-pending
            // at boot start; deleting it means a crash from this point on is a runtime crash,
            // not a boot crash — only the latter marks the next boot safe (see native/core/
            // bootguard.h). Any fault before this line (inex arm, CoreCLR hosting, chainloader
            // load, the Tide self-test) auto-enables safe mode for the next boot.
            try
            {
                File.Delete(Path.Combine(namiRoot, "boot-pending"));
            }
            catch
            {
                // Best-effort: a leftover marker only costs one safe-mode boot.
            }

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
