using Xunit;

// Wave installs real detours on shared JIT methods and keeps one process-wide registry.
// Parallel test classes would corrupt each other's patches, so serialize everything.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
