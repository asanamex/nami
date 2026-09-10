namespace Nami.Core.Generations;

/// <summary>
/// The only visible active set: an immutable copy-on-write snapshot (version, generation
/// map, dispatch slot map, dependency graph). The Chainloader publishes a fully built and
/// validated candidate with a single <see cref="Interlocked.Exchange{T}(ref T, T)"/> root
/// replacement — never per-node sequential publishes — so readers always observe exactly
/// one generation graph; dispatch and queries read the reference once per operation.
/// </summary>
internal sealed record RuntimeSnapshot(
    long Version,
    IReadOnlyDictionary<string, ModGeneration> Generations,
    // DispatchSlot is owned by DispBuilder (Phase 3).
    IReadOnlyDictionary<string, DispatchSlot> DispatchSlots,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies);
