using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class StackImportPlanStorePolicyTests
{
    [Fact]
    public void Defaults_AreProductOwned()
    {
        var policy = new RuntimeStackPolicyOptions();

        Assert.Equal(16, policy.MaxImportPlans);
        Assert.Equal(2L * 1024 * 1024 * 1024, policy.MaxImportPlanExtractedBytes);
    }

    [Theory]
    [InlineData(3, 100)]
    [InlineData(32, 3)]
    public async Task ConcurrentAdmissions_AtomicallyEnforceCountAndAggregateExtractedBytes(
        int maxPlans,
        long maxExtractedBytes)
    {
        await WithStoreAsync(
            new RuntimeStackPolicyOptions
            {
                MaxImportPlans = maxPlans,
                MaxImportPlanExtractedBytes = maxExtractedBytes,
            },
            async (store, paths, _) =>
            {
                var candidates = Enumerable.Range(0, 24)
                    .Select(index => CreatePlan(paths, $"plan-{index:D2}", extractedBytes: 1, store.ExpiresAtUtc))
                    .ToArray();
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var attempts = candidates.Select(async plan =>
                {
                    await start.Task;
                    return (Plan: plan, Added: store.TryAdd(plan));
                }).ToArray();

                start.SetResult();
                var results = await Task.WhenAll(attempts);

                Assert.Equal(3, results.Count(static result => result.Added));
                foreach (var result in results)
                {
                    if (result.Added) Assert.True(store.Discard(result.Plan.PlanId));
                    else store.Release(result.Plan);
                }
                Assert.All(candidates, AssertPlanFilesDeleted);
            });
    }

    [Fact]
    public async Task TakenPlan_RetainsQuotaUntilReleaseThenAllowsReplacement()
    {
        await WithStoreAsync(
            new RuntimeStackPolicyOptions
            {
                MaxImportPlans = 1,
                MaxImportPlanExtractedBytes = 4,
            },
            (store, paths, _) =>
            {
                var first = CreatePlan(paths, "first", extractedBytes: 4, store.ExpiresAtUtc);
                var replacement = CreatePlan(paths, "replacement", extractedBytes: 1, store.ExpiresAtUtc);
                Assert.True(store.TryAdd(first));
                Assert.True(store.TryTake(first.PlanId, out var taken));

                Assert.False(store.TryAdd(replacement));

                store.Release(taken);
                AssertPlanFilesDeleted(first);
                Assert.True(store.TryAdd(replacement));
                Assert.True(store.Discard(replacement.PlanId));
                AssertPlanFilesDeleted(replacement);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task SweepExpired_ReleasesQuotaAndCleansExtractedAndUploadFiles()
    {
        var lifetime = TimeSpan.FromMinutes(1);
        await WithStoreAsync(
            new RuntimeStackPolicyOptions
            {
                ImportPlanLifetime = lifetime,
                MaxImportPlans = 1,
                MaxImportPlanExtractedBytes = 1,
            },
            (store, paths, clock) =>
            {
                var expired = CreatePlan(paths, "expired", extractedBytes: 1, store.ExpiresAtUtc);
                Assert.True(store.TryAdd(expired));

                clock.Advance(lifetime);
                store.SweepExpired();

                Assert.False(store.TryTake(expired.PlanId, out _));
                AssertPlanFilesDeleted(expired);
                var replacement = CreatePlan(paths, "replacement", extractedBytes: 1, store.ExpiresAtUtc);
                Assert.True(store.TryAdd(replacement));
                Assert.True(store.Discard(replacement.PlanId));
                AssertPlanFilesDeleted(replacement);
                return Task.CompletedTask;
            });
    }

    private static async Task WithStoreAsync(
        RuntimeStackPolicyOptions policy,
        Func<StackImportPlanStore, RuntimePackagePaths, MutableTimeProvider, Task> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sunder-stack-import-plan-policy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new RuntimePackagePaths(root);
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            using var transfers = new RuntimeContentTransferStore(paths, timeProvider: clock);
            using var store = new StackImportPlanStore(transfers, clock, policy);
            await action(store, paths, clock);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static StackImportPlan CreatePlan(
        RuntimePackagePaths paths,
        string planId,
        int extractedBytes,
        DateTimeOffset expiresAtUtc)
    {
        var stagingPath = Path.Combine(paths.RootPath, "import-plans", planId);
        Directory.CreateDirectory(stagingPath);
        File.WriteAllBytes(Path.Combine(stagingPath, "payload.bin"), new byte[extractedBytes]);
        Directory.CreateDirectory(paths.TransferRootPath);
        var uploadPath = Path.Combine(paths.TransferRootPath, planId + ".sunderstack");
        File.WriteAllBytes(uploadPath, [1]);
        var upload = new RuntimeUploadLease(
            planId + "-upload",
            uploadPath,
            new string('a', 64),
            Length: 1,
            "stack.sunderstack",
            "application/vnd.sunder.stack");
        return new StackImportPlan(
            planId,
            upload,
            upload.ContentHash,
            stagingPath,
            Generation: 1,
            SelectedFragmentIds: [],
            InputValues: new Dictionary<string, string>(),
            IdRemaps: new Dictionary<string, string>(),
            Contributors: [],
            Actions: [],
            Conflicts: [],
            expiresAtUtc);
    }

    private static void AssertPlanFilesDeleted(StackImportPlan plan)
    {
        Assert.False(Directory.Exists(plan.StagingPath));
        Assert.False(File.Exists(plan.Upload.FilePath));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}
