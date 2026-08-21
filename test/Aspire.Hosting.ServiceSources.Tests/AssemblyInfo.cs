using Xunit;

// GitCredentialResolverTests mutates process-wide environment variables (including PATH) for the
// duration of each test; disabling parallelization keeps that from racing against unrelated tests
// running concurrently in other collections. The suite is small enough (a few hundred ms) that
// running sequentially costs nothing worth trading isolation for.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
