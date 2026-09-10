namespace Nami.Core.Generations;

/// <summary>
/// Host-owned lifetime of a single mod generation. A generation moves forward along
/// Discovered → Preparing → Loaded → Starting → Running → Retiring → Quiescing →
/// Reclaiming → UnloadRequested → Collected; Running may divert to Quarantined; any
/// non-terminal state may move to Failed; Quiescing may park at RetirementBlocked
/// (drain blocked) and return to Quiescing once resolved. Terminal states (Collected,
/// Failed, Quarantined) exit only via record removal in a new transaction.
/// </summary>
public enum LifetimeState
{
    Discovered,
    Preparing,
    Loaded,
    Starting,
    Running,
    Retiring,
    Quiescing,
    Reclaiming,
    UnloadRequested,
    Collected,
    Failed,
    Quarantined,
    RetirementBlocked
}

/// <summary>Validates <see cref="LifetimeState"/> edges. Used by the reload coordinator (integrator).</summary>
internal static class LifetimeTransitions
{
    internal static bool IsTerminal(this LifetimeState state) =>
        state is LifetimeState.Collected or LifetimeState.Failed or LifetimeState.Quarantined;

    /// <summary>
    /// Returns <paramref name="to"/> when the <paramref name="from"/> → <paramref name="to"/>
    /// edge is legal; otherwise throws <see cref="InvalidOperationException"/> naming both ends.
    /// </summary>
    internal static LifetimeState TransitionTo(this LifetimeState from, LifetimeState to)
    {
        if (to == LifetimeState.Failed)
        {
            // Any non-terminal state may fail; terminal states exit only via record removal.
            if (from.IsTerminal())
            {
                throw new InvalidOperationException(
                    $"Illegal lifetime transition from {from} to {to}.");
            }

            return to;
        }

        if (to == LifetimeState.Quarantined)
        {
            if (from != LifetimeState.Running)
            {
                throw new InvalidOperationException(
                    $"Illegal lifetime transition from {from} to {to}.");
            }

            return to;
        }

        if (from == LifetimeState.Quiescing && to == LifetimeState.RetirementBlocked)
        {
            return to;
        }

        if (from == LifetimeState.RetirementBlocked && to == LifetimeState.Quiescing)
        {
            return to;
        }

        var forward = (from, to) is
            (LifetimeState.Discovered, LifetimeState.Preparing) or
            (LifetimeState.Preparing, LifetimeState.Loaded) or
            (LifetimeState.Loaded, LifetimeState.Starting) or
            (LifetimeState.Starting, LifetimeState.Running) or
            (LifetimeState.Running, LifetimeState.Retiring) or
            (LifetimeState.Retiring, LifetimeState.Quiescing) or
            (LifetimeState.Quiescing, LifetimeState.Reclaiming) or
            (LifetimeState.Reclaiming, LifetimeState.UnloadRequested) or
            (LifetimeState.UnloadRequested, LifetimeState.Collected);
        if (forward)
        {
            return to;
        }

        throw new InvalidOperationException(
            $"Illegal lifetime transition from {from} to {to}.");
    }
}
