using Xunit;

// Live tests hit the same external APIs — run them sequentially to avoid self-throttling.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
