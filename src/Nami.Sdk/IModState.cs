namespace Nami.Sdk;

/// <summary>
/// Host-owned persisted state for one mod. The host keeps an opaque JSON document plus a schema
/// version outside every load context, so state survives generation replacement. Only strings and
/// ints cross this boundary — never <c>Type</c>, <c>MethodInfo</c>, <c>Delegate</c>, <c>Task</c>,
/// or live instances — which is what makes state safe to carry from a retired generation to its
/// successor. The host validates payloads before they become visible.
/// </summary>
public interface IModState
{
    /// <summary>Schema version of the currently stored state (0 when nothing is stored).</summary>
    int SchemaVersion { get; }

    /// <summary>Returns the stored JSON document, or null when the mod has no stored state.</summary>
    string? GetState();

    /// <summary>Replaces the stored state. The host validates the payload before swapping it in.</summary>
    void SetState(string json, int schemaVersion);

    /// <summary>Drops the stored state.</summary>
    void ClearState();
}
