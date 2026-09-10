namespace Nami.Core.Generations;

/// <summary>
/// Host-owned tracking for one retired generation. The <see cref="WeakReference"/> lives
/// OUTSIDE the measured object: a WeakReference field on the generation itself would join
/// the graph under observation and root the ALC being collected.
/// </summary>
internal sealed record RetirementRecord(
    string ModId,
    int GenerationId,
    WeakReference AlcTracker,
    // RetirementReport is owned by ResBuilder (Phase 4).
    RetirementReport? Outcome);
