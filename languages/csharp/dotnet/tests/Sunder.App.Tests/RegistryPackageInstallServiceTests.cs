using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RegistryPackageInstallServiceTests
{
    [Fact]
    public async Task CombinedApprovalPrecedesBothJobsAndPreservesExactCapabilityConsent()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var client = new RegistryInstallationTestClient();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        using var service = new RegistryPackageInstallService((review, _) =>
        {
            prompts++;
            Assert.Empty(queue.ListProcesses());
            Assert.Equal(1024, review.SharedRuntimeDownloadBytes);
            Assert.Equal(client.Consents, review.Plan.RequiredCapabilityConsents);
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
        Assert.Equal(2, queue.ListProcesses().Count);
        Assert.All(queue.ListProcesses(), row => Assert.Equal(BackgroundProcessState.Completed, row.State));
    }

    [Fact]
    public async Task InitialCancelEnqueuesNothingAndDoesNotAcquireRuntime()
    {
        using var queue = new BackgroundProcessQueueService();
        var client = new RegistryInstallationTestClient();
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(false), queue);
        Assert.False((await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client)).Success);
        Assert.Empty(queue.ListProcesses());
        Assert.Null(client.InstallConfirmation);
        Assert.Empty(client.Installs);
    }

    [Theory]
    [InlineData("execution-runtime.download-failed", "Vendor connection refused")]
    [InlineData("execution-runtime.plan-stale", "Runtime preview changed; review again")]
    public async Task RuntimeFailureCancelsQueuedPackageAndKeepsExactError(string code, string message)
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var client = new RegistryInstallationTestClient
        { InstallHandler = (_, _) => Task.FromResult(new RuntimeExecutionRuntimeOperationResult(false, code, message, "install", null, false)) };
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(true), queue);
        var result = await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client);
        Assert.False(result.Success);
        Assert.Contains(message, result.Message);
        Assert.Empty(client.Installs);
        Assert.Single(queue.ListProcesses(), row => row.State == BackgroundProcessState.Failed);
        Assert.Single(queue.ListProcesses(), row => row.State == BackgroundProcessState.Cancelled);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("artifact")]
    [InlineData("permissions")]
    public async Task MaterialDriftWhileQueuedRequiresFreshApproval(string change)
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(new("Other operation", PackageOperationService.PackageStoreGroupKey, BackgroundProcessIndicator.Packages,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup, true, context => release.Task.WaitAsync(context.CancellationToken)));
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
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        using var service = new RegistryPackageInstallService((_, _) => { prompts++; return Task.FromResult(true); }, queue);
        var result = await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, prompts);
        Assert.Null(client.InstallConfirmation);
        Assert.True(client.PreviewCount > 1);
        Assert.Single(client.Installs);
        var job = Assert.Single(queue.ListProcesses());
        Assert.Equal("package", job.Metadata["sunder.installationPhase"]);
        Assert.Equal(BackgroundProcessState.Completed, job.State);
    }

    [Fact]
    public async Task HealthyRuntimeBecomingUnavailableAfterApprovalCannotTriggerUnapprovedAcquisition()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
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
        Assert.Equal(BackgroundProcessState.Failed, Assert.Single(queue.ListProcesses()).State);
    }

    [Fact]
    public async Task CacheOnlyRuntimePreviewCannotBeSilentlyUpgradedToRetainedInstallation()
    {
        using var queue = new BackgroundProcessQueueService();
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
        Assert.Empty(queue.ListProcesses());
        Assert.Empty(client.InstallPreviews);
        Assert.Null(client.InstallConfirmation);
        Assert.Empty(client.Installs);
    }

    [Fact]
    public async Task StackBatchWaitsOutsideOneSlotQueueAndCommitsOneBackendTransaction()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var client = new RegistryInstallationTestClient { PackageIds = ["demo", "other"] };
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(true), queue);
        var result = await service.InstallPackagesAsync([new() { PackageId = "demo" }, new() { PackageId = "other" }],
            new Registry(), client).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Success, result.Message);
        Assert.Equal(2, Assert.Single(client.Batches).Packages.Count);
        Assert.Empty(client.Installs);
        var package = Assert.Single(queue.ListProcesses(), row => row.Metadata["sunder.installationPhase"] == "package");
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
        using var queue = new BackgroundProcessQueueService();
        var client = new RegistryInstallationTestClient
        {
            TransformPreview = preview => invalidFingerprint
                ? preview with { ResolutionFingerprint = "invalid" }
                : preview with { Success = false, Errors = ["Package dependency conflict"] },
        };
        using var service = new RegistryPackageInstallService((_, _) => throw new InvalidOperationException("Must not ask for approval"), queue);
        Assert.False((await service.InstallPackageAsync("demo", "1.0.0", null, false, false, new Registry(), client)).Success);
        Assert.Empty(queue.ListProcesses());
        Assert.Empty(client.Installs);
        Assert.Null(client.InstallConfirmation);
    }

    private sealed class Registry : IRegistryClient
    {
        public Uri RegistryUrl => new(RuntimeManagementTestClient.Origin);
        public void Dispose() { }
    }
}
