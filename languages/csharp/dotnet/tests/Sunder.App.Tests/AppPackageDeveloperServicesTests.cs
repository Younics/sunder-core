using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using System.Text.Json;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageDeveloperServicesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedLogsPageOnlyBoundedHistoryFilterPackageAndScopeAndFitTheirTransport(bool replaceRuntime)
    {
        var packageId = "test.logs." + Guid.NewGuid().ToString("N");
        var runtimeId = Guid.NewGuid();
        var time = DateTimeOffset.UtcNow.AddSeconds(-3000);
        var message = string.Concat(Enumerable.Repeat("\U0001F642\t\"", 2000));
        using var logClient = new LogClient((after, _) => new PackageLogSnapshot(2000,
            Enumerable.Range(after == 0 ? 1 : 1001, 1000).Select(index => new PackageLogEntryDescriptor(index,
                index >= 1900 || index == 1 ? packageId : "another.package", time.AddSeconds(index), RuntimePackageLogLevel.Information,
                "diagnostic", message, false)).ToArray(), false)
            { RuntimeInstanceId = replaceRuntime && after != 0 ? Guid.NewGuid() : runtimeId });
        using var transport = new RuntimeClientTransport(() => null);
        await using var queue = new BackgroundTaskQueueService(maxParallelism: 1);
        IAppPackageDevelopmentBackend backend = new AppPackageDeveloperServices(transport, new Factory(logClient), queue, new ImmediateDispatcher());
        AppSessionLog.WriteInfo("not package scoped", developerLogSource: packageId);
        AppSessionLog.WritePackage(PackageLogLevel.Information, "another.package", "other package");
        AppSessionLog.WritePackage(PackageLogLevel.Information, packageId, "selected App package");
        if (replaceRuntime)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.GetLogsAsync(packageId, 50, default));
            return;
        }
        var result = await backend.GetLogsAsync(packageId, 50, default);
        Assert.Equal(packageId, result.PackageId);
        Assert.True(result.WasTruncated);
        Assert.InRange(result.Entries.Count, 1, 50);
        Assert.Contains(result.Entries, entry => entry.Source == "app" && entry.Message == "selected App package");
        Assert.DoesNotContain(result.Entries, entry => entry.Message is "not package scoped" or "other package");
        Assert.True(result.Entries.All(entry => entry.Message.Length <= 4096
            && (entry.Message.Length == 0 || !char.IsHighSurrogate(entry.Message[^1]))), "Log truncation must preserve Unicode scalar boundaries.");
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length < 256 * 1024);
        Assert.Equal(new long[] { 0, 1000 }, logClient.Cursors);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    public async Task LogQueryByteBudgetIncludesMetadataEscapedTextAndContinuation(int maximumBytes)
    {
        var packageId = "bounded.logs." + Guid.NewGuid().ToString("N");
        var origin = Guid.NewGuid();
        using var logClient = new LogClient((_, _) => new(3,
            Enumerable.Range(1, 3).Select(index => new PackageLogEntryDescriptor(index, packageId, DateTimeOffset.UnixEpoch.AddSeconds(index),
                RuntimePackageLogLevel.Error, new string('\u263A', 256), new string('\u263A', 4096), false)
                { Properties = new Dictionary<string, string> { ["rpc_method_id"] = new string('\u263A', 256) } }).ToArray(), false) { RuntimeInstanceId = origin });
        using var transport = new RuntimeClientTransport(() => null);
        await using var queue = new BackgroundTaskQueueService();
        IAppPackageDevelopmentBackend backend = new AppPackageDeveloperServices(transport, new Factory(logClient), queue, new ImmediateDispatcher());
        var result = await backend.GetLogsAsync(packageId, new PackageDevelopmentLogQuery { MaximumBytes = maximumBytes, MaximumEntries = 1 }, default);
        Assert.Single(result.Entries);
        Assert.True(result.WasTruncated);
        Assert.True(result.HasMore);
        Assert.NotNull(result.NextCursor);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(result).Length, 1, maximumBytes);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("all")]
    public async Task LogQueriesFilterBeforeLatestSelectionAndPageWithoutRepeatingEntries(string source)
    {
        var packageId = "filtered.logs." + Guid.NewGuid().ToString("N");
        var origin = Guid.NewGuid();
        var time = DateTimeOffset.UtcNow.AddMinutes(-10);
        using var logClient = new LogClient((_, _) => new(300,
            Enumerable.Range(1, 300).Select(index => new PackageLogEntryDescriptor(index, packageId, time.AddSeconds(index),
                index is 2 or 3 or 149 or 250 ? RuntimePackageLogLevel.Error : RuntimePackageLogLevel.Information,
                "worker", index.ToString(), false)).ToArray(), false) { RuntimeInstanceId = origin });
        using var transport = new RuntimeClientTransport(() => null);
        await using var queue = new BackgroundTaskQueueService();
        IAppPackageDevelopmentBackend backend = new AppPackageDeveloperServices(transport, new Factory(logClient), queue, new ImmediateDispatcher());
        var query = new PackageDevelopmentLogQuery { Source = source, MinimumLevel = PackageLogLevel.Error, MaximumEntries = 2,
            Since = time.AddSeconds(2), Until = time.AddSeconds(299), MaximumBytes = 2048 };
        var first = await backend.GetLogsAsync(packageId, query, default);
        Assert.Equal(new[] { "149", "250" }, first.Entries.Select(entry => entry.Message));
        Assert.True(first.HasMore);
        Assert.False(first.HistoryGap);
        Assert.False(first.WasTruncated);
        Assert.NotNull(first.NextCursor);
        var second = await backend.GetLogsAsync(packageId, query with { Cursor = first.NextCursor }, default);
        Assert.Equal(new[] { "2", "3" }, second.Entries.Select(entry => entry.Message));
        Assert.False(second.HasMore);
        Assert.Null(second.NextCursor);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first).Length <= query.MaximumBytes);
        await Assert.ThrowsAsync<ArgumentException>(() => backend.GetLogsAsync("other.package", query with { Cursor = first.NextCursor }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => backend.GetLogsAsync(packageId, query with { Cursor = first.NextCursor, MinimumLevel = PackageLogLevel.Warning }, default));
    }

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
    private sealed class LogClient(Func<long, int, PackageLogSnapshot> snapshot) : IRuntimeLogClient
    {
        internal List<long> Cursors { get; } = [];
        public Task<PackageLogSnapshot> GetPackageLogSnapshotAsync(long afterSequenceId = 0, int limit = 500, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(1000, limit);
            Cursors.Add(afterSequenceId);
            return Task.FromResult(snapshot(afterSequenceId, limit));
        }
        public IAsyncEnumerable<PackageLogEntryDescriptor> StreamPackageLogsAsync(long afterSequenceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void Dispose() { }
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
