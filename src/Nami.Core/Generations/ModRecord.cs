namespace Nami.Core.Generations;

/// <summary>Host-owned record for one logical mod: its current generation plus draining predecessors.</summary>
internal sealed class ModRecord
{
    internal ModRecord(string id)
    {
        Id = id;
        Retiring = new List<ModGeneration>();
    }

    public string Id { get; }
    public ModGeneration? Current { get; internal set; }
    public List<ModGeneration> Retiring { get; }

    /// <summary>Host-owned JSON state for this mod (owned by ResBuilder, Phase 4).</summary>
    public ModStateStore State { get; internal set; } = null!;

    public int ReloadCount { get; internal set; }
    public long PendingSequence { get; internal set; }

    /// <summary>Uncommitted prepare transaction, if any (owned by the integrator, Phase 5).</summary>
    public ReloadTransaction? Active { get; internal set; }
}
