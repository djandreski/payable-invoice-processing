using Xunit;

// Browser scenarios publish the same frontend workspace. Serial execution avoids
// concurrent npm installs/builds and keeps restart/failure artifacts deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
