using Xunit;

// Each integration test creates an application host and resets a shared test
// database. Running those hosts concurrently also races Serilog's reloadable
// bootstrap logger. Keep the suite deterministic; production workers remain
// horizontally scalable and are covered separately by lease/idempotency tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
