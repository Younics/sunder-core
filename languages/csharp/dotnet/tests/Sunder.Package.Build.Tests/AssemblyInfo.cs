using Xunit;

// Compiler-heavy collections must fit alongside the SDK build processes on CI workers.
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
