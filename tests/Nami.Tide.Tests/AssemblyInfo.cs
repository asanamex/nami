using Xunit;

// The bridge-enabled flag is process-wide mutable state (toggled by the gate tests).
// Parallel test classes could observe a disabled window, so serialize everything.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
