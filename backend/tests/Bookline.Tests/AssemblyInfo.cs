using Xunit;

// Integration test classes each boot their own in-memory server and SQLite
// database. Running those classes in parallel corrupts shared framework state
// during concurrent host startup, which shows up as sporadic
// "non-concurrent collections must have exclusive access" failures in whichever
// class happened to start at the same moment.
//
// The whole suite runs in about two seconds either way, so determinism is worth
// far more here than the parallelism. A flaky test suite is worse than a slow
// one: people stop believing it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
