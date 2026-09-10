using System.Diagnostics;

// Player-facing shortcut for running the game with Nami injected, without the CLI open.
// launchNami.exe lives at <nami root>/launchNami.exe and derives the root from its own path
// (the loader sits at <root>/native/nami_loader.dll, the injector at <root>/native/nami_boot.exe).
//
// Usage:
//   launchNami            launch the game with Nami injected (offline)
//   launchNami steam      launch the game with Nami injected; ensures the Steam client is running first
//
// The game executable comes from nami.json ("gameExe"); nami.json lives at <root>/nami.json.

var namiRoot = Path.GetDirectoryName(Environment.ProcessPath)
               ?? throw new InvalidOperationException("cannot locate launchNami.exe");

var configPath = Path.Combine(namiRoot, "nami.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"[launchNami] nami.json not found next to launchNami.exe ({configPath})");
    return 1;
}

string gameExe;
string? steamAppId = null;
try
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
    var root = doc.RootElement;
    gameExe = root.TryGetProperty("gameExe", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.String
        ? g.GetString()!
        : throw new InvalidOperationException("nami.json has no gameExe — run `nami launch set <game>.exe` first");
    if (root.TryGetProperty("steamAppId", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String)
    {
        steamAppId = s.GetString();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[launchNami] could not read nami.json: {ex.Message}");
    return 1;
}

if (!File.Exists(gameExe))
{
    Console.Error.WriteLine($"[launchNami] game executable not found: {gameExe}");
    return 1;
}

var boot = Path.Combine(namiRoot, "native", "nami_boot.exe");
var loader = Path.Combine(namiRoot, "native", "nami_loader.dll");
if (!File.Exists(boot) || !File.Exists(loader))
{
    Console.Error.WriteLine("[launchNami] native injector/loader missing under " + Path.Combine(namiRoot, "native"));
    return 1;
}

var wantsSteam = args.Length > 0 && args[0].Equals("steam", StringComparison.OrdinalIgnoreCase);
if (wantsSteam)
{
    var gameDir = Path.GetDirectoryName(gameExe) ?? namiRoot;
    var appidFile = Path.Combine(gameDir, "steam_appid.txt");
    string? fileId = null;
    if (File.Exists(appidFile))
    {
        fileId = File.ReadAllText(appidFile).Trim();
        if (fileId.Length == 0) fileId = null;
    }

    var appId = steamAppId ?? fileId;
    if (appId is null)
    {
        Console.Error.WriteLine("[launchNami] no Steam app id — run `nami launch set --steam-id <appid>` first");
        return 1;
    }

    if (fileId != appId)
    {
        try
        {
            File.WriteAllText(appidFile, appId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[launchNami] could not write Steam app id to {appidFile}: {ex.Message}");
            return 1;
        }
    }

    if (Process.GetProcessesByName("steam").Length == 0)
    {
        Console.Error.WriteLine("[launchNami] Steam client not running — start Steam, then retry");
        return 1;
    }
}

var workingDir = Path.GetDirectoryName(gameExe) ?? namiRoot;
using (var p = Process.Start(new ProcessStartInfo
       {
           FileName = boot,
           Arguments = $"\"{gameExe}\" \"{loader}\"",
           WorkingDirectory = workingDir,
           UseShellExecute = false
       }))
{
    if (p is null)
    {
        Console.Error.WriteLine("[launchNami] failed to start nami_boot.exe");
        return 1;
    }

    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        Console.Error.WriteLine($"[launchNami] nami_boot exited with code {p.ExitCode} — check nami.log");
        return p.ExitCode;
    }
}

return 0;
