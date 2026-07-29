using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ActivePackageSessionTests
{
    [Fact]
    public void MarkPackageFailed_DisablesSessionPackageAndReturnsLoadedPackageForDeactivation()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            });

        var marked = session.MarkPackageFailed(
            "test.package",
            PackageFailureOrigin.RuntimeActivation,
            "Activation failed.",
            out var packageToDeactivate);

        Assert.True(marked);
        Assert.Same(loadedPackage, packageToDeactivate);
        Assert.Empty(session.GetActivePackages());
        Assert.False(session.TryGetLoadedPackage("test.package", out _));

        var failedPackage = Assert.Single(session.GetSessionPackages());
        Assert.False(failedPackage.IsEnabled);
        Assert.Equal(PackageReadinessState.Failed, failedPackage.Readiness);
        Assert.Equal(PackageFailureOrigin.RuntimeActivation, failedPackage.FailureOrigin);
        Assert.Equal("Activation failed.", failedPackage.LastError);
        Assert.Equal(1, failedPackage.FailureCount);
        Assert.NotNull(failedPackage.LastFailureAtUtc);
    }

    [Fact]
    public void MarkPackageFailed_RemovesPackageContributionsFromRuntimeCatalog()
    {
        var extensionPoint = new PackageExtensionPoint<ITestContribution>("test:contribution");
        var extensionCatalog = new RuntimePackageExtensionCatalog();
        var extensionOwner = extensionCatalog.BeginOwnerActivation("test.package");
        extensionCatalog.Add(extensionOwner, extensionPoint, new TestContribution("test"));
        extensionCatalog.Add("other.package", extensionPoint, new TestContribution("other"));
        var loadedPackage = CreateLoadedPackage("test.package") with
        {
            ExtensionOwner = extensionOwner,
        };
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            },
            extensionCatalog);

        session.MarkPackageFailed(
            "TEST.PACKAGE",
            PackageFailureOrigin.RuntimeActivation,
            "Activation failed.",
            out _);

        var contribution = Assert.Single(extensionCatalog.GetExtensions(extensionPoint));
        Assert.Equal("other", contribution.Name);
    }

    [Fact]
    public void DisableInstalledPackage_DisablesPackageAndReturnsLoadedPackageForDeactivation()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            });

        var disabled = session.DisableInstalledPackage("test.package", out var packageToDeactivate);

        Assert.True(disabled);
        Assert.Same(loadedPackage, packageToDeactivate);
        var sessionPackage = Assert.Single(session.GetSessionPackages());
        Assert.False(sessionPackage.IsEnabled);
        Assert.Equal(PackageReadinessState.Disabled, sessionPackage.Readiness);
        Assert.False(session.TryGetLoadedPackage("test.package", out _));
    }

    [Fact]
    public void RemovePackage_RemovesSessionAndLoadedPackage()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            });

        var removed = session.RemovePackage("test.package", out var packageToDeactivate);

        Assert.True(removed);
        Assert.Same(loadedPackage, packageToDeactivate);
        Assert.Empty(session.GetSessionPackages());
        Assert.False(session.TryGetLoadedPackage("test.package", out _));
    }

    [Fact]
    public async Task DisposeAsync_ReleasesSessionFolder()
    {
        var sessionFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);
        try
        {
            var session = new ActivePackageSession(
                sessionFolder,
                new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase));

            await session.DisposeAsync();

            Assert.False(Directory.Exists(sessionFolder));
        }
        finally
        {
            TryDeleteDirectory(sessionFolder);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_CommitsGenerationOnlyAfterBackgroundServicesStart()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        var events = new RuntimeEventStreamService();
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events,
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var publisher = new PackageSessionPublisher(owner, ui, NullLogger<PackageSessionPublisher>.Instance);
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundService = new GenerationAwareBackgroundService(
            () => owner.Generation,
            startEntered,
            allowStart.Task);
        var loadedPackage = CreateLoadedPackage("test.package", backgroundService);
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            },
            backgroundServicesStarted: false);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            Assert.Equal(0, backgroundService.StartCount);
            Assert.Equal(0, owner.Generation);

            var commit = publisher.CommitAsync(pending);
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, owner.Generation);
            allowStart.TrySetResult();
            var committed = await commit;

            Assert.Equal(1, committed.Stamp.SessionGeneration);
            Assert.Equal(1, backgroundService.StartCount);
            Assert.Equal(0, backgroundService.GenerationObservedAtStart);
            Assert.Equal(1, backgroundService.CommitCount);
            Assert.Equal(1, backgroundService.GenerationObservedAtCommit);
            Assert.Equal(1, backgroundService.CommittedGeneration?.SessionGeneration);
            Assert.Equal(loadedPackage.RuntimeActivationId, backgroundService.CommittedGeneration?.ActivationId);
        }
        finally
        {
            allowStart.TrySetResult();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_StartupTimeoutDoesNotPublishAndStopsAttemptedService()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageBackgroundServiceStartupTimeout = TimeSpan.FromMilliseconds(50),
            PackageBackgroundServiceCleanupTimeout = TimeSpan.FromMilliseconds(200),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            ui,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var backgroundService = new HangingStartBackgroundService();
        var session = CreateSession("test.package", backgroundService);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<TimeoutException>(() => publisher.CommitAsync(pending));

            Assert.Contains("did not start", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, owner.Generation);
            Assert.Equal(1, backgroundService.StopCount);
            using var lease = owner.State.AcquireLease();
            Assert.Equal(0, lease.Generation);
        }
        finally
        {
            backgroundService.Release();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_CandidateStartFailureLeavesCommittedGenerationActive()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var publisher = new PackageSessionPublisher(owner, ui, NullLogger<PackageSessionPublisher>.Instance);
        var activeService = new RecordingGenerationBackgroundService();
        var activeSession = CreateSession("active.package", activeService);
        var failingService = new ImmediateFailingGenerationBackgroundService();
        var candidateSession = CreateSession("candidate.package", failingService);

        try
        {
            var initial = publisher.Prepare(activeSession, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var initialPending = await publisher.BeginPublishAsync(initial, CancellationToken.None);
            await publisher.CommitAsync(initialPending);

            var candidate = publisher.Prepare(candidateSession, owner.Sources.Snapshot(), [], [], baseGeneration: 1);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.CommitAsync(pending));

            Assert.Equal(1, owner.Generation);
            Assert.Equal(1, activeService.StartCount);
            Assert.Equal(1, activeService.CommitCount);
            Assert.Equal(0, activeService.StopCount);
            Assert.Equal(1, failingService.StartCount);
            Assert.Equal(0, failingService.CommitCount);
            Assert.Equal(1, failingService.StopCount);
            using var lease = owner.State.AcquireLease();
            Assert.Same(activeSession, lease.Session);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_RuntimeGenerationFaultDisablesExactActivePackage()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var publisher = new PackageSessionPublisher(owner, ui, NullLogger<PackageSessionPublisher>.Instance);
        var service = new FaultingGenerationBackgroundService();
        var session = CreateSession("test.package", service);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);
            var publication = await publisher.CommitAsync(pending);

            service.Fail(new InvalidOperationException("Generation lease was stolen."));
            var failed = await WaitForPackageAsync(
                owner,
                "test.package",
                package => package.Readiness == PackageReadinessState.Failed);

            Assert.Equal(PackageFailureOrigin.RuntimeBackgroundService, failed.FailureOrigin);
            Assert.Contains("Generation lease was stolen.", failed.LastError, StringComparison.Ordinal);
            Assert.Equal(publication.Stamp.SessionGeneration + 1, owner.Generation);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task DelayedFaultCommit_CannotRegressNewerSnapshotEventsSourcesOrUi()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var initialFolder = Path.Combine(rootPath, "initial-session");
        var replacementFolder = Path.Combine(rootPath, "replacement-session");
        Directory.CreateDirectory(initialFolder);
        Directory.CreateDirectory(replacementFolder);
        var paths = new RuntimePackagePaths(rootPath);
        var events = new RuntimeEventStreamService();
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events,
            uiSnapshots: snapshots);
        var initialSession = CreateSession(
            "fault.package",
            initialFolder,
            serviceProvider: null,
            backgroundServicesStarted: true);
        var replacementSession = CreateSession(
            "replacement.package",
            replacementFolder,
            serviceProvider: null,
            backgroundServicesStarted: true);
        var initialSource = Assert.Single(initialSession.GetActivePackageSources());
        var replacementSource = Assert.Single(replacementSession.GetActivePackageSources());
        var initialSources = new PackageSessionSourceState().Snapshot();
        initialSources.SetDevOverlay(new PackageSessionDevOverlay(
            "fault.package",
            initialSource.SourceFolder,
            Watch: false,
            PackageSessionOverlayOwner.Startup));
        var replacementSources = new PackageSessionSourceState().Snapshot();
        replacementSources.SetDevOverlay(new PackageSessionDevOverlay(
            "replacement.package",
            replacementSource.SourceFolder,
            Watch: false,
            PackageSessionOverlayOwner.Startup));
        var initialUi = snapshots.CreateSnapshots([initialSource], generation: 1);
        IReadOnlyList<PackageUiSnapshotDescriptor> faultUi = [];
        IReadOnlyList<PackageUiSnapshotDescriptor> replacementUi = [];
        var faultCommitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFaultCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? faultTask = null;
        Task<IReadOnlyList<string>>? replacementPublication = null;
        bool? staleCommitApplied = null;
        SessionPackageDescriptor[] faultedPackages = [];

        try
        {
            await owner.PublishAsync(
                initialSession,
                initialSources,
                initialUi,
                [],
                [],
                expectedGeneration: 0);
            faultUi = snapshots.CreateSnapshots([initialSource], generation: 2);
            replacementUi = snapshots.CreateSnapshots([replacementSource], generation: 3);
            PackageActivationIdentity activationIdentity;
            using (var lease = owner.State.AcquireLease())
            {
                var loadedPackage = Assert.IsType<ActiveLoadedPackage>(
                    owner.State.GetLoadedPackage(lease, "fault.package"));
                activationIdentity = lease.GetPackageActivationIdentity(loadedPackage);
            }

            faultTask = Task.Run(() => owner.State.HandlePackageFault(
                "fault.package",
                activationIdentity,
                expectedGeneration: 1,
                PackageFailureOrigin.RuntimeAuthentication,
                new InvalidOperationException("Exact activation fault."),
                "test delayed fault publication",
                committed: generation =>
                {
                    var activePackages = owner.State.GetActivePackages();
                    faultedPackages = owner.State.GetSessionPackages().ToArray();
                    faultCommitEntered.TrySetResult();
                    releaseFaultCommit.Task.GetAwaiter().GetResult();
                    staleCommitApplied = owner.CommitGeneration(
                        generation,
                        activePackages,
                        faultedPackages,
                        faultUi,
                        initialSources,
                        [],
                        ["Package 'fault.package' failed: Exact activation fault."]);
                }));
            await faultCommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var exactFault = Assert.Single(faultedPackages);
            Assert.Equal(PackageReadinessState.Failed, exactFault.Readiness);
            Assert.Equal(PackageFailureOrigin.RuntimeAuthentication, exactFault.FailureOrigin);
            Assert.Equal("Exact activation fault.", exactFault.LastError);
            Assert.Equal(2, owner.Generation);

            replacementPublication = owner.PublishAsync(
                replacementSession,
                replacementSources,
                replacementUi,
                ["newer warning"],
                ["newer error"],
                expectedGeneration: 2);
            await WaitForGenerationAsync(owner, generation: 3);

            var newerSnapshot = owner.GetSnapshot();
            Assert.Equal(3, newerSnapshot.SessionGeneration);
            Assert.Equal("replacement.package", Assert.Single(newerSnapshot.ActivePackages).PackageId);
            Assert.Equal("replacement.package", Assert.Single(newerSnapshot.PackageUiSnapshots).PackageId);
            Assert.Equal("replacement.package", Assert.Single(owner.Sources.Snapshot().ActiveDevOverlays).PackageId);
            Assert.Contains("newer warning", newerSnapshot.Warnings);
            Assert.Contains("newer error", newerSnapshot.Errors);
            using (var newerUiLease = snapshots.Acquire(
                       Assert.Single(replacementUi).SnapshotId,
                       generation: 3,
                       stageId: null))
            {
                Assert.NotNull(newerUiLease);
            }

            releaseFaultCommit.TrySetResult();
            Assert.True(await faultTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await replacementPublication.WaitAsync(TimeSpan.FromSeconds(5));

            var finalSnapshot = owner.GetSnapshot();
            Assert.Equal(3, owner.Generation);
            Assert.Equal("replacement.package", Assert.Single(owner.State.GetActivePackages()).PackageId);
            Assert.Equal(newerSnapshot.SessionGeneration, finalSnapshot.SessionGeneration);
            Assert.Equal(newerSnapshot.ActivePackages, finalSnapshot.ActivePackages);
            Assert.Equal(newerSnapshot.PackageUiSnapshots, finalSnapshot.PackageUiSnapshots);
            Assert.False(staleCommitApplied);
            Assert.Equal(3, events.GetSnapshot().SessionGeneration);
            var publishedGenerations = events.GetSnapshot().Events
                .Where(runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.SessionGenerationChanged)
                .Select(runtimeEvent => runtimeEvent.SessionGeneration)
                .ToArray();
            Assert.Equal([1L, 3L], publishedGenerations);
            Assert.Equal(publishedGenerations.Order(), publishedGenerations);
            Assert.Null(snapshots.Acquire(Assert.Single(faultUi).SnapshotId, generation: 2, stageId: null));
            using var finalUiLease = snapshots.Acquire(
                Assert.Single(replacementUi).SnapshotId,
                generation: 3,
                stageId: null);
            Assert.NotNull(finalUiLease);
            Assert.Equal("replacement.package", Assert.Single(owner.Sources.Snapshot().ActiveDevOverlays).PackageId);
        }
        finally
        {
            releaseFaultCommit.TrySetResult();
            if (faultTask is not null)
            {
                await faultTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (replacementPublication is not null)
            {
                await replacementPublication.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_BlockedGenerationActivationNeverBecomesLeasableAndTimesOutToEmptySession()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            packageOperationPolicy: new RuntimePackageOperationPolicyOptions
            {
                SessionDrainTimeout = TimeSpan.FromMilliseconds(50),
            },
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageRuntimeGenerationActivationTimeout = TimeSpan.FromMilliseconds(50),
            PackageBackgroundServiceCleanupTimeout = TimeSpan.FromMilliseconds(50),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            ui,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var service = new BlockingGenerationBackgroundService();
        var session = CreateSession("test.package", service);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            var commit = publisher.CommitAsync(pending);
            await service.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, owner.Generation);
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());

            await Assert.ThrowsAsync<TimeoutException>(() => commit.WaitAsync(TimeSpan.FromSeconds(2)));
            await service.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, owner.Generation);
            Assert.Same(ActivePackageSession.Empty, owner.State.ActiveSession);
            Assert.Empty(owner.GetSnapshot().ActivePackages);
            using var lease = owner.State.AcquireLease();
            Assert.Same(ActivePackageSession.Empty, lease.Session);
            Assert.Equal(1, service.CommitCount);
            Assert.Equal(0, service.StopCount);

            service.ReleaseCommit();
            await service.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, service.StopCount);
        }
        finally
        {
            service.ReleaseCommit();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_BlockedGenerationActivationBecomesLeasableAfterExactCommit()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var publisher = new PackageSessionPublisher(owner, ui, NullLogger<PackageSessionPublisher>.Instance);
        var service = new BlockingGenerationBackgroundService();
        var session = CreateSession("test.package", service);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);
            var commit = publisher.CommitAsync(pending);
            await service.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var published = owner.GetSnapshot();
            Assert.Equal(1, published.SessionGeneration);
            Assert.Contains(published.ActivePackages, package => package.PackageId == "test.package");
            Assert.Throws<RuntimeUnavailableException>(() => owner.State.AcquireLease());

            service.ReleaseCommit();
            var result = await commit.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(published.SessionGeneration, result.Stamp.SessionGeneration);
            using var lease = owner.State.AcquireLease();
            Assert.Same(session, lease.Session);
            Assert.Equal(result.Stamp.SessionGeneration, lease.Generation);
            Assert.Equal(1, service.CommitCount);
        }
        finally
        {
            service.ReleaseCommit();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_RepeatedGenerationActivationFailureLeavesNoHalfStartedPackage()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var publisher = new PackageSessionPublisher(
            owner,
            ui,
            NullLogger<PackageSessionPublisher>.Instance,
            new RuntimeLifecyclePolicyOptions
            {
                PackageRuntimeGenerationActivationAttempts = 2,
            });
        var service = new RepeatedlyFailingGenerationBackgroundService();
        var session = CreateSession("test.package", service);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => publisher.CommitAsync(pending));

            Assert.Contains("activation failed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, service.CommitCount);
            Assert.Equal(1, service.StopCount);
            Assert.Equal(2, owner.Generation);
            Assert.Same(ActivePackageSession.Empty, owner.State.ActiveSession);
            Assert.Empty(owner.GetSnapshot().ActivePackages);
        }
        finally
        {
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_SequentialGenerationFaultsDisableEachExactPackageOwner()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var publisher = new PackageSessionPublisher(owner, ui, NullLogger<PackageSessionPublisher>.Instance);
        var firstService = new FaultingGenerationBackgroundService();
        var secondService = new FaultingGenerationBackgroundService();
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["first.package"] = CreateLoadedPackage("first.package", firstService),
                ["second.package"] = CreateLoadedPackage("second.package", secondService),
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["first.package"] = CreateSessionPackage("first.package", isEnabled: true),
                ["second.package"] = CreateSessionPackage("second.package", isEnabled: true),
            },
            backgroundServicesStarted: false);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);
            var publication = await publisher.CommitAsync(pending);

            firstService.Fail(new InvalidOperationException("First generation failed."));
            var first = await WaitForPackageAsync(
                owner,
                "first.package",
                package => package.Readiness == PackageReadinessState.Failed);

            secondService.Fail(new InvalidOperationException("Second generation failed later."));
            var second = await WaitForPackageAsync(
                owner,
                "second.package",
                package => package.Readiness == PackageReadinessState.Failed);

            Assert.Equal(PackageFailureOrigin.RuntimeBackgroundService, first.FailureOrigin);
            Assert.Equal(PackageFailureOrigin.RuntimeBackgroundService, second.FailureOrigin);
            Assert.Contains("First generation failed.", first.LastError, StringComparison.Ordinal);
            Assert.Contains("Second generation failed later.", second.LastError, StringComparison.Ordinal);
            Assert.Equal(publication.Stamp.SessionGeneration + 2, owner.Generation);
            Assert.Empty(owner.State.GetActivePackages());
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_StartupTimeoutQuarantinesSessionUntilIgnoredStartCompletes()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "candidate-session");
        Directory.CreateDirectory(sessionFolder);
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageBackgroundServiceStartupTimeout = TimeSpan.FromMilliseconds(40),
            PackageBackgroundServiceCleanupTimeout = TimeSpan.FromMilliseconds(40),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            ui,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var backgroundService = new CancellationIgnoringStartBackgroundService();
        var serviceProvider = new TrackingServiceProvider();
        var session = CreateSession(
            "test.package",
            sessionFolder,
            serviceProvider,
            false,
            backgroundService);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            await Assert.ThrowsAsync<TimeoutException>(() => publisher.CommitAsync(pending));

            Assert.Equal(0, owner.Generation);
            Assert.Equal(1, backgroundService.StopCount);
            Assert.True(Directory.Exists(sessionFolder));
            Assert.False(serviceProvider.IsDisposed);

            backgroundService.ReleaseStart();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));

            Assert.True(serviceProvider.IsDisposed);
            Assert.False(Directory.Exists(sessionFolder));
        }
        finally
        {
            backgroundService.ReleaseStart();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PublishSessionAsync_StopTimeoutQuarantinesRetiredSessionUntilIgnoredStopCompletes()
    {
        var state = new PackageSessionState(
            NullLogger.Instance,
            static () => { },
            static _ => { },
            TimeSpan.FromMilliseconds(50));
        var oldFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var newFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(oldFolder);
        Directory.CreateDirectory(newFolder);
        var backgroundService = new CancellationIgnoringStopBackgroundService();
        var serviceProvider = new TrackingServiceProvider();
        var oldSession = CreateSession(
            "old.package",
            oldFolder,
            serviceProvider,
            true,
            backgroundService);
        var newSession = new ActivePackageSession(
            newFolder,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase));

        try
        {
            await state.PublishSessionAsync(oldSession);

            var publication = await state.PublishSessionAsync(newSession);

            Assert.Contains(publication.Warnings, warning => warning.Contains("quarantined", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, backgroundService.StopCount);
            Assert.True(Directory.Exists(oldFolder));
            Assert.False(serviceProvider.IsDisposed);

            backgroundService.ReleaseStop();
            await oldSession.DisposeAsync(TimeSpan.FromSeconds(2));

            Assert.True(serviceProvider.IsDisposed);
            Assert.False(Directory.Exists(oldFolder));
        }
        finally
        {
            backgroundService.ReleaseStop();
            await oldSession.DisposeAsync(TimeSpan.FromSeconds(2));
            await state.ClearActiveSessionAsync();
            TryDeleteDirectory(oldFolder);
            TryDeleteDirectory(newFolder);
        }
    }

    [Fact]
    public async Task DisposeAsync_LeakedExtensionLeaseQuarantinesProviderAndLoadContextUntilCallbackExits()
    {
        var extensionPoint = new PackageExtensionPoint<ITestContribution>("test:leased-callback");
        var extensionCatalog = new RuntimePackageExtensionCatalog();
        var extensionOwner = extensionCatalog.BeginOwnerActivation("test.package");
        extensionCatalog.Add(extensionOwner, extensionPoint, new TestContribution("leased"));
        var reference = Assert.Single(extensionCatalog.GetExtensionReferences(extensionPoint));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = false;
        var providerDisposedDuringCallback = false;
        var loadContextUnloadedDuringCallback = false;
        var provider = new TrackingServiceProvider(() => providerDisposedDuringCallback = !Volatile.Read(ref callbackExited));
        var loadedPackage = CreateLoadedPackageCore(
            "test.package",
            sessionFolder: null,
            provider,
            backgroundServices: []) with
        {
            ExtensionOwner = extensionOwner,
        };
        loadedPackage.LoadContext.Unloading += _ =>
            loadContextUnloadedDuringCallback = !Volatile.Read(ref callbackExited);
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            },
            extensionCatalog);
        CancellationToken retirementToken = default;
        var callback = Task.Run(async () =>
        {
            Assert.True(reference.TryAcquire(out var lease));
            using (lease)
            {
                retirementToken = lease.RetirementToken;
                Assert.Equal("leased", lease.Contribution.Name);
                callbackStarted.TrySetResult();
                await releaseCallback.Task;
                Assert.Equal("leased", lease.Contribution.Name);
                Volatile.Write(ref callbackExited, true);
            }
        });

        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await Assert.ThrowsAsync<TimeoutException>(
                () => session.DisposeAsync(TimeSpan.FromMilliseconds(40)));

            Assert.True(retirementToken.IsCancellationRequested);
            Assert.False(reference.TryAcquire(out _));
            Assert.False(provider.IsDisposed);
            Assert.False(providerDisposedDuringCallback);
            Assert.False(loadContextUnloadedDuringCallback);

            releaseCallback.TrySetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(2));
            await session.DisposeAsync(TimeSpan.FromSeconds(2));

            Assert.True(provider.IsDisposed);
            Assert.False(providerDisposedDuringCallback);
            Assert.False(loadContextUnloadedDuringCallback);
        }
        finally
        {
            releaseCallback.TrySetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(2));
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DisposeAsync_InFlightGenerationCommitQuarantinesProviderUntilCallbackExits()
    {
        var sessionFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);
        var provider = new TrackingServiceProvider();
        var generationParticipant = new HangingGenerationBackgroundService();
        var session = CreateSession(
            "test.package",
            sessionFolder,
            provider,
            backgroundServicesStarted: true,
            generationParticipant);

        try
        {
            var commit = session.CommitRuntimeGenerationAsync(1);
            await generationParticipant.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await Assert.ThrowsAsync<TimeoutException>(
                () => session.DisposeAsync(TimeSpan.FromMilliseconds(40)));

            Assert.False(provider.IsDisposed);
            generationParticipant.ReleaseCommit();
            await commit.WaitAsync(TimeSpan.FromSeconds(2));
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            Assert.True(provider.IsDisposed);
        }
        finally
        {
            generationParticipant.ReleaseCommit();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            TryDeleteDirectory(sessionFolder);
        }
    }

    [Fact]
    public async Task DisposeAsync_ChangedReentrancyUsesInstalledSingleFlightTask()
    {
        var extensionPoint = new PackageExtensionPoint<ITestContribution>("test:dispose-reentrancy");
        var extensionCatalog = new RuntimePackageExtensionCatalog();
        var extensionOwner = extensionCatalog.BeginOwnerActivation("test.package");
        extensionCatalog.Add(extensionOwner, extensionPoint, new TestContribution("active"));
        var loadedPackage = CreateLoadedPackage("test.package") with
        {
            ExtensionOwner = extensionOwner,
        };
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            },
            extensionCatalog);
        Task? reentrantDisposal = null;
        extensionCatalog.Changed += (_, change) =>
        {
            if (change.Changes.Any(static item => item.Kind == PackageExtensionChangeKind.Removed))
            {
                reentrantDisposal = session.DisposeAsync(TimeSpan.FromSeconds(2));
            }
        };

        var disposal = session.DisposeAsync(TimeSpan.FromSeconds(2));

        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.IsAssignableFrom<Task>(reentrantDisposal).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PackageSessionPublisher_FailingServiceCleanupAttemptsAllServicesAndIsBounded()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageBackgroundServiceStartupTimeout = TimeSpan.FromSeconds(1),
            PackageBackgroundServiceCleanupTimeout = TimeSpan.FromMilliseconds(50),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            ui,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var startedService = new TestBackgroundService();
        var failingService = new FailingStartBackgroundService();
        var session = CreateSession("test.package", startedService, failingService);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => publisher.CommitAsync(pending).WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(0, owner.Generation);
            Assert.Equal(1, startedService.StopCount);
            Assert.Equal(1, failingService.StopCount);
        }
        finally
        {
            failingService.Release();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void CleanupStaleSessions_RemovesFoldersWithoutRunningOwner()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var activeFolder = Path.Combine(rootPath, $"20260511190000-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var staleFolder = Path.Combine(rootPath, $"20260511190001-{int.MaxValue}-{Guid.NewGuid():N}");
        var legacyFolder = Path.Combine(rootPath, $"20260511190002-{Guid.NewGuid():N}");
        Directory.CreateDirectory(activeFolder);
        Directory.CreateDirectory(staleFolder);
        Directory.CreateDirectory(legacyFolder);
        try
        {
            RuntimePackageSessionDirectories.CleanupStaleSessions(rootPath);

            Assert.True(Directory.Exists(activeFolder));
            Assert.False(Directory.Exists(staleFolder));
            Assert.False(Directory.Exists(legacyFolder));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    private static ActivePackageSession CreateSession(
        string packageId,
        params IPackageBackgroundService[] backgroundServices)
        => CreateSession(
            packageId,
            sessionFolder: null,
            serviceProvider: null,
            backgroundServicesStarted: false,
            backgroundServices: backgroundServices);

    private static ActivePackageSession CreateSession(
        string packageId,
        string? sessionFolder,
        IServiceProvider? serviceProvider,
        bool backgroundServicesStarted,
        params IPackageBackgroundService[] backgroundServices)
    {
        var loadedPackage = CreateLoadedPackageCore(packageId, sessionFolder, serviceProvider, backgroundServices);
        return new ActivePackageSession(
            sessionFolder,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = CreateSessionPackage(packageId, isEnabled: true),
            },
            backgroundServicesStarted: backgroundServicesStarted);
    }

    private static ActiveLoadedPackage CreateLoadedPackage(
        string packageId,
        params IPackageBackgroundService[] backgroundServices)
        => CreateLoadedPackageCore(
            packageId,
            sessionFolder: null,
            serviceProvider: null,
            backgroundServices: backgroundServices);

    private static ActiveLoadedPackage CreateLoadedPackageCore(
        string packageId,
        string? sessionFolder,
        IServiceProvider? serviceProvider,
        IReadOnlyList<IPackageBackgroundService> backgroundServices)
    {
        var tempDirectory = sessionFolder is null
            ? Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"))
            : Path.Combine(sessionFolder, "package-content");
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(Path.Combine(tempDirectory, "sunder-package.json"), "{}");
        var assemblyPath = typeof(PackageLoadPlanner).Assembly.Location;
        serviceProvider ??= new ServiceCollection().BuildServiceProvider();

        return new ActiveLoadedPackage(
            CreateActivePackage(packageId, isEnabled: true, PackageReadinessState.Ready),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, tempDirectory),
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(tempDirectory, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(tempDirectory, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            AuthHandler: null,
            CallbackHandlers: new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
            BackgroundServices: backgroundServices.Count == 0
                ? [new TestBackgroundService()]
                : backgroundServices,
            serviceProvider,
            new RuntimePackageLoadContext(
                packageId,
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            EmptyTestPackageSettings.Instance);
    }

    private static ActivePackageDescriptor CreateActivePackage(
        string packageId,
        bool isEnabled,
        PackageReadinessState readiness)
        => new(packageId, packageId, "1.0.0", PackageHostRoles.Runtime, Icon: null, isEnabled, readiness, Views: []);

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static SessionPackageDescriptor CreateSessionPackage(string packageId, bool isEnabled)
        => new(
            packageId,
            packageId,
            "1.0.0",
            PackageHostRoles.Runtime,
            Icon: null,
            isEnabled,
            isEnabled ? PackageReadinessState.Ready : PackageReadinessState.Failed,
            Views: [],
            FailureOrigin: null,
            LastError: null,
            LastFailureAtUtc: null,
            FailureCount: 0);

    private sealed class TestBackgroundService(
        Func<long>? getGeneration = null,
        TaskCompletionSource? startEntered = null,
        Task? allowStart = null) : IPackageBackgroundService
    {
        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public long? GenerationObservedAtStart { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            GenerationObservedAtStart = getGeneration?.Invoke();
            startEntered?.TrySetResult();
            if (allowStart is not null)
            {
                await allowStart.WaitAsync(cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class GenerationAwareBackgroundService(
        Func<long> getGeneration,
        TaskCompletionSource startEntered,
        Task allowStart) : IPackageRuntimeGenerationParticipant
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int CommitCount { get; private set; }
        public long? GenerationObservedAtStart { get; private set; }
        public long? GenerationObservedAtCommit { get; private set; }
        public PackageRuntimeGeneration? CommittedGeneration { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            GenerationObservedAtStart = getGeneration();
            startEntered.TrySetResult();
            await allowStart.WaitAsync(cancellationToken);
        }

        public Task CommitGenerationAsync(
            PackageRuntimeGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;
            GenerationObservedAtCommit = getGeneration();
            CommittedGeneration = generation;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGenerationBackgroundService : IPackageRuntimeGenerationParticipant
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int CommitCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task CommitGenerationAsync(
            PackageRuntimeGeneration generation,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateFailingGenerationBackgroundService : IPackageRuntimeGenerationParticipant
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int CommitCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            throw new InvalidOperationException("Candidate startup failed.");
        }

        public Task CommitGenerationAsync(
            PackageRuntimeGeneration generation,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class HangingGenerationBackgroundService : IPackageRuntimeGenerationParticipant
    {
        private readonly TaskCompletionSource _releaseCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task CommitGenerationAsync(
            PackageRuntimeGeneration generation,
            CancellationToken cancellationToken = default)
        {
            CommitEntered.TrySetResult();
            await _releaseCommit.Task;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ReleaseCommit() => _releaseCommit.TrySetResult();
    }

    private sealed class BlockingGenerationBackgroundService : IPackageRuntimeGenerationParticipant
    {
        private readonly TaskCompletionSource _releaseCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CommitCount { get; private set; }
        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task CommitGenerationAsync(
            PackageRuntimeGeneration generation,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            using var registration = cancellationToken.Register(CancellationObserved.SetResult);
            CommitEntered.TrySetResult();
            await _releaseCommit.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            StopObserved.TrySetResult();
            return Task.CompletedTask;
        }

        public void ReleaseCommit() => _releaseCommit.TrySetResult();
    }

    private sealed class RepeatedlyFailingGenerationBackgroundService : IPackageRuntimeGenerationParticipant
    {
        public int CommitCount { get; private set; }
        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CommitGenerationAsync(
            PackageRuntimeGeneration generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;
            throw new InvalidOperationException("Runtime generation activation failed.");
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingGenerationBackgroundService : IPackageRuntimeGenerationParticipant
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task GenerationCompletion => _completion.Task;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CommitGenerationAsync(
            PackageRuntimeGeneration generation,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    private static async Task<SessionPackageDescriptor> WaitForPackageAsync(
        RuntimeSessionOwner owner,
        string packageId,
        Func<SessionPackageDescriptor, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            var package = owner.State.GetSessionPackage(packageId);
            if (package is not null && predicate(package))
            {
                return package;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for package '{packageId}' state.");
            }
            await Task.Delay(20);
        }
    }

    private static async Task WaitForGenerationAsync(RuntimeSessionOwner owner, long generation)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (owner.GetSnapshot().SessionGeneration != generation)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for Runtime generation {generation}.");
            }
            await Task.Delay(20);
        }
    }

    private sealed class HangingStartBackgroundService : IPackageBackgroundService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _release.Task;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            _release.TrySetResult();
            return Task.CompletedTask;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class CancellationIgnoringStartBackgroundService : IPackageBackgroundService
    {
        private readonly TaskCompletionSource _startRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _startRelease.Task;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void ReleaseStart() => _startRelease.TrySetResult();
    }

    private sealed class CancellationIgnoringStopBackgroundService : IPackageBackgroundService
    {
        private readonly TaskCompletionSource _stopRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return _stopRelease.Task;
        }

        public void ReleaseStop() => _stopRelease.TrySetResult();
    }

    private sealed class TrackingServiceProvider(Action? onDisposing = null) : IServiceProvider, IAsyncDisposable
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public object? GetService(Type serviceType) => null;

        public ValueTask DisposeAsync()
        {
            onDisposing?.Invoke();
            Volatile.Write(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStartBackgroundService : IPackageBackgroundService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Background service startup failed.");

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            await _release.Task;
        }

        public void Release() => _release.TrySetResult();
    }

    private interface ITestContribution
    {
        string Name { get; }
    }

    private sealed record TestContribution(string Name) : ITestContribution;
}
