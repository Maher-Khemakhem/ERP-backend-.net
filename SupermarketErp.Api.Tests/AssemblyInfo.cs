// Fixtures publish the test database through process-wide environment variables
// (they must override anything in a developer's .env), so test classes run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
