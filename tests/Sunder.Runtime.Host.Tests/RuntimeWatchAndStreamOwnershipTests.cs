using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeWatchAndStreamOwnershipTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public async Task RuntimeEvents_ReconnectGapFallsBackToSnapshot()
    {
        var events = new RuntimeEventStreamService();
        for (var index = 0; index < RuntimeEventStreamService.ReplayCapacity + 10; index++)
        {
            events.PublishOperationPhase(RuntimeOperationPhase.DevReloadDebounce, [$"package.{index}"]);
        }

        await using var subscription = events.Subscribe(afterSequenceId: 1);
        var item = await subscription.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(subscription.HistoryGap);
        Assert.Equal(RuntimeEventKind.Snapshot, item.Kind);
        Assert.Equal(events.GetSnapshot().SequenceId, item.SequenceId);
    }

    [Fact]
    public async Task RuntimeEvents_SlowClientIsDisconnectedAtBoundedCapacity()
    {
        var events = new RuntimeEventStreamService();
        await using var subscription = events.Subscribe(0);
        for (var index = 0; index < RuntimeEventStreamService.SubscriberCapacity + 10; index++)
        {
            events.PublishOperationPhase(RuntimeOperationPhase.DevReloadDebounce, [$"package.{index}"]);
        }

        await Assert.ThrowsAsync<SlowRuntimeStreamConsumerException>(async () =>
        {
            await foreach (var _ in subscription.Reader.ReadAllAsync())
            {
            }
        });
    }

    [Fact]
    public async Task RuntimeEvents_ShutdownCompletesSubscribers()
    {
        var events = new RuntimeEventStreamService();
        await using var subscription = events.Subscribe(0);

        events.Complete();

        Assert.False(await subscription.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task PackageLogs_MalformedOversizedAndSensitiveLinesAreSafelyBounded()
    {
        var root = CreateTempDirectory();
        try
        {
            var logs = Path.Combine(root, "test.package", "logs");
            Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "runtime.log");
            var oversized = $"Jul 11 12:00:00 host test.package[1]: level=error category=worker msg=\"token=visible {root} /tmp/private {new string('x', PackageLogStreamService.MaxLineBytes * 2)}\"";
            await File.WriteAllTextAsync(path, $"malformed log line{Environment.NewLine}{oversized}{Environment.NewLine}");
            await using var service = new PackageLogStreamService(root);

            service.Start();
            var snapshot = service.GetSnapshot(limit: 10);

            Assert.Equal(2, snapshot.Entries.Count);
            Assert.Equal(RuntimePackageLogLevel.Information, snapshot.Entries[0].Level);
            var entry = snapshot.Entries[1];
            Assert.Equal(RuntimePackageLogLevel.Error, entry.Level);
            Assert.True(entry.Truncated);
            Assert.True(entry.Message.Length <= PackageLogStreamService.MaxMessageCharacters);
            Assert.DoesNotContain(root, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("visible", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("/tmp/private", entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PackageLogs_ShutdownCompletesLiveStream()
    {
        var root = CreateTempDirectory();
        var service = new PackageLogStreamService(root);
        service.Start();
        await using var subscription = service.Subscribe(0);

        await service.DisposeAsync();

        Assert.False(await subscription.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        TryDeleteDirectory(root);
    }

    [Fact]
    public async Task PackageLogs_SlowClientIsDisconnectedAtBoundedCapacity()
    {
        var root = CreateTempDirectory();
        await using var service = new PackageLogStreamService(root);
        service.Start();
        await using var subscription = service.Subscribe(0);
        for (var index = 0; index < PackageLogStreamService.SubscriberCapacity + 10; index++)
        {
            service.ProcessLineForTest("test.package", $"level=info category=test msg=entry-{index}");
        }

        await Assert.ThrowsAsync<SlowRuntimeStreamConsumerException>(async () =>
        {
            await foreach (var _ in subscription.Reader.ReadAllAsync())
            {
            }
        });
        TryDeleteDirectory(root);
    }

    [Fact]
    public async Task DevWatcher_DebounceIsLatestWinsAndDisposalStopsReloads()
    {
        var folder = CreateDevPackageFolder();
        var stageCount = 0;
        var commitCount = 0;
        var events = new RuntimeEventStreamService();
        var watcher = CreateWatcher(
            folder,
            events,
            stage: (request, _) =>
            {
                Interlocked.Increment(ref stageCount);
                return Task.FromResult(CreateStage(request.PackageIds));
            },
            commit: (_, _) =>
            {
                Interlocked.Increment(ref commitCount);
                return Task.FromResult(CreateReloadResult(success: true));
            });
        await watcher.SetIntentAsync(true);

        for (var index = 0; index < 20; index++)
        {
            watcher.NotifyChangedForTest("test.package");
        }

        await WaitUntilAsync(() => Volatile.Read(ref commitCount) == 1);
        Assert.Equal(1, stageCount);

        await watcher.DisposeAsync();
        await File.AppendAllTextAsync(Path.Combine(folder, "lib", "package.dll"), "after-dispose");
        await Task.Delay(150);
        Assert.Equal(1, commitCount);
        TryDeleteDirectory(folder);
    }

    [Fact]
    public async Task DevWatcher_ParentFolderReplacementStormReloadsAndRebinds()
    {
        var folder = CreateDevPackageFolder();
        var parent = Path.GetDirectoryName(folder)!;
        var commitCount = 0;
        var events = new RuntimeEventStreamService();
        await using var watcher = CreateWatcher(
            folder,
            events,
            commit: (_, _) =>
            {
                Interlocked.Increment(ref commitCount);
                return Task.FromResult(CreateReloadResult(success: true));
            });
        await watcher.SetIntentAsync(true);

        for (var index = 0; index < 3; index++)
        {
            var old = folder + $".old-{index}";
            Directory.Move(folder, old);
            CreateDevPackageFolder(folder);
            TryDeleteDirectory(old);
            watcher.NotifyParentReplacementForTest("test.package");
        }

        watcher.NotifyChangedForTest("test.package");
        await WaitUntilAsync(() => Volatile.Read(ref commitCount) >= 1);
        var afterStorm = commitCount;
        await File.AppendAllTextAsync(Path.Combine(folder, "lib", "package.dll"), "rebound");
        await WaitUntilAsync(() => Volatile.Read(ref commitCount) > afterStorm);
        TryDeleteDirectory(parent);
    }

    [Fact]
    public async Task DevWatcher_StaleGenerationResultIsPublishedAsFailure()
    {
        var folder = CreateDevPackageFolder();
        var events = new RuntimeEventStreamService();
        await using var watcher = CreateWatcher(
            folder,
            events,
            commit: (_, _) => Task.FromResult(CreateReloadResult(success: false, "stage is stale because the session generation changed")));
        await watcher.SetIntentAsync(true);

        watcher.NotifyChangedForTest("test.package");
        await WaitUntilAsync(() => events.GetSnapshot().Events.Any(item => item.Kind == RuntimeEventKind.DevReloadCompleted));

        var result = events.GetSnapshot().Events.Last(item => item.Kind == RuntimeEventKind.DevReloadCompleted);
        Assert.False(result.Success);
        Assert.Contains("stale", result.Message, StringComparison.OrdinalIgnoreCase);
        TryDeleteDirectory(folder);
    }

    [Fact]
    public void AppProductionCode_DoesNotWatchOrDiscoverPackageRoots()
    {
        var appRoot = Path.Combine(RepositoryRoot, "src", "Host", "Sunder.App");
        var sources = string.Join('\n', Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var developerLog = File.ReadAllText(Path.Combine(appRoot, "Services", "DeveloperLogService.cs"));

        Assert.DoesNotContain("FileSystemWatcher", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("DevPackageWatchSupport", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.EnumerateFiles", developerLog, StringComparison.Ordinal);
        Assert.DoesNotContain("SpecialFolder.LocalApplicationData", developerLog, StringComparison.Ordinal);
    }

    private static DevPackageWatchService CreateWatcher(
        string folder,
        RuntimeEventStreamService events,
        Func<PackageLifecycleStageRequest, CancellationToken, Task<PackageLifecycleStageResult>>? stage = null,
        Func<string, CancellationToken, Task<PackageLifecycleOperationResult>>? commit = null)
        => new(
            (enabled, _) => Task.FromResult<IReadOnlyList<DevPackageWatchTarget>>(enabled ? [new("test.package", folder)] : []),
            stage ?? ((request, _) => Task.FromResult(CreateStage(request.PackageIds))),
            commit ?? ((_, _) => Task.FromResult(CreateReloadResult(success: true))),
            (_, _) => Task.FromResult(true),
            () => 7,
            events,
            NullLogger<DevPackageWatchService>.Instance,
            async (delay, cancellationToken) =>
            {
                if (delay == DevPackageWatchService.DebounceDelay)
                {
                    await Task.Delay(50, cancellationToken);
                }
            });

    private static PackageLifecycleStageResult CreateStage(IReadOnlyList<string> packageIds)
        => new("stage-1", [], [], [], [], packageIds);

    private static PackageLifecycleOperationResult CreateReloadResult(bool success, string? error = null)
        => new(success, success ? "reloaded" : error, [], [], [], error is null ? [] : [error], ["test.package"]);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition was not reached before the test deadline.");
    }

    private static string CreateDevPackageFolder(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), "sunder-runtime-watch-tests", Guid.NewGuid().ToString("N"), "sunder-dev");
        Directory.CreateDirectory(Path.Combine(path, "lib"));
        File.WriteAllText(Path.Combine(path, "sunder-package.json"), "{\"id\":\"test.package\"}");
        File.WriteAllText(Path.Combine(path, "lib", "package.dll"), "test");
        return path;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-stream-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
