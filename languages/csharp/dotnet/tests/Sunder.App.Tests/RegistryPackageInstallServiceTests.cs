using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RegistryPackageInstallServiceTests
{
    [Fact]
    public async Task CombinedApprovalPrecedesBothJobsAndPreservesExactCapabilityConsent()
    {
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var client = new RegistryInstallationTestClient();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        using var service = new RegistryPackageInstallService((review, _) =>
        {
            prompts++;
            Assert.Empty(queue.ListTasks());
            Assert.Equal(1024, review.SharedRuntimeDownloadBytes);
            Assert.Equal(client.Consents, review.Plan.RequiredCapabilityConsents);
            var root = Assert.Single(review.Plan.Items);
            Assert.Equal(("demo", "1.0.0"), (root.PackageId, root.Version));
            Assert.Equal(new Registry().RegistryUrl.AbsoluteUri, Assert.Single(review.ExpectedPlans).RegistryOrigin);
            shown.SetResult();
            return approve.Task;
        }, queue);
        var task = service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client);
        await shown.Task;
        Assert.Empty(client.Installs);
        Assert.Null(client.InstallConfirmation);
        approve.SetResult(true);
        Assert.True((await task.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Equal(1, prompts);
        var install = Assert.Single(client.Installs);
        Assert.Equal(client.Consents, install.CapabilityConsents);
        Assert.Equal(RuntimeManagementTestClient.Hash, install.RuntimePrerequisiteExpectation!.SourceFingerprint);
        Assert.Equal(2, queue.ListTasks().Count);
        Assert.All(queue.ListTasks(), row => Assert.Equal(BackgroundTaskState.Completed, row.State));
    }

    [Fact]
    public async Task InitialCancelEnqueuesNothingAndDoesNotAcquireRuntime()
    {
        using var queue = new BackgroundTaskQueueService();
        var client = new RegistryInstallationTestClient();
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(false), queue);
        Assert.False((await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client)).Success);
        Assert.Empty(queue.ListTasks());
        Assert.Null(client.InstallConfirmation);
        Assert.Empty(client.Installs);
    }

    [Theory]
    [InlineData("other", "1.0.0")]
    [InlineData("demo", "2.0.0")]
    public async Task MismatchedInitialResolutionDoesNotOpenConsentOrQueue(string resolvedId, string resolvedVersion)
    {
        using var queue = new BackgroundTaskQueueService();
        var client = new RegistryInstallationTestClient { PackageIds = [resolvedId], PackageVersion = resolvedVersion };
        var shown = false;
        using var service = new RegistryPackageInstallService((_, _) => { shown = true; return Task.FromResult(true); }, queue);
        var result = await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client);
        Assert.False(result.Success);
        Assert.False(shown);
        Assert.Empty(queue.ListTasks());
    }

    [Theory]
    [InlineData("execution-runtime.download-failed", "Vendor connection refused")]
    [InlineData("execution-runtime.plan-stale", "Runtime preview changed; review again")]
    public async Task RuntimeFailureCancelsQueuedPackageAndKeepsExactError(string code, string message)
    {
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var client = new RegistryInstallationTestClient
        { InstallHandler = (_, _) => Task.FromResult(new RuntimeExecutionRuntimeOperationResult(false, code, message, "install", null, false)) };
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(true), queue);
        var result = await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client);
        Assert.False(result.Success);
        Assert.Contains(message, result.Message);
        Assert.Empty(client.Installs);
        Assert.Single(queue.ListTasks(), row => row.State == BackgroundTaskState.Failed);
        Assert.Single(queue.ListTasks(), row => row.State == BackgroundTaskState.Cancelled);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("artifact")]
    [InlineData("permissions")]
    public async Task MaterialDriftWhileQueuedRequiresFreshApproval(string change)
    {
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(new("Other operation", PackageOperationService.PackageStoreGroupKey, BackgroundTaskIndicator.Packages,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup, true, context => release.Task.WaitAsync(context.CancellationToken)));
        var client = new RegistryInstallationTestClient();
        var approved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        using var service = new RegistryPackageInstallService((_, _) => { prompts++; approved.SetResult(); return Task.FromResult(true); }, queue);
        var result = service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client);
        await approved.Task;
        if (change == "version") client.PackageVersion = "2.0.0";
        else if (change == "artifact") client.PackageHash = new('f', 64);
        else client.Consents = [new("demo", "1.0.0", RuntimeManagementTestClient.Hash, ["new.permission"])];
        release.SetResult();
        Assert.Contains("Review the installation again", (await result).Message);
        Assert.Equal(1, prompts);
        Assert.Empty(client.Installs);
        Assert.Null(client.InstallConfirmation);
    }

    [Fact]
    public async Task FreshHandlesTimestampsAndRuntimeCacheReuseDoNotAskForRoutineSecondConsent()
    {
        var client = new RegistryInstallationTestClient { RuntimeReady = true };
        var prompts = 0;
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        using var service = new RegistryPackageInstallService((_, _) => { prompts++; return Task.FromResult(true); }, queue);
        var result = await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, prompts);
        Assert.Null(client.InstallConfirmation);
        Assert.True(client.PreviewCount > 1);
        Assert.Single(client.Installs);
        var job = Assert.Single(queue.ListTasks());
        Assert.Equal("package", job.Metadata["sunder.installationPhase"]);
        Assert.Equal(BackgroundTaskState.Completed, job.State);
    }

    [Fact]
    public async Task ThreeApprovedInstallsReuseTheFirstIdenticalDependencyInOrder()
    {
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 3);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<string>();
        var reviews = new List<string>();
        using var service = new RegistryPackageInstallService((review, _) =>
        {
            Assert.All(review.Plan.Items, item => Assert.Equal(RegistryPackageActionKinds.PackageInstall, item.ActionKind));
            reviews.Add(review.Plan.Items[^1].PackageId);
            return Task.FromResult(true);
        }, queue);
        var tasks = new List<Task<RegistryPackageInstallExecutionResult>>();
        foreach (var name in new[] { "first", "second", "third" })
        {
            var client = new RegistryInstallationTestClient
            {
                RuntimeReady = true, PackageIds = ["shared", name], Consents = [],
                TransformPreview = plan => applied.Count == 0 ? plan : plan with
                {
                    Items = [plan.Items[0] with { ActionKind = RegistryPackageActionKinds.Keep, CurrentVersion = "1.0.0", Artifacts = [] }, plan.Items[1]],
                },
                BeforePackageApply = async () =>
                {
                    if (name == "first")
                    {
                        firstEntered.SetResult();
                        await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    applied.Add(name);
                },
            };
            tasks.Add(service.InstallPackageAsync(name, "1.0.0", null, false, false, new Registry(), client));
            if (name == "first") await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(["first", "second", "third"], reviews);
        Assert.Equal(2, queue.ListTasks().Count(task => task.State == BackgroundTaskState.Queued));
        releaseFirst.SetResult();
        Assert.All(await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)), result => Assert.True(result.Success, result.Message));
        Assert.Equal(reviews, applied);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("target")]
    [InlineData("runtime")]
    [InlineData("acquisition")]
    [InlineData("requested-root")]
    public async Task QueuedInstallCannotReplaceADifferentApprovedAcquisitionWithKeep(string change)
    {
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(new("Prior approval", PackageOperationService.PackageStoreGroupKey, BackgroundTaskIndicator.Packages,
            BackgroundTaskConcurrencyMode.SequentialWithinGroup, true, context => release.Task.WaitAsync(context.CancellationToken)));
        var client = new RegistryInstallationTestClient { PackageIds = ["shared", "root"], RuntimeReady = true, Consents = [] };
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(true), queue);
        var pending = service.InstallPackageAsync("root", "1.0.0", null, false, false, new Registry(), client);
        try
        {
            Assert.Contains(queue.ListTasks(), row => row.Metadata.GetValueOrDefault("sunder.installationPhase") == "package"
                && row.State == BackgroundTaskState.Queued);
            client.TransformPreview = plan =>
            {
                var index = change == "requested-root" ? 1 : 0;
                var items = plan.Items.ToArray();
                var kept = items[index] with { ActionKind = RegistryPackageActionKinds.Keep, CurrentVersion = items[index].Version, Artifacts = [] };
                items[index] = change switch
                {
                    "content" => kept with { PackageContentIdentity = new('f', 64) },
                    "target" => kept with { Targets = [new("app", "linux-arm64", "avalonia", "Other.dll", null, null, [])] },
                    _ => kept,
                };
                return plan with { Items = items, RuntimePrerequisites = change == "acquisition"
                    ? plan.RuntimePrerequisites! with { SourceFingerprint = new('c', 64) } : plan.RuntimePrerequisites };
            };
            if (change == "runtime") client.SelectedRuntime = client.SelectedRuntime with { VendorChecksumDigest = new('b', 128) };
            release.SetResult();
            Assert.Contains("Review the installation again", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Message);
            Assert.Empty(client.Installs);
            Assert.Null(client.InstallConfirmation);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task HealthyRuntimeBecomingUnavailableAfterApprovalCannotTriggerUnapprovedAcquisition()
    {
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var client = new RegistryInstallationTestClient { RuntimeReady = true };
        using var service = new RegistryPackageInstallService((_, _) =>
        {
            client.RuntimeReady = false;
            return Task.FromResult(true);
        }, queue);

        var result = await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client);

        Assert.False(result.Success);
        Assert.Contains("runtime is not ready", result.Message);
        Assert.Null(client.InstallConfirmation);
        Assert.Empty(client.Installs);
        Assert.Equal(BackgroundTaskState.Failed, Assert.Single(queue.ListTasks()).State);
    }

    [Fact]
    public async Task CacheOnlyRuntimePreviewCannotBeSilentlyUpgradedToRetainedInstallation()
    {
        using var queue = new BackgroundTaskQueueService();
        var runtimes = RuntimeManagementTestClient.Prerequisites(false);
        var client = new RegistryInstallationTestClient
        {
            TransformPreview = preview => preview with
            {
                RuntimePrerequisites = runtimes with
                {
                    Requirements = [runtimes.Requirements[0] with { Installation = RuntimeManagementTestClient.InstallPlan(retain: false) }],
                },
            },
        };
        using var service = new RegistryPackageInstallService((_, _) => throw new InvalidOperationException("Must not ask for approval"), queue);

        Assert.False((await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client)).Success);
        Assert.Empty(queue.ListTasks());
        Assert.Empty(client.InstallPreviews);
        Assert.Null(client.InstallConfirmation);
        Assert.Empty(client.Installs);
    }

    [Fact]
    public async Task StackBatchWaitsOutsideOneSlotQueueAndCommitsOneBackendTransaction()
    {
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var client = new RegistryInstallationTestClient { PackageIds = ["demo", "other"] };
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(true), queue);
        var result = await service.InstallPackagesAsync([new() { PackageId = "demo" }, new() { PackageId = "other" }],
            new Registry(), client).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Success, result.Message);
        Assert.Equal(2, Assert.Single(client.Batches).Packages.Count);
        Assert.Empty(client.Installs);
        var package = Assert.Single(queue.ListTasks(), row => row.Metadata["sunder.installationPhase"] == "package");
        Assert.Contains("other", package.Metadata["sunder.installationMembers"]);
    }

    [Fact]
    public async Task UpdateAllUsesOneAtomicUpdateInsteadOfPerPackageCommits()
    {
        var client = new RegistryInstallationTestClient { PackageIds = ["demo", "other"] };
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(true));
        Assert.True((await service.UpdateAllAsync(client)).Success);
        var update = Assert.Single(client.Updates);
        Assert.Null(update.PackageId);
        Assert.NotNull(update.RuntimePrerequisiteExpectation);
        Assert.NotEmpty(update.ExpectedPlans);
        Assert.Empty(client.Batches);
    }

    [Fact]
    public async Task AdoptionRetainsSourceAndFreshPlanExpectations()
    {
        var client = new RegistryInstallationTestClient();
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(true));
        Assert.True((await service.AdoptPackageSourceAsync("demo", new(RuntimeManagementTestClient.Origin), client)).Success);
        var request = Assert.Single(client.Adoptions);
        Assert.True(request.Confirm);
        Assert.Equal(RuntimeManagementTestClient.Origin, request.RegistryOrigin);
        Assert.NotNull(request.RuntimePrerequisiteExpectation);
        Assert.NotNull(request.ExpectedResolutionFingerprint);
        Assert.Equal(client.Consents, request.CapabilityConsents);
    }

    [Fact]
    public async Task ChangedVendorChecksumAtSameVersionCannotReuseConsent()
    {
        var client = new RegistryInstallationTestClient();
        using var service = new RegistryPackageInstallService((_, _) =>
        {
            client.SelectedRuntime = client.SelectedRuntime with { VendorChecksumDigest = new('b', 128) };
            return Task.FromResult(true);
        });
        var result = await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client);
        Assert.False(result.Success);
        Assert.Contains("Review the installation again", result.Message);
        Assert.Null(client.InstallConfirmation);
        Assert.Empty(client.Installs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidOrConflictingPackagePreviewCannotReachConfirmationOrQueue(bool invalidFingerprint)
    {
        using var queue = new BackgroundTaskQueueService();
        var client = new RegistryInstallationTestClient
        {
            TransformPreview = preview => invalidFingerprint
                ? preview with { ResolutionFingerprint = "invalid" }
                : preview with { Success = false, Errors = ["Package dependency conflict"] },
        };
        using var service = new RegistryPackageInstallService((_, _) => throw new InvalidOperationException("Must not ask for approval"), queue);
        Assert.False((await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client)).Success);
        Assert.Empty(queue.ListTasks());
        Assert.Empty(client.Installs);
        Assert.Null(client.InstallConfirmation);
    }

    private sealed class Registry : IRegistryClient
    {
        public Uri RegistryUrl => new(RuntimeManagementTestClient.Origin);
        public void Dispose() { }
    }
}
