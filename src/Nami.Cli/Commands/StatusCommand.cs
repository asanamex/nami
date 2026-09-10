using System.Text.Json;

namespace Nami.Cli.Commands;

/// <summary>
/// `nami status [--json]` - render the live runtime's `status.json`
/// (`{version,updatedAt,mods:[{id,current,lifetime,health,activeExecutions,tasks,timers,
/// subscriptions,waveBindings,reloadCount,lastReload:{success,durationMs,at},alc}],
/// retiring:[{modId,generation,lifetime,blockers:[]}]}`). `--json` prints the file raw,
/// otherwise a human table is rendered. Missing file means no live runtime (exit 1).
/// This command only reads; the runtime (integrator-owned writer) produces the file.
/// </summary>
internal static class StatusCommand
{
    public static int Run(string gameDir, string[] args)
    {
        var json = false;
        foreach (var arg in args)
        {
            if (arg == "--json")
            {
                json = true;
            }
            else
            {
                Console.Error.WriteLine($"nami status: unexpected argument '{arg}'");
                Usage();
                return 1;
            }
        }

        var root = NamiPaths.FindRoot(gameDir);
        var path = root is null ? null : Path.Combine(root, "status.json");
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("no live runtime");
            return 1;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nami status: cannot read '{path}': {ex.Message}");
            return 1;
        }

        if (json)
        {
            Console.WriteLine(text);
            return 0;
        }

        try
        {
            Render(text);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nami status: cannot parse '{path}': {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static void Render(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Console.WriteLine($"runtime version {Str(root, "version")} updated {Str(root, "updatedAt")}");

        if (root.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"{"ID",-40} {"GEN",-5} {"LIFETIME",-12} {"HEALTH",-10} {"EXEC",-5} {"TASKS",-6} {"TIMERS",-7} {"SUBS",-5} {"BIND",-5} {"RELOADS",-8} LAST");
            foreach (var m in mods.EnumerateArray())
            {
                var last = m.TryGetProperty("lastReload", out var lr) && lr.ValueKind == JsonValueKind.Object
                    ? $"{(lr.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True ? "ok" : "FAIL")} {Str(lr, "durationMs")}ms {Str(lr, "at")}"
                    : "-";
                Console.WriteLine($"{Str(m, "id"),-40} {Str(m, "current"),-5} {Str(m, "lifetime"),-12} {Str(m, "health"),-10} " +
                                  $"{Str(m, "activeExecutions"),-5} {Str(m, "tasks"),-6} {Str(m, "timers"),-7} " +
                                  $"{Str(m, "subscriptions"),-5} {Str(m, "waveBindings"),-5} {Str(m, "reloadCount"),-8} {last}");
            }
        }

        if (root.TryGetProperty("retiring", out var retiring) && retiring.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in retiring.EnumerateArray())
            {
                var blockers = r.TryGetProperty("blockers", out var b) && b.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", b.EnumerateArray().Select(e => e.ToString()))
                    : "";
                Console.WriteLine($"retiring: {Str(r, "modId")}#{Str(r, "generation")} {Str(r, "lifetime")} blockers: [{blockers}]");
            }
        }
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "-";

    private static void Usage()
    {
        Console.WriteLine("""
            usage: nami status [--json] [gameDir]

              (no args)          render the live runtime's status.json as a table
              --json             print status.json raw
            """);
    }
}
