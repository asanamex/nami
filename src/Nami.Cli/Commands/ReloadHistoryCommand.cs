using System.Text.Json;

namespace Nami.Cli.Commands;

/// <summary>
/// `nami reload-history &lt;mod&gt;` - render the `reload-history.json` ring
/// (`[{modId,from,to,success,error,durationMs,at}]`, last 50) filtered to one mod.
/// Missing file means no history was ever recorded (exit 1). Read-only.
/// </summary>
internal static class ReloadHistoryCommand
{
    public static int Run(string gameDir, string[] args)
    {
        if (args.Length != 1 || args[0].StartsWith('-'))
        {
            Console.Error.WriteLine("nami reload-history: pass exactly one <mod> id");
            Usage();
            return 1;
        }

        var modId = args[0];
        var root = NamiPaths.FindRoot(gameDir);
        if (root is null)
        {
            Console.Error.WriteLine($"no Nami install found in '{gameDir}' — run `nami install \"{gameDir}\"` first");
            return 1;
        }

        var path = Path.Combine(root, "reload-history.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("no reload history");
            return 1;
        }

        List<JsonElement> entries;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Console.Error.WriteLine($"nami reload-history: '{path}' is not a JSON array");
                return 1;
            }

            entries = doc.RootElement.EnumerateArray()
                .Where(e => e.TryGetProperty("modId", out var id) &&
                            id.GetString()?.Equals(modId, StringComparison.OrdinalIgnoreCase) == true)
                .Select(e => e.Clone())
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nami reload-history: cannot read '{path}': {ex.Message}");
            return 1;
        }

        if (entries.Count == 0)
        {
            Console.WriteLine($"no reload history for '{modId}'");
            return 0;
        }

        foreach (var e in entries)
        {
            var ok = e.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            var line = $"{Str(e, "at")} {Str(e, "modId")} {Str(e, "from")}->{Str(e, "to")} " +
                       $"{(ok ? "ok" : "FAIL")} {Str(e, "durationMs")}ms";
            if (!ok && e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            {
                line += $" {err.GetString()}";
            }

            Console.WriteLine(line);
        }

        return 0;
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "-";

    private static void Usage()
    {
        Console.WriteLine("""
            usage: nami reload-history <mod> [gameDir]

              <mod>              render recorded reloads for one mod id
            """);
    }
}
