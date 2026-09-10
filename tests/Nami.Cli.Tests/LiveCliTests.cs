using System.Text.Json;
using Nami.Cli.Commands;
using Nami.Core.Configuration;

namespace Nami.Cli.Tests;

/// <summary>
/// Live-runtime CLI proofs: `nami reload` validates before writing anything
/// (unknown id exits 1 with no request file and no staged bytes), request
/// files keep their exact camelCase shape, and the read-only renderers report
/// a missing runtime instead of inventing one.
/// </summary>
public sealed class LiveCliTests : IDisposable
{
    private const string AlphaId = "dev.nami.fixtures.alpha";

    private static readonly Lock ConsoleLock = new();

    private readonly string _root;

    public LiveCliTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        new NamiConfig { RootPath = _root }.Save();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; Windows may hold file locks briefly.
        }
    }

    private string ModsDir
    {
        get
        {
            var dir = Path.Combine(_root, "mods");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private void InstallAlpha()
    {
        var outputDir = AppContext.BaseDirectory;
        File.Copy(
            Path.Combine(outputDir, "AlphaPlugin.dll"),
            Path.Combine(ModsDir, "AlphaPlugin.dll"),
            overwrite: true);
        File.Copy(
            Path.Combine(outputDir, "Nami.Sdk.dll"),
            Path.Combine(ModsDir, "Nami.Sdk.dll"),
            overwrite: true);
    }

    private string RequestPath(string modId) => Path.Combine(_root, "reload-requests", modId + ".request");

    private static string CaptureOut(Func<int> run, out int exit)
    {
        lock (ConsoleLock)
        {
            var previous = Console.Out;
            var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                exit = run();
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(previous);
            }
        }
    }

    [Fact]
    public void Reload_UnknownId_Exit1_NothingWritten()
    {
        InstallAlpha();

        var exit = ReloadCommand.Run(_root, new[] { "dev.nami.fixtures.does-not-exist" });

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(Path.Combine(_root, "reload-requests")));
        Assert.Equal(
            new[] { "AlphaPlugin.dll", "Nami.Sdk.dll" },
            Directory.GetFiles(ModsDir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Reload_KnownId_WritesExactCamelCaseRequest()
    {
        InstallAlpha();

        var exit = ReloadCommand.Run(_root, new[] { AlphaId });

        Assert.Equal(0, exit);
        var payload = File.ReadAllText(RequestPath(AlphaId));
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(
            new[] { "modId" },
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(AlphaId, document.RootElement.GetProperty("modId").GetString());
    }

    [Fact]
    public void Reload_Source_StagesBytes_BeforeRecordingRequest()
    {
        InstallAlpha();
        var sourceDir = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "staged.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "AlphaPlugin.dll"), source);

        var exit = ReloadCommand.Run(_root, new[] { AlphaId, "--source", source });

        Assert.Equal(0, exit);
        var staged = Path.Combine(ModsDir, "staged.dll");
        Assert.True(File.Exists(staged));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(staged));

        using var document = JsonDocument.Parse(File.ReadAllText(RequestPath(AlphaId)));
        Assert.Equal(
            new[] { "modId", "sourceDll" },
            document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal("staged.dll", document.RootElement.GetProperty("sourceDll").GetString());
    }

    [Fact]
    public void Reload_SourceMissing_Exit1_NoRequest()
    {
        InstallAlpha();

        var exit = ReloadCommand.Run(_root, new[] { AlphaId, "--source", Path.Combine(_root, "missing.dll") });

        Assert.Equal(1, exit);
        Assert.False(File.Exists(RequestPath(AlphaId)));
    }

    [Fact]
    public void Reload_All_WritesOneRequestPerInstalledMod()
    {
        InstallAlpha();

        var exit = ReloadCommand.Run(_root, new[] { "--all" });

        Assert.Equal(0, exit);
        Assert.True(File.Exists(RequestPath(AlphaId)));
    }

    [Fact]
    public void Reload_SourceWithAll_Exit1_NoWrites()
    {
        InstallAlpha();
        var sourceDir = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "staged.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "AlphaPlugin.dll"), source);

        var exit = ReloadCommand.Run(_root, new[] { "--all", "--source", source });

        // One staged file cannot serve many mods: rejected before any staging or requests.
        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(Path.Combine(_root, "reload-requests")));
        Assert.False(File.Exists(Path.Combine(_root, "mods", "staged.dll")));
    }

    [Fact]
    public void Reload_NoRoot_Exit1()
    {
        var bare = Path.Combine(Path.GetTempPath(), "nami-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bare);
        try
        {
            Assert.Equal(1, ReloadCommand.Run(bare, new[] { AlphaId }));
        }
        finally
        {
            Directory.Delete(bare, recursive: true);
        }
    }

    [Fact]
    public void Status_MissingFile_Exit1()
    {
        Assert.Equal(1, StatusCommand.Run(_root, Array.Empty<string>()));
    }

    [Fact]
    public void Status_RendersTable_AndJsonPassthrough()
    {
        File.WriteAllText(Path.Combine(_root, "status.json"), """
            {"version":7,"updatedAt":"2026-09-10T00:00:00Z",
             "mods":[{"id":"dev.nami.fixtures.alpha","current":2,"lifetime":"Running",
               "health":"healthy","activeExecutions":0,"tasks":0,"timers":1,"subscriptions":0,
               "waveBindings":0,"reloadCount":1,
               "lastReload":{"success":true,"durationMs":12,"at":"2026-09-10T00:00:00Z"},
               "alc":"nami-plugin-alpha"}],
             "retiring":[]}
            """);

        var table = CaptureOut(() => StatusCommand.Run(_root, Array.Empty<string>()), out var tableExit);
        Assert.Equal(0, tableExit);
        Assert.Contains(AlphaId, table);

        var json = CaptureOut(() => StatusCommand.Run(_root, new[] { "--json" }), out var jsonExit);
        Assert.Equal(0, jsonExit);
        Assert.Contains(AlphaId, json);
    }

    [Fact]
    public void ReloadHistory_MissingFile_Exit1()
    {
        Assert.Equal(1, ReloadHistoryCommand.Run(_root, new[] { AlphaId }));
    }

    [Fact]
    public void ReloadHistory_MalformedFile_Exit1()
    {
        File.WriteAllText(Path.Combine(_root, "reload-history.json"), "{oops");

        Assert.Equal(1, ReloadHistoryCommand.Run(_root, new[] { AlphaId }));
    }

    [Fact]
    public void ReloadHistory_RendersFilteredRing()
    {
        File.WriteAllText(Path.Combine(_root, "reload-history.json"), """
            [{"modId":"dev.nami.fixtures.alpha","from":1,"to":2,"success":true,"error":null,
              "durationMs":12,"at":"2026-09-10T00:00:00Z"},
             {"modId":"dev.nami.fixtures.beta","from":1,"to":0,"success":false,"error":"boom",
              "durationMs":3,"at":"2026-09-10T00:01:00Z"}]
            """);

        var output = CaptureOut(() => ReloadHistoryCommand.Run(_root, new[] { AlphaId }), out var exit);

        Assert.Equal(0, exit);
        Assert.Contains(AlphaId, output);
        Assert.Contains("1->2", output);
    }
}
