namespace Nami.Core.Generations;

/// <summary>
/// Thrown when host code attempts to enter ordinary execution on a retired generation.
/// Retired generations receive no new execution.
/// </summary>
public sealed class GenerationRetiredException : InvalidOperationException
{
    public GenerationRetiredException(string modId, int generation)
        : base($"Mod '{modId}' generation {generation} is retired; new execution is rejected.")
    {
        ModId = modId;
        GenerationId = generation;
    }

    public string ModId { get; }
    public int GenerationId { get; }
}

/// <summary>
/// Tracks one in-flight host→mod call. Releasing (dispose) decrements the generation's packed
/// lease count through the same atomic gate that retirement sets, so the quiescence drain
/// observes every acquired lease exactly once. Dispose is idempotent; cooperative drain only,
/// never blocking.
/// </summary>
internal sealed class ExecutionLease : IDisposable
{
    private ModGeneration? _generation;

    internal ExecutionLease(ModGeneration generation)
    {
        _generation = generation;
    }

    public ModGeneration Generation => _generation
        ?? throw new ObjectDisposedException(nameof(ExecutionLease));

    public void Dispose()
    {
        var generation = Interlocked.Exchange(ref _generation, null);
        generation?.ReleaseExecution();
    }
}
