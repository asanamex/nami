namespace Nami.Core.Plugins;

/// <summary>Outcome of resolving a plugin graph.</summary>
/// <param name="LoadOrder">Plugins to load, ordered so dependencies precede dependents.</param>
/// <param name="Skipped">Plugins excluded from loading, with reasons.</param>
public sealed record ResolutionResult(
    IReadOnlyList<PluginManifest> LoadOrder,
    IReadOnlyDictionary<string, string> Skipped);

/// <summary>
/// Computes a deterministic load order from discovered plugins: prunes incompatibilities,
/// checks dependencies, and topologically sorts so dependencies load first.
/// The result is pure — no loading happens here — which makes it easy to unit test.
/// </summary>
public static class DependencyResolver
{
    public static ResolutionResult Resolve(IReadOnlyList<PluginManifest> manifests)
    {
        var byId = manifests
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Prune duplicates (keep first by discovery order).
        foreach (var group in manifests.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dup in group.Skip(1))
            {
                skipped[dup.Id] = $"duplicate id '{dup.Id}' (kept {group.First().AssemblyPath})";
            }
        }

        // 2. Prune incompatible pairs: disable the one that loses (alphabetical tiebreak) with a warning.
        var active = byId.Values.ToHashSet();
        foreach (var a in manifests)
        {
            if (!active.Contains(a))
            {
                continue;
            }

            foreach (var otherId in a.Incompatibilities)
            {
                if (!byId.TryGetValue(otherId, out var other) || !active.Contains(other))
                {
                    continue;
                }

                var (disabled, reason) = DisablePair(a, other);
                active.Remove(disabled);
                skipped[disabled.Id] = reason;
            }
        }

        // 3. Verify dependencies resolve to active plugins with satisfying versions.
        foreach (var m in active.ToList())
        {
            foreach (var dep in m.Dependencies)
            {
                if (!byId.TryGetValue(dep.Id, out var provider))
                {
                    active.Remove(m);
                    skipped[m.Id] = $"dependency '{dep.Id}' not found";
                    break;
                }

                if (!active.Contains(provider))
                {
                    active.Remove(m);
                    skipped[m.Id] = $"dependency '{dep.Id}' was skipped ({skipped.GetValueOrDefault(dep.Id, "inactive")})";
                    break;
                }

                if (!VersionSatisfies(provider.Version, dep.MinimumVersion))
                {
                    active.Remove(m);
                    skipped[m.Id] = $"dependency '{dep.Id}' needs v{dep.MinimumVersion} (found {provider.Version})";
                    break;
                }
            }
        }

        // 4. Topological sort (Kahn) with stable ordering for determinism.
        var order = new List<PluginManifest>();
        var remaining = active
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var placed = new HashSet<PluginManifest>();

        while (remaining.Count > 0)
        {
            var progress = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var candidate = remaining[i];
                var depsSatisfied = candidate.Dependencies.All(dep =>
                    byId.TryGetValue(dep.Id, out var provider) && placed.Contains(provider));
                if (!depsSatisfied)
                {
                    continue;
                }

                order.Add(candidate);
                placed.Add(candidate);
                remaining.RemoveAt(i);
                progress = true;
                break;
            }

            if (!progress)
            {
                // Remaining plugins are involved in a dependency cycle.
                foreach (var m in remaining)
                {
                    skipped[m.Id] = "dependency cycle detected";
                }

                break;
            }
        }

        return new ResolutionResult(order, skipped);
    }

    /// <summary>
    /// True when <paramref name="providerVersion"/> satisfies <paramref name="minimum"/>:
    /// SemVer-style numeric compare when both parse as <see cref="Version"/>, exact match
    /// otherwise (covers prerelease tags <see cref="Version"/> cannot parse).
    /// </summary>
    internal static bool VersionSatisfies(string providerVersion, string? minimum)
    {
        if (string.IsNullOrWhiteSpace(minimum))
        {
            return true;
        }

        if (Version.TryParse(providerVersion, out var have) && Version.TryParse(minimum, out var need))
        {
            return have >= need;
        }

        return string.Equals(providerVersion, minimum, StringComparison.Ordinal);
    }

    private static (PluginManifest Disabled, string Reason) DisablePair(PluginManifest a, PluginManifest b)
    {
        // Deterministic loser: prefer keeping the plugin that declares fewer incompatibilities,
        // tiebreak by id.
        var keepA = (a.Incompatibilities.Count, a.Id).CompareTo((b.Incompatibilities.Count, b.Id)) <= 0;
        return keepA
            ? (b, $"incompatible with '{a.Id}'")
            : (a, $"incompatible with '{b.Id}'");
    }
}
