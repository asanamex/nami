using Nami.Core.Plugins;

namespace Nami.Tests;

public class DependencyResolverTests
{
    private static PluginManifest Plugin(string id, string[]? deps = null, string[]? incompat = null) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        AssemblyPath = $@"C:\mods\{id}.dll",
        Dependencies = deps ?? Array.Empty<string>(),
        Incompatibilities = incompat ?? Array.Empty<string>()
    };

    [Fact]
    public void Resolve_SortsDependenciesBeforeDependents()
    {
        var result = DependencyResolver.Resolve(new[]
        {
            Plugin("gamma", deps: new[] { "beta" }),
            Plugin("alpha"),
            Plugin("beta", deps: new[] { "alpha" })
        });

        var ids = result.LoadOrder.Select(m => m.Id).ToArray();
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, ids);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Resolve_SkipsPluginWithMissingDependency()
    {
        var result = DependencyResolver.Resolve(new[]
        {
            Plugin("needy", deps: new[] { "missing" }),
            Plugin("alpha")
        });

        Assert.Equal(new[] { "alpha" }, result.LoadOrder.Select(m => m.Id));
        Assert.True(result.Skipped.ContainsKey("needy"));
    }

    [Fact]
    public void Resolve_DisablesOneOfAnIncompatiblePair()
    {
        var result = DependencyResolver.Resolve(new[]
        {
            Plugin("a"),
            Plugin("b", incompat: new[] { "a" })
        });

        Assert.Single(result.LoadOrder);
        Assert.Single(result.Skipped);
        Assert.Contains(result.Skipped.Values, v => v.Contains("incompatible", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_DetectsDependencyCycle()
    {
        var result = DependencyResolver.Resolve(new[]
        {
            Plugin("x", deps: new[] { "y" }),
            Plugin("y", deps: new[] { "x" })
        });

        Assert.Empty(result.LoadOrder);
        Assert.Equal(2, result.Skipped.Count);
        Assert.All(result.Skipped.Values, v => Assert.Contains("cycle", v));
    }

    [Fact]
    public void Resolve_KeepsOnlyFirstDuplicate()
    {
        var result = DependencyResolver.Resolve(new[]
        {
            Plugin("dup"),
            Plugin("dup")
        });

        Assert.Single(result.LoadOrder);
        Assert.True(result.Skipped.ContainsKey("dup"));
    }

    [Fact]
    public void Resolve_IsDeterministic()
    {
        var manifests = new[]
        {
            Plugin("zeta"),
            Plugin("alpha", deps: new[] { "zeta" }),
            Plugin("mike"),
            Plugin("bravo", deps: new[] { "alpha" })
        };

        var first = DependencyResolver.Resolve(manifests).LoadOrder.Select(m => m.Id).ToArray();
        var second = DependencyResolver.Resolve(manifests).LoadOrder.Select(m => m.Id).ToArray();
        Assert.Equal(first, second);
    }
}
