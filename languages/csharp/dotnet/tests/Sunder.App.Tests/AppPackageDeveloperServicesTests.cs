using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageDeveloperServicesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentPreparationUsesExistingApprovalAndQueueAndRejectsSourceDrift(bool drift)
    {
        using var transport = new RuntimeClientTransport(() => null);
        using var client = new RuntimeManagementTestClient();
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        var approved = false;
        var installed = false;
        var requests = new List<RuntimePackageRuntimePrerequisiteRequest>();
        client.PreviewPrerequisites = (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(RuntimeManagementTestClient.Prerequisites(installed,
                source: approved && drift ? new string('d', 64) : null) with { DevelopmentPackageId = "demo" });
        };
        client.InstallHandler = (_, _) =>
        {
            Assert.True(approved);
            var tasks = queue.ListTasks();
            Assert.Equal(2, tasks.Count);
            Assert.All(tasks, task => Assert.Equal(BackgroundTaskIndicator.Main, task.Indicator));
            Assert.Equal(BackgroundTaskState.Running, Assert.Single(tasks, task => task.Metadata["sunder.installationPhase"] == "runtime").State);
            Assert.Equal(BackgroundTaskState.Queued, Assert.Single(tasks, task => task.Metadata["sunder.installationPhase"] == "package").State);
            installed = true;
            return Task.FromResult(new RuntimeExecutionRuntimeOperationResult(true, null, "Installed", "install", RuntimeManagementTestClient.Installed(), false));
        };
        IAppPackageDevelopmentBackend backend = new AppPackageDeveloperServices(transport, new Factory(client), queue, new ImmediateDispatcher())
        {
            ConfirmRuntimes = (packageId, review, _) =>
            {
                Assert.Equal("demo", packageId);
                Assert.Equal(RuntimeManagementTestClient.Identity, Assert.Single(review.Requirements).Installation!.Resolution!.Artifact.Identity);
                Assert.Empty(queue.ListTasks());
                Assert.DoesNotContain("runtime-install", client.Calls);
                approved = true;
                return Task.FromResult(true);
            },
        };
        if (drift)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.PrepareAsync(Path.GetFullPath("dev"), default));
            Assert.False(installed);
            Assert.DoesNotContain("runtime-install", client.Calls);
        }
        else
        {
            Assert.Equal("demo", await backend.PrepareAsync(Path.GetFullPath("dev"), default));
            Assert.True(installed);
            Assert.Equal(2, queue.ListTasks().Count);
            Assert.All(queue.ListTasks(), job => Assert.Equal(Sunder.Sdk.Abstractions.BackgroundTaskState.Completed, job.State));
        }
        Assert.All(requests, request =>
        {
            Assert.Equal(Path.GetFullPath("dev"), request.DevelopmentFolder);
            Assert.Null(request.Local);
            Assert.Null(request.Registry);
        });
        Assert.All(requests.Skip(1), request => Assert.Equal(RuntimeManagementTestClient.Hash, request.ExpectedSourceFingerprint));
    }

    private sealed class Factory(IRuntimeClient client) : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient => (TClient)client;
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
