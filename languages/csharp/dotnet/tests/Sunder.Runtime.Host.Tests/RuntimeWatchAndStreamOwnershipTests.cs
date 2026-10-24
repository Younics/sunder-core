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
    public async Task RuntimeEvents_ReconnectBacklogLargerThanSubscriberBufferFallsBackToSnapshot()
    {
        var events = new RuntimeEventStreamService();
        for (var index = 0; index < RuntimeEventStreamService.SubscriberCapacity + 1; index++)
        {
            events.PublishOperationPhase(RuntimeOperationPhase.DevReloadDebounce, [$"package.{index}"]);
        }

        await using var subscription = events.Subscribe(afterSequenceId: 0);
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
    public async Task PackageLogsRetainOnlyBoundedSafeStructuredFaultIdentity()
    {
        var root = CreateTempDirectory();
        try
        {
            var folder = Path.Combine(root, "test.package", "logs"); Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "events.log"),
                "Jul 11 12:00:00 host test.package[1]: level=error event=runtime.rpc.provider-fault msg=failed rpc_method_id=get rpc_transport_failure_kind=WriteQueueAdmission rpc_exception_type=RpcInstanceError secret=do-not-expose\n");
            await using var service = new PackageLogStreamService(root); service.Start();
            var entry = Assert.Single(service.GetSnapshot().Entries);
            Assert.Equal("get", entry.Properties["rpc_method_id"]);
            Assert.Equal("WriteQueueAdmission", entry.Properties["rpc_transport_failure_kind"]);
            Assert.Equal("RpcInstanceError", entry.Properties["rpc_exception_type"]);
            Assert.False(entry.Properties.ContainsKey("secret"));
            Assert.DoesNotContain("do-not-expose", System.Text.Json.JsonSerializer.Serialize(entry));
        }
        finally { TryDeleteDirectory(root); }
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

            Assert.NotEqual(Guid.Empty, snapshot.RuntimeInstanceId);
            Assert.Equal(2, snapshot.Entries.Count);
            Assert.All(snapshot.Entries, entry => Assert.Equal(snapshot.RuntimeInstanceId, entry.RuntimeInstanceId));
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
    public async Task PackageLogs_InitialReplayIsLimitedToNewestFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var timestamp = DateTime.UtcNow.AddDays(-1);
            string? oldestPath = null;
            for (var index = 0; index < PackageLogStreamService.MaxInitialReplayFiles + 2; index++)
            {
                var logs = Path.Combine(root, $"test.package.{index}", "logs");
                Directory.CreateDirectory(logs);
                var path = Path.Combine(logs, "runtime.log");
                await File.WriteAllTextAsync(path, $"level=info category=test msg=entry-{index}{Environment.NewLine}");
                File.SetLastWriteTimeUtc(path, timestamp.AddSeconds(index));
                oldestPath ??= path;
            }

            await using var service = new PackageLogStreamService(root);
            service.Start();
            var snapshot = service.GetSnapshot(limit: 1000);

            Assert.Equal(PackageLogStreamService.MaxInitialReplayFiles, snapshot.Entries.Count);
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Message == "entry-0");
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Message == "entry-1");
            Assert.Contains(snapshot.Entries, entry => entry.Message == $"entry-{PackageLogStreamService.MaxInitialReplayFiles + 1}");

            await File.AppendAllTextAsync(oldestPath!, $"level=info category=test msg=entry-0-new{Environment.NewLine}");
            await WaitUntilAsync(() =>
                service.GetSnapshot(limit: 1000).Entries.Any(entry => entry.Message == "entry-0-new"));
            var updated = service.GetSnapshot(limit: 1000);

            Assert.DoesNotContain(updated.Entries, entry => entry.Message == "entry-0");
            Assert.Contains(updated.Entries, entry => entry.Message == "entry-0-new");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PackageLogs_LimitedReplayUsesContinuationCursorWithoutSkippingEntries()
    {
        var root = CreateTempDirectory();
        try
        {
            var logs = Path.Combine(root, "test.package", "logs");
            Directory.CreateDirectory(logs);
            await File.WriteAllLinesAsync(
                Path.Combine(logs, "runtime.log"),
                Enumerable.Range(0, 1200).Select(index => $"level=info category=test msg=entry-{index}"));
            await using var service = new PackageLogStreamService(root);
            service.Start();

            var first = service.GetSnapshot(limit: 500);
            var second = service.GetSnapshot(first.SequenceId, limit: 500);
            var third = service.GetSnapshot(second.SequenceId, limit: 500);

            Assert.Equal(1, first.Entries[0].SequenceId);
            Assert.Equal(500, first.SequenceId);
            Assert.Equal(501, second.Entries[0].SequenceId);
            Assert.Equal(1000, second.SequenceId);
            Assert.Equal(1001, third.Entries[0].SequenceId);
            Assert.Equal(1200, third.SequenceId);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PackageLogs_SingleLargeAppendContinuesPastOneMiBReadBudget()
    {
        var root = CreateTempDirectory();
        try
        {
            var logs = Path.Combine(root, "test.package", "logs");
            Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "runtime.log");
            await File.WriteAllTextAsync(path, string.Empty);
            await using var service = new PackageLogStreamService(root);
            service.Start();
            var payload = new string('x', 1024);
            var content = string.Join(
                Environment.NewLine,
                Enumerable.Range(0, 1100)
                    .Select(index => $"level=info category=test msg=entry-{index}-{payload}"));
            await File.AppendAllTextAsync(
                path,
                content + Environment.NewLine + "level=info category=test msg=continuation-marker" + Environment.NewLine);

            await WaitUntilAsync(() =>
            {
                var replay = service.GetSnapshot(limit: 1000);
                return service.GetSnapshot(replay.SequenceId, limit: 1000).Entries
                    .Any(entry => entry.Message == "continuation-marker");
            });

            var first = service.GetSnapshot(limit: 1000);
            Assert.Contains(
                service.GetSnapshot(first.SequenceId, limit: 1000).Entries,
                entry => entry.Message == "continuation-marker");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PackageLogs_WatcherObservesFileCreatedAfterReplaySetup()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var service = new PackageLogStreamService(root);
            service.Start();
            var logs = Path.Combine(root, "test.package", "logs");
            Directory.CreateDirectory(logs);
            await File.WriteAllTextAsync(
                Path.Combine(logs, "runtime.log"),
                $"level=info category=test msg=watcher-marker{Environment.NewLine}");

            await WaitUntilAsync(() =>
                service.GetSnapshot(limit: 10).Entries.Any(entry => entry.Message == "watcher-marker"));
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
        try
        {
            var logs = Path.Combine(root, "test.package", "logs");
            Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "runtime.log");
            await File.WriteAllTextAsync(path, string.Empty);
            await using var service = new PackageLogStreamService(root);
            service.Start();
            await using var subscription = service.Subscribe(0);
            var entryCount = PackageLogStreamService.SubscriberCapacity + 10;
            await File.AppendAllLinesAsync(
                path,
                Enumerable.Range(0, entryCount).Select(index => $"level=info category=test msg=entry-{index}"));
            await WaitUntilAsync(() => service.GetSnapshot(limit: 1000).Entries.Count == entryCount);

            await Assert.ThrowsAsync<SlowRuntimeStreamConsumerException>(async () =>
            {
                await foreach (var _ in subscription.Reader.ReadAllAsync())
                {
                }
            });
        }
        finally
        {
            TryDeleteDirectory(root);
        }
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
        await watcher.SynchronizeAsync([new DevPackageWatchTarget("test.package", folder)]);

        for (var index = 0; index < 20; index++)
        {
            await File.AppendAllTextAsync(GetDevPackagePayloadPath(folder), index.ToString());
        }

        await WaitUntilAsync(() => Volatile.Read(ref commitCount) == 1);
        Assert.Equal(1, stageCount);

        await watcher.DisposeAsync();
        await File.AppendAllTextAsync(GetDevPackagePayloadPath(folder), "after-dispose");
        await Task.Delay(150);
        Assert.Equal(1, commitCount);
        TryDeleteDirectory(folder);
    }

    [Fact]
    public async Task DevWatcher_OwnerDerivedTargetsReplacePreviousRegistrations()
    {
        var folder = CreateDevPackageFolder();
        var events = new RuntimeEventStreamService();
        await using var watcher = new DevPackageWatchService(
            (request, _) => Task.FromResult(CreateStage(request.PackageIds)),
            (_, _) => Task.FromResult(CreateReloadResult(success: true)),
            (_, _) => Task.FromResult(true),
            events,
            NullLogger<DevPackageWatchService>.Instance,
            Task.Delay);

        await watcher.SynchronizeAsync([new DevPackageWatchTarget("test.package", folder)]);
        await watcher.SynchronizeAsync([]);
        await File.AppendAllTextAsync(GetDevPackagePayloadPath(folder), "unregistered");
        await Task.Delay(100);
        Assert.DoesNotContain(
            events.GetSnapshot().Events,
            item => item.Kind == RuntimeEventKind.DevReloadCompleted);
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
        await watcher.SynchronizeAsync([new DevPackageWatchTarget("test.package", folder)]);

        for (var index = 0; index < 3; index++)
        {
            var old = folder + $".old-{index}";
            Directory.Move(folder, old);
            CreateDevPackageFolder(folder);
            TryDeleteDirectory(old);
        }

        await WaitUntilAsync(() => Volatile.Read(ref commitCount) >= 1);
        await Task.Delay(250);
        var afterStorm = commitCount;
        await File.AppendAllTextAsync(GetDevPackagePayloadPath(folder), "rebound");
        await WaitUntilAsync(() => Volatile.Read(ref commitCount) > afterStorm);
        TryDeleteDirectory(parent);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DevWatcher_RemovingOrRebindingOneOwnerCancelsItsPendingStageWhileOtherOwnersStayLive(bool rebind, bool commitInFlight)
    {
        var first = CreateDevPackageFolder();
        var second = CreateDevPackageFolder();
        var replacement = rebind ? CreateDevPackageFolder() : null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = 0;
        var commits = 0;
        var events = new RuntimeEventStreamService();
        await using var watcher = new DevPackageWatchService(
            async (request, token) =>
            {
                if (Interlocked.Increment(ref stages) == 1)
                {
                    Assert.Equal(["first"], request.PackageIds);
                    if (!commitInFlight)
                    {
                        using var registration = token.Register(() => canceled.TrySetResult());
                        entered.TrySetResult();
                        await release.Task; // A late candidate must be discarded even if staging ignores cancellation.
                    }
                    return new("withdrawn-owner", [], [], [], request.PackageIds);
                }
                Assert.Equal(["second"], request.PackageIds);
                return new("live-owner", [], [], [], request.PackageIds);
            },
            async (stage, token) =>
            {
                if (stage == "withdrawn-owner")
                {
                    Assert.True(commitInFlight);
                    using var registration = token.Register(() => canceled.TrySetResult());
                    entered.TrySetResult();
                    await release.Task; // A stale commit response can also arrive after withdrawal.
                    return CreateReloadResult(false, "stage is stale because its source owner changed");
                }
                Assert.Equal("live-owner", stage);
                Interlocked.Increment(ref commits);
                committed.TrySetResult();
                return CreateReloadResult(true);
            },
            (stage, _) =>
            {
                Assert.Equal("withdrawn-owner", stage);
                discarded.TrySetResult();
                return Task.FromResult(true);
            },
            events, NullLogger<DevPackageWatchService>.Instance,
            (delay, token) => Task.Delay(delay == DevPackageWatchService.DebounceDelay ? TimeSpan.FromMilliseconds(50) : TimeSpan.Zero, token));
        try
        {
            await watcher.SynchronizeAsync([new("first", first), new("second", second)]);
            await File.AppendAllTextAsync(GetDevPackagePayloadPath(first), "first edit");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await watcher.SynchronizeAsync(rebind ? [new("first", replacement!), new("second", second)] : [new("second", second)]);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            await discarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref commits));
            Assert.DoesNotContain(events.GetSnapshot().Events, item => item.Kind == RuntimeEventKind.DevReloadCompleted && item.Success == false);
            await File.AppendAllTextAsync(GetDevPackagePayloadPath(second), "still owned");
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref commits));
        }
        finally
        {
            release.TrySetResult();
            await watcher.DisposeAsync();
            TryDeleteDirectory(Path.GetDirectoryName(first)!);
            TryDeleteDirectory(Path.GetDirectoryName(second)!);
            if (replacement is not null) TryDeleteDirectory(Path.GetDirectoryName(replacement)!);
        }
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
        await watcher.SynchronizeAsync([new DevPackageWatchTarget("test.package", folder)]);

        await File.AppendAllTextAsync(GetDevPackagePayloadPath(folder), "stale");
        await WaitUntilAsync(() => events.GetSnapshot().Events.Any(item => item.Kind == RuntimeEventKind.DevReloadCompleted));

        var result = events.GetSnapshot().Events.Last(item => item.Kind == RuntimeEventKind.DevReloadCompleted);
        Assert.False(result.Success);
        Assert.Contains("stale", result.Message, StringComparison.OrdinalIgnoreCase);
        TryDeleteDirectory(folder);
    }

    [Fact]
    public async Task DevWatcher_ImportHandoffCreationAndEditsReloadWithoutReplacingTheDirectory()
    {
        var folder = CreateDevPackageFolder();
        var parent = Path.GetDirectoryName(folder)!;
        var commits = 0;
        var events = new RuntimeEventStreamService();
        await using var watcher = CreateWatcher(folder, events, commit: (_, _) =>
        {
            Interlocked.Increment(ref commits);
            return Task.FromResult(CreateReloadResult(true));
        });
        try
        {
            await watcher.SynchronizeAsync([new DevPackageWatchTarget("test.package", folder)]);
            var imports = folder + ".imports.v1.json";
            await File.WriteAllTextAsync(imports, "first handoff");
            await WaitUntilAsync(() => Volatile.Read(ref commits) > 0);
            var previous = Volatile.Read(ref commits);
            await File.WriteAllTextAsync(imports, "changed handoff");
            await WaitUntilAsync(() => Volatile.Read(ref commits) > previous);
        }
        finally
        {
            await watcher.DisposeAsync();
            TryDeleteDirectory(parent);
        }
    }

    [Fact]
    public async Task DevWatcher_WaitsForCompleteDirectoryAndImportHandoffPublication()
    {
        var folder = CreateDevPackageFolder();
        var parent = Path.GetDirectoryName(folder)!;
        var journal = folder + ".sunder-output-transaction.json";
        var imports = folder + ".imports.v1.json";
        await File.WriteAllTextAsync(journal, "publication in progress");
        var stabilityStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = 0;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var watcher = new DevPackageWatchService(
            (request, _) =>
            {
                Assert.False(File.Exists(journal));
                Assert.Equal("complete", File.ReadAllText(imports));
                Interlocked.Increment(ref stages);
                return Task.FromResult(CreateStage(request.PackageIds));
            },
            (_, _) => { committed.TrySetResult(); return Task.FromResult(CreateReloadResult(true)); },
            (_, _) => Task.FromResult(true), new RuntimeEventStreamService(), NullLogger<DevPackageWatchService>.Instance,
            async (delay, token) =>
            {
                if (delay == DevPackageWatchService.DebounceDelay) await Task.Delay(50, token);
                else { stabilityStarted.TrySetResult(); await finishPublication.Task.WaitAsync(token); }
            });
        try
        {
            await watcher.SynchronizeAsync([new DevPackageWatchTarget("test.package", folder)]);
            await File.AppendAllTextAsync(GetDevPackagePayloadPath(folder), "rebuilt");
            await stabilityStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref stages));
            await File.WriteAllTextAsync(imports, "complete");
            File.Delete(journal);
            finishPublication.TrySetResult();
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            finishPublication.TrySetResult();
            await watcher.DisposeAsync();
            TryDeleteDirectory(parent);
        }
    }

    [Fact]
    public async Task DevWatcher_CompletedTransactionUsesStrictStageWithoutDebounceOrStabilityDelay()
    {
        var folder = CreateDevPackageFolder();
        var journal = folder + ".sunder-output-transaction.json";
        File.WriteAllText(journal, "publication in progress");
        var logs = new RecordingLogSink();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageCount = 0;
        var discarded = 0;
        CancellationToken stageToken = default;
        await using var watcher = new DevPackageWatchService(
            async (request, token) =>
            {
                Assert.False(File.Exists(journal));
                var validation = await Sunder.Package.Format.SunderPackageArchiveInspector.ValidateExtractedPackageAsync(folder, token);
                Assert.True(validation.Success, string.Join(" | ", validation.Errors));
                Interlocked.Increment(ref stageCount);
                stageToken = token;
                staging.TrySetResult();
                await releaseStage.Task.WaitAsync(token);
                return CreateStage(request.PackageIds);
            },
            (_, _) => { committed.TrySetResult(); return Task.FromResult(CreateReloadResult(true)); },
            (_, _) => { Interlocked.Increment(ref discarded); return Task.FromResult(true); },
            new RuntimeEventStreamService(), logs.CreateLogger<DevPackageWatchService>(),
            (_, token) =>
            {
                waiting.TrySetResult();
                // No debounce or stability delay is ever allowed to finish. A
                // completed publication must make progress by canceling/bypassing
                // these waits, regardless of pre-journal native event ordering.
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        try
        {
            await watcher.SynchronizeAsync([new DevPackageWatchTarget("test.package", folder)]);
            File.SetLastWriteTimeUtc(GetDevPackagePayloadPath(folder), DateTime.UtcNow);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(journal);
            await staging.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // A delayed child notification observes the same published files;
            // parent Changed must not recreate its watcher and replay events.
            File.SetLastWriteTimeUtc(GetDevPackagePayloadPath(folder), File.GetLastWriteTimeUtc(GetDevPackagePayloadPath(folder)));
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
            await WaitUntilAsync(() => logs.Entries.Any(entry => entry.Message.Contains("Coalesced delayed notification", StringComparison.Ordinal)));
            Assert.False(stageToken.IsCancellationRequested);
            releaseStage.TrySetResult();
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref stageCount));
            Assert.Equal(0, Volatile.Read(ref discarded));
        }
        finally
        {
            releaseStage.TrySetResult();
            await watcher.DisposeAsync();
            TryDeleteDirectory(Path.GetDirectoryName(folder)!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevWatcher_InterruptedStagingCarriesDirtyPackagesForwardAndDiscardsCanceledCandidate(bool withdrawFirst)
    {
        var first = CreateDevPackageFolder();
        var second = CreateDevPackageFolder();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new List<string>();
        var attempts = 0;
        await using var watcher = new DevPackageWatchService(
            async (request, token) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    Assert.Equal(["first"], request.PackageIds);
                    using var registration = token.Register(() => canceled.TrySetResult());
                    entered.TrySetResult();
                    await release.Task;
                    return new("interrupted", [], [], [], request.PackageIds);
                }
                Assert.Equal(withdrawFirst ? ["second"] : new[] { "first", "second" }, request.PackageIds.Order(StringComparer.Ordinal));
                return new("latest", [], [], [], request.PackageIds);
            },
            (stage, _) =>
            {
                Assert.Equal("latest", stage);
                committed.TrySetResult();
                return Task.FromResult(CreateReloadResult(true));
            },
            (stage, _) => { discarded.Add(stage); return Task.FromResult(true); },
            new RuntimeEventStreamService(), NullLogger<DevPackageWatchService>.Instance,
            (delay, token) => Task.Delay(delay == DevPackageWatchService.DebounceDelay ? TimeSpan.FromMilliseconds(50) : TimeSpan.Zero, token));
        try
        {
            await watcher.SynchronizeAsync([new("first", first), new("second", second)]);
            await File.AppendAllTextAsync(GetDevPackagePayloadPath(first), "first edit");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.AppendAllTextAsync(GetDevPackagePayloadPath(second), "second edit");
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (withdrawFirst) await watcher.SynchronizeAsync([new("second", second)]);
            release.TrySetResult();
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["interrupted"], discarded);
        }
        finally
        {
            release.TrySetResult();
            await watcher.DisposeAsync();
            TryDeleteDirectory(Path.GetDirectoryName(first)!);
            TryDeleteDirectory(Path.GetDirectoryName(second)!);
        }
    }

    [Fact]
    public void AppProductionCode_DoesNotWatchOrDiscoverPackageRoots()
    {
        var appRoot = Path.Combine(RepositoryRoot, "languages", "csharp", "dotnet", "host", "Sunder.App");
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
            stage ?? ((request, _) => Task.FromResult(CreateStage(request.PackageIds))),
            commit ?? ((_, _) => Task.FromResult(CreateReloadResult(success: true))),
            (_, _) => Task.FromResult(true),
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
        => new("stage-1", [], [], [], packageIds);

    private static PackageLifecycleOperationResult CreateReloadResult(bool success, string? error = null)
        => new(success, success ? "reloaded" : error, [], [], error is null ? [] : [error], ["test.package"]);

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
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            path,
            "test.package",
            "1.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        return path;
    }

    private static string GetDevPackagePayloadPath(string folder)
        => Path.Combine(
            folder,
            "payload",
            "shared",
            "lib",
            Path.GetFileName(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location));

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
