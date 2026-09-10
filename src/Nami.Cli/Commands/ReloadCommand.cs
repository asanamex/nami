using System.Text.Json;
using System.Text.Json.Serialization;
using Nami.Core;
using Nami.Core.Configuration;
using Nami.Core.Logging;
using Nami.Sdk;

namespace Nami.Cli.Commands;

/// <summary>
/// `nami reload &lt;modId|--all&gt; [--source &lt;dll&gt;]` - queue a live-reload request for the
/// running game. Writes `reload-requests/&lt;modId&gt;.request` (`{modId,sourceDll?}`, camelCase)
/// under the Nami root for the runtime watcher to enqueue at the next safe point.
/// `--source` stages replacement bytes over `mods/&lt;basename&gt;` first (plain overwrite
/// copy, no-lock semantics); the request then records the staged file name.
/// </summary>
internal static class ReloadCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record ReloadRequest(string ModId, string? SourceDll);

    public static int Run(string gameDir, string[] args)
    {
        string? modId = null;
        var all = false;
        string? source = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--all")
            {
                all = true;
            }
            else if (arg == "--source")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("nami reload: --source needs a <dll> path");
                    Usage();
                    return 1;
                }

                source = args[++i];
            }
            else if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"nami reload: unknown option '{arg}'");
                Usage();
                return 1;
            }
            else if (modId is null)
            {
                modId = arg;
            }
            else
            {
                Console.Error.WriteLine($"nami reload: unexpected argument '{arg}'");
                Usage();
                return 1;
            }
        }

        if ((modId is null) == !all)
        {
            Console.Error.WriteLine("nami reload: pass exactly one of <modId> or --all");
            Usage();
            return 1;
        }

        // One staged file cannot serve many mods: stamping a single stagedName into every
        // request would point each mod at the same bytes. Reject before any writes.
        if (all && source is not null)
        {
            Console.Error.WriteLine("nami reload: --source cannot be combined with --all (stage per mod instead)");
            Usage();
            return 1;
        }

        var root = NamiPaths.FindRoot(gameDir);
        if (root is null)
        {
            Console.Error.WriteLine($"no Nami install found in '{gameDir}' — run `nami install \"{gameDir}\"` first");
            return 1;
        }

        // Known ids come from the same manifest scan `nami list` uses (assembly manifests,
        // not DLL basenames), so validation agrees with what the runtime would load.
        var hub = new LogHub { MinimumLevel = LogLevel.Warn };
        var chainloader = new Chainloader(root, NamiConfig.Load(root), hub);
        chainloader.Initialize();
        var known = chainloader.DiscoverPlugins()
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> targets;
        if (all)
        {
            targets = known.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            if (targets.Count == 0)
            {
                Console.Error.WriteLine("nami reload: no mods installed");
                return 1;
            }
        }
        else
        {
            // Validate BEFORE staging any bytes: an unknown id writes nothing (no copy,
            // no request file).
            if (!known.Contains(modId!))
            {
                Console.Error.WriteLine($"nami reload: unknown mod '{modId}'");
                return 1;
            }

            targets = new List<string> { known.First(id => id.Equals(modId, StringComparison.OrdinalIgnoreCase)) };
        }

        // Stage replacement bytes (if any) before writing requests, so a failed copy
        // never leaves a request pointing at un-staged content.
        string? stagedName = null;
        if (source is not null)
        {
            if (!File.Exists(source))
            {
                Console.Error.WriteLine($"nami reload: source not found: '{source}'");
                return 1;
            }

            var modsDir = Path.Combine(root, "mods");
            Directory.CreateDirectory(modsDir);
            stagedName = Path.GetFileName(source);
            try
            {
                File.Copy(source, Path.Combine(modsDir, stagedName), overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"nami reload: failed to stage '{source}': {ex.Message}");
                return 1;
            }
        }

        var requestsDir = Path.Combine(root, "reload-requests");
        Directory.CreateDirectory(requestsDir);
        try
        {
            foreach (var id in targets)
            {
                var payload = JsonSerializer.Serialize(new ReloadRequest(id, stagedName), JsonOptions);
                File.WriteAllText(Path.Combine(requestsDir, id + ".request"), payload);
                Console.WriteLine($"reload queued for '{id}' -> reload-requests/{id}.request");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nami reload: failed to write request: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static void Usage()
    {
        Console.WriteLine("""
            usage: nami reload <modId|--all> [--source <dll>] [gameDir]

              <modId>            queue a reload request for one installed mod
              --all              queue one request per installed mod
              --source <dll>     stage replacement bytes over mods/<basename> first
            """);
    }
}
