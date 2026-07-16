using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageViewHostServiceTests
{
    [Fact]
    public async Task ApplyPackageDeltaAsync_RuntimeOnlyPackageDoesNotMaterializeOrCreateAppLoadContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var package = CreateActivePackage("runtime.only") with { HostRoles = PackageHostRoles.Runtime };
        var downloadCalled = false;
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: root,
            downloadPackageUiSnapshotAsync: (_, _, _) =>
            {
                downloadCalled = true;
                return Task.CompletedTask;
            },
            uiDispatcher: TestUiDispatcher);
        try
        {
            await hostService.ApplyPackageDeltaAsync([package], []);

            Assert.False(downloadCalled);
            Assert.Equal(0, hostService.LoadedPackageCount);
            Assert.Equal(0, hostService.LoadContextCount);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(root);
        }
    }

    private static readonly IUiDispatcher TestUiDispatcher = new ImmediateUiDispatcher();

    [Fact]
    public void AppSharedAssemblyRegistry_ResolvesHostStackSdkAssembly()
    {
        using var registry = new AppSharedAssemblyRegistry([]);
        var stackSdkAssembly = typeof(IPackageStackExporter).Assembly;

        Assert.Same(stackSdkAssembly, registry.ResolveSharedAssembly(stackSdkAssembly.GetName()));
    }

    [Fact]
    public async Task DisablePackageAsync_PublishesPackageDisabledNotification()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var notificationCenter = new NotificationCenterService(Path.Combine(rootPath, "notifications.json"));
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null,
            notificationCenter: notificationCenter,
            uiDispatcher: TestUiDispatcher);

        try
        {
            await hostService.DisablePackageAsync("agent", "Hosted view failed.", PackageFailureOrigin.AppHostedView);

            var notification = Assert.Single(notificationCenter.ListNotifications());
            Assert.Equal("sunder.app", notification.SourcePackageId);
            Assert.Equal("Sunder", notification.SourceDisplayName);
            Assert.Equal("Package disabled", notification.Title);
            Assert.Contains("agent", notification.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Hosted view failed.", notification.Message, StringComparison.Ordinal);
            Assert.Equal(PackageNotificationSeverity.Error, notification.Severity);
            Assert.True(notificationCenter.HasUnreadTrayNotifications());
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task DisablePackageAsync_CancelsAndDrainsPackageViewOperationsBeforeDisposal()
    {
        var probe = new WarmupNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<WarmupNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);
        var warmup = hostService.PreloadViewAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            CancellationToken.None);
        await probe.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disable = hostService.DisablePackageAsync(
            "agent",
            "Hosted view failed.",
            PackageFailureOrigin.AppHostedView);
        var duplicateDisable = hostService.DisablePackageAsync(
            "agent",
            "Duplicate hosted view failure.",
            PackageFailureOrigin.AppHostedView);
        await Task.Delay(50);
        Assert.False(disable.IsCompleted);
        Assert.False(duplicateDisable.IsCompleted);
        Assert.False(probe.DisposalObserved.Task.IsCompleted);

        probe.ReleaseWarmup.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => warmup.WaitAsync(TimeSpan.FromSeconds(2)));
        await disable.WaitAsync(TimeSpan.FromSeconds(2));
        await duplicateDisable.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(probe.DisposalObserved.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisablePackageAsync_WaitsForPackageScopedBackgroundProcessesToStop()
    {
        var backgroundProcesses = new BackgroundProcessQueueService(maxParallelism: 1);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null,
            backgroundProcessQueue: backgroundProcesses,
            uiDispatcher: TestUiDispatcher);
        var packageQueue = new PackageScopedBackgroundProcessQueue(
            "agent",
            "Agent",
            backgroundProcesses,
            hostService.CurrentGenerationId);
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        packageQueue.Enqueue(new BackgroundProcessRequest(
            "Agent background work",
            "work",
            BackgroundProcessIndicator.Settings,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            CanCancel: false,
            async context =>
            {
                processStarted.SetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), context.CancellationToken);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.SetResult();
                    await allowCleanup.Task;
                }
            }));
        try
        {
            await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var disableTask = hostService.DisablePackageAsync("agent", "Activation failed.", PackageFailureOrigin.AppActivation);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(disableTask.IsCompleted);

            allowCleanup.SetResult();
            await disableTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.All(backgroundProcesses.ListProcesses(), process => Assert.Equal(BackgroundProcessState.Cancelled, process.State));
        }
        finally
        {
            allowCleanup.TrySetResult();
            await packageQueue.DisposeAsync();
            await hostService.DisposeAsync();
            await backgroundProcesses.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_LeavesSessionFolderForProcessLifetime()
    {
        var sessionFolder = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);
        try
        {
            var hostService = new PackageViewHostService(
                new AppPackageViewRegistry(),
                [],
                [],
                [],
                faultReporter: null,
                sessionFolder,
                uiDispatcher: TestUiDispatcher);

            await hostService.DisposeAsync();

            Assert.True(Directory.Exists(sessionFolder));
        }
        finally
        {
            TryDeleteDirectory(sessionFolder);
        }
    }

    [Fact]
    public async Task AppPackageSourcePreparer_Prepare_WhenManifestIdMissing_DeletesShadowFolder()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        var packageSourceFolder = Path.Combine(rootPath, "package-source");
        Directory.CreateDirectory(sessionFolder);
        Directory.CreateDirectory(packageSourceFolder);
        File.WriteAllText(Path.Combine(packageSourceFolder, "sunder-package.json"), "{}");

        try
        {
            var preparer = new AppPackageSourcePreparer(sessionFolder);
            var preparedSource = await PrepareSnapshotAsync(preparer, "agent", packageSourceFolder);

            Assert.Null(preparedSource);
            Assert.Empty(Directory.EnumerateDirectories(sessionFolder));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task AppPackageSourcePreparer_MaterializesIntoAppOwnedRootSeparateFromRuntimeRoot()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(rootPath, "runtime-root");
        var appRoot = Path.Combine(rootPath, "app-root");
        var runtimeSource = CreateAppPackageSource(runtimeRoot, "agent");
        var snapshot = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, runtimeSource);
        var preparer = new AppPackageSourcePreparer(appRoot);

        try
        {
            var prepared = await preparer.PrepareAsync(
                snapshot,
                RuntimeContractTestData.DownloadSnapshotAsync,
                CancellationToken.None);

            Assert.NotNull(prepared);
            Assert.StartsWith(Path.GetFullPath(appRoot), Path.GetFullPath(prepared.Folder), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(runtimeRoot), Path.GetFullPath(prepared.Folder), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(runtimeSource, "sunder-package.json")));
            Assert.True(File.Exists(Path.Combine(prepared.Folder, "sunder-package.json")));
        }
        finally
        {
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task DisposeAsync_RejectsPublicOperations()
    {
        var dispatcher = new TrackingUiDispatcher();
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(dispatcher),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: dispatcher);

        await hostService.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => hostService.ApplyPackageDeltaAsync([], []));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => hostService.DisablePackageAsync("agent", "Failed.", PackageFailureOrigin.AppActivation));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await hostService.NotifyViewNavigatedAsync("agent.chat", null));
        Assert.Throws<ObjectDisposedException>(() => hostService.FilterEnabledPackages([CreateActiveAgentPackage()]));
        Assert.Throws<ObjectDisposedException>(() => hostService.TryHandleUnhandledException(new InvalidOperationException("boom")));
        Assert.Throws<ObjectDisposedException>(() => hostService.GetOrCreateView("agent.chat"));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => hostService.ReloadViewAsync("agent.chat").AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => hostService.InvalidateViewAsync("agent.chat").AsTask());
        Assert.Throws<ObjectDisposedException>(() => hostService.HasSettingsView("agent"));
        Assert.Throws<ObjectDisposedException>(() => hostService.ListSettingsViewPackages());
        Assert.Throws<ObjectDisposedException>(() => hostService.GetOrCreateSettingsView("agent"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotifyViewNavigatedAsync_AfterAsynchronousAttachmentBarrier_EntersTargetOnInjectedDispatcher(
        bool useDataContextTarget)
    {
        var dispatcher = new TrackingUiDispatcher();
        var probe = new NavigationDispatcherProbe(dispatcher);
        using var serviceProvider = new ServiceCollection()
            .AddSingleton<IUiDispatcher>(dispatcher)
            .AddSingleton(probe)
            .BuildServiceProvider();
        var registry = new AppPackageViewRegistry(dispatcher);
        if (useDataContextTarget)
        {
            registry.RegisterPackageView<DispatcherDataContextPackageView>("agent", "agent.chat", serviceProvider);
        }
        else
        {
            registry.RegisterPackageView<DispatcherControlPackageView>("agent", "agent.chat", serviceProvider);
        }
        var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: dispatcher);
        var attachmentBarrierReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAttachmentBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            Assert.Null(Application.Current);
            var navigation = Task.Run(async () =>
            {
                attachmentBarrierReached.SetResult();
                await releaseAttachmentBarrier.Task.ConfigureAwait(false);
                probe.WorkerContinuationHadDispatcherAccess = dispatcher.CheckAccess();
                await hostService.NotifyViewNavigatedAsync("agent.chat", parameters: null);
            });
            await attachmentBarrierReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(navigation.IsCompleted);

            releaseAttachmentBarrier.SetResult();
            await navigation.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(probe.WorkerContinuationHadDispatcherAccess);
            Assert.True(probe.ControlCreatedWithDispatcherAccess);
            Assert.True(probe.CallbackEnteredWithDispatcherAccess);
            Assert.Equal(1, probe.NavigationCount);
            Assert.True(dispatcher.InvocationCount > 0);
        }
        finally
        {
            releaseAttachmentBarrier.TrySetResult();
            await hostService.DisposeAsync();
        }
    }

    [Fact]
    public async Task WarmupViewAsync_SerializesPresentationAndNavigationForTheSameView()
    {
        var probe = new WarmupNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<WarmupNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);

        var warmup = hostService.PreloadViewAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            CancellationToken.None);
        await probe.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var presented = false;
        var presentation = hostService.PrepareViewForPresentationAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            _ => presented = true,
            CancellationToken.None);
        var navigation = hostService.NotifyViewNavigatedAsync(
            "agent.chat",
            parameters: null).AsTask();

        await Task.Delay(50);
        Assert.False(presented);
        Assert.False(probe.NavigationStarted.Task.IsCompleted);

        probe.ReleaseWarmup.TrySetResult();
        Assert.NotNull(await warmup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await presentation.WaitAsync(TimeSpan.FromSeconds(2)));
        await navigation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(presented);
        Assert.True(probe.NavigationStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsInFlightViewWarmup()
    {
        var probe = new WarmupNavigationProbe { WaitForCancellation = true };
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<WarmupNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);
        var warmup = hostService.PreloadViewAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            CancellationToken.None);
        await probe.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = hostService.DisposeAsync().AsTask();
        await probe.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disposal.IsCompleted);

        probe.ReleaseCancellationCleanup.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => warmup.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task PresentationBeforePreload_SkipsLateWarmupForPresentedView()
    {
        var probe = new WarmupNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<WarmupNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);

        var presentation = hostService.PrepareViewForPresentationAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            _ => true,
            CancellationToken.None);
        await probe.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(presentation.IsCompleted);

        probe.ReleaseWarmup.TrySetResult();
        Assert.True(await presentation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(await hostService.PreloadViewAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            CancellationToken.None));

        Assert.Equal(1, probe.WarmupCount);
    }

    [Fact]
    public async Task ReloadViewAsync_WaitsForInFlightWarmupBeforeReplacingControl()
    {
        var probe = new WarmupNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<WarmupNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);
        var warmup = hostService.PreloadViewAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            CancellationToken.None);
        await probe.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var reload = hostService.ReloadViewAsync("agent.chat").AsTask();
        var navigation = hostService.NotifyViewNavigatedAsync(
            "agent.chat",
            parameters: null,
            hostService.CurrentGenerationId,
            CancellationToken.None);
        await Task.Delay(50);
        Assert.False(reload.IsCompleted);
        Assert.False(navigation.IsCompleted);

        probe.ReleaseWarmup.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => warmup.WaitAsync(TimeSpan.FromSeconds(2)));
        var replacement = await reload.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(replacement);
        Assert.True(await navigation.AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(probe.NavigationStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task NavigationCallback_CanReenterSameViewPresentationWithoutDeadlock()
    {
        var probe = new ReentrantNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<ReentrantNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);
        probe.OnNavigateAsync = async cancellationToken =>
        {
            await hostService.CancelViewNavigationAsync(
                "agent.chat",
                hostService.CurrentGenerationId);
            Assert.True(await hostService.PrepareViewForPresentationAsync(
                "agent.chat",
                hostService.CurrentGenerationId,
                _ => true,
                cancellationToken));
        };

        await hostService.NotifyViewNavigatedAsync("agent.chat", parameters: null)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, probe.NavigationCount);
    }

    [Fact]
    public async Task EscapedNavigationScope_DoesNotBypassActiveViewGate()
    {
        var probe = new ReentrantNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<ReentrantNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);
        var releaseEscapedOperation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondNavigationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondNavigation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? escapedOperation = null;
        probe.OnNavigateAsync = async cancellationToken =>
        {
            if (probe.NavigationCount == 1)
            {
                escapedOperation = Task.Run(async () =>
                {
                    await releaseEscapedOperation.Task;
                    return await hostService.PrepareViewForPresentationAsync(
                        "agent.chat",
                        hostService.CurrentGenerationId,
                        _ => true,
                        CancellationToken.None);
                });
                return;
            }

            secondNavigationStarted.TrySetResult();
            await releaseSecondNavigation.Task.WaitAsync(cancellationToken);
        };

        await hostService.NotifyViewNavigatedAsync("agent.chat", parameters: null);
        var secondNavigation = hostService.NotifyViewNavigatedAsync(
            "agent.chat",
            parameters: null).AsTask();
        await secondNavigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseEscapedOperation.TrySetResult();
        await Task.Delay(50);
        Assert.NotNull(escapedOperation);
        Assert.False(escapedOperation!.IsCompleted);

        releaseSecondNavigation.TrySetResult();
        await secondNavigation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await escapedOperation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ReentrantNavigationCancellation_CancelsQueuedSuccessorWithoutDeadlock()
    {
        var probe = new ReentrantNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<ReentrantNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);
        Task? successor = null;
        probe.OnNavigateAsync = async cancellationToken =>
        {
            if (probe.NavigationCount == 1)
            {
                successor = hostService.NotifyViewNavigatedAsync(
                    "agent.chat",
                    parameters: null).AsTask();
                await hostService.CancelViewNavigationAsync(
                    "agent.chat",
                    hostService.CurrentGenerationId);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => successor);
                return;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            hostService.NotifyViewNavigatedAsync("agent.chat", parameters: null)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, probe.NavigationCount);
    }

    [Fact]
    public async Task CanceledQueuedReset_PreservesPredecessorBarrierForLaterOperations()
    {
        var probe = new WarmupNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<WarmupNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);
        var warmup = hostService.PreloadViewAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            CancellationToken.None);
        await probe.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var firstReset = hostService.ReloadViewAsync("agent.chat").AsTask();
        using var resetCancellation = new CancellationTokenSource();
        var canceledReset = hostService.ReloadViewAsync(
            "agent.chat",
            resetCancellation.Token).AsTask();
        var presentation = hostService.PrepareViewForPresentationAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            _ => true,
            CancellationToken.None);

        resetCancellation.Cancel();
        await Task.Delay(50);
        Assert.False(canceledReset.IsCompleted);
        Assert.False(presentation.IsCompleted);

        probe.ReleaseWarmup.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => warmup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(await firstReset.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledReset.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await presentation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task GenerationFencedNavigation_RevalidatesRequestBeforeCallbackStarts()
    {
        var probe = new ReentrantNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<ReentrantNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);

        var notified = await hostService.NotifyViewNavigatedAsync(
            "agent.chat",
            parameters: null,
            hostService.CurrentGenerationId,
            CancellationToken.None,
            canStart: () => false);

        Assert.False(notified);
        Assert.Equal(0, probe.NavigationCount);
    }

    [Fact]
    public async Task GenerationFencedNavigation_RevalidatesRequestAfterWaitingForViewGate()
    {
        var probe = new WarmupNavigationProbe();
        using var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<WarmupNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider);
        await using var hostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: TestUiDispatcher);
        var warmup = hostService.PreloadViewAsync(
            "agent.chat",
            hostService.CurrentGenerationId,
            CancellationToken.None);
        await probe.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var canStart = true;
        var navigation = hostService.NotifyViewNavigatedAsync(
            "agent.chat",
            parameters: null,
            hostService.CurrentGenerationId,
            CancellationToken.None,
            () => canStart);

        canStart = false;
        probe.ReleaseWarmup.TrySetResult();

        Assert.NotNull(await warmup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await navigation.AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(probe.NavigationStarted.Task.IsCompleted);
    }

    [Fact]
    public void AppSharedAssemblyRegistry_WhenSameUnsignedIdentityHasDifferentBinaryDefinition_RejectsIt()
    {
        using var registry = new AppSharedAssemblyRegistry([]);
        var registerMethod = typeof(AppSharedAssemblyRegistry).GetMethod("TryRegisterSharedAssemblyPath", BindingFlags.Instance | BindingFlags.NonPublic);
        var candidateType = typeof(AppSharedAssemblyRegistry).GetNestedType("AssemblyCandidate", BindingFlags.NonPublic);
        Assert.NotNull(registerMethod);
        Assert.NotNull(candidateType);
        var assemblyName = new AssemblyName("Example.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        var firstCandidate = Activator.CreateInstance(candidateType, typeof(PackageViewHostServiceTests).Assembly.Location, assemblyName);
        var secondCandidate = Activator.CreateInstance(candidateType, typeof(ISunderRuntimePackageModule).Assembly.Location, assemblyName);

        registerMethod.Invoke(registry, [firstCandidate, null]);
        var error = Assert.Throws<TargetInvocationException>(() => registerMethod.Invoke(registry, [secondCandidate, null]));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void AppSharedAssemblyRegistry_WhenHigherVersionContractExists_SelectsHigherVersion()
    {
        using var registry = new AppSharedAssemblyRegistry([]);
        var registerMethod = typeof(AppSharedAssemblyRegistry).GetMethod("TryRegisterSharedAssemblyPath", BindingFlags.Instance | BindingFlags.NonPublic);
        var candidateType = typeof(AppSharedAssemblyRegistry).GetNestedType("AssemblyCandidate", BindingFlags.NonPublic);
        var namesField = typeof(AppSharedAssemblyRegistry).GetField("_sharedAssemblyNames", BindingFlags.Instance | BindingFlags.NonPublic);
        var pathsField = typeof(AppSharedAssemblyRegistry).GetField("_sharedAssemblyPaths", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(registerMethod);
        Assert.NotNull(candidateType);
        Assert.NotNull(namesField);
        Assert.NotNull(pathsField);

        var lowerCandidate = Activator.CreateInstance(
            candidateType,
            "/tmp/old/Sunder.Package.Agent.Contracts.dll",
            new AssemblyName("Sunder.Package.Agent.Contracts, Version=1.0.2.0, Culture=neutral, PublicKeyToken=null"));
        var higherCandidate = Activator.CreateInstance(
            candidateType,
            "/tmp/new/Sunder.Package.Agent.Contracts.dll",
            new AssemblyName("Sunder.Package.Agent.Contracts, Version=1.0.3.0, Culture=neutral, PublicKeyToken=null"));

        registerMethod.Invoke(registry, [lowerCandidate, null]);
        registerMethod.Invoke(registry, [higherCandidate, null]);
        registerMethod.Invoke(registry, [lowerCandidate, null]);

        var names = Assert.IsType<Dictionary<string, AssemblyName>>(namesField.GetValue(registry));
        var paths = Assert.IsType<Dictionary<string, string>>(pathsField.GetValue(registry));
        Assert.Equal(new Version(1, 0, 3, 0), names["Sunder.Package.Agent.Contracts"].Version);
        Assert.Equal("/tmp/new/Sunder.Package.Agent.Contracts.dll", paths["Sunder.Package.Agent.Contracts"]);
    }

    [Fact]
    public void AppSharedAssemblyRegistry_WhenRequestedVersionIsOlderThanLoadedVersion_AllowsBinding()
    {
        var requested = new AssemblyName("Sunder.Package.Agent.Contracts, Version=1.0.2.0, Culture=neutral, PublicKeyToken=null");
        var loaded = new AssemblyName("Sunder.Package.Agent.Contracts, Version=1.0.3.0, Culture=neutral, PublicKeyToken=null");

        Assert.True(AppSharedAssemblyRegistry.IsSharedAssemblyReferenceSatisfiedBy(requested, loaded));
        Assert.False(AppSharedAssemblyRegistry.IsSharedAssemblyReferenceSatisfiedBy(loaded, requested));
    }

    [Fact]
    public void AppSharedAssemblyRegistry_RejectsHigherMajorAndDifferentContractIdentity()
    {
        var requested = new AssemblyName("Example.Contracts, Version=1.2.0.0, Culture=neutral, PublicKeyToken=0011223344556677");
        var higherMajor = new AssemblyName("Example.Contracts, Version=2.0.0.0, Culture=neutral, PublicKeyToken=0011223344556677");
        var unrelated = new AssemblyName("Example.Contracts, Version=1.3.0.0, Culture=neutral, PublicKeyToken=8899aabbccddeeff");

        Assert.False(AppSharedAssemblyRegistry.IsSharedAssemblyReferenceSatisfiedBy(requested, higherMajor));
        Assert.False(AppSharedAssemblyRegistry.IsSharedAssemblyReferenceSatisfiedBy(requested, unrelated));
    }

    [Fact]
    public void AppPackageAssemblyTracker_ResolvesPackageFromExceptionStackFrame()
    {
        var tracker = new AppPackageAssemblyTracker();
        tracker.RegisterPackageAssembly("agent", typeof(PackageStackFrame).Assembly);

        var exception = Assert.Throws<FormatException>(PackageStackFrame.ThrowFrameworkException);

        Assert.Equal("agent", tracker.ResolvePackageId(exception));
    }

    [Fact]
    public void CleanupStaleSessions_RemovesFoldersWithoutRunningOwner()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var activeFolder = Path.Combine(rootPath, $"20260511190000-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var staleFolder = Path.Combine(rootPath, $"20260511190001-{int.MaxValue}-{Guid.NewGuid():N}");
        var legacyFolder = Path.Combine(rootPath, $"20260511190002-{Guid.NewGuid():N}");
        Directory.CreateDirectory(activeFolder);
        Directory.CreateDirectory(staleFolder);
        Directory.CreateDirectory(legacyFolder);
        try
        {
            AppPackageSessionDirectories.CleanupStaleSessions(rootPath);

            Assert.True(Directory.Exists(activeFolder));
            Assert.False(Directory.Exists(staleFolder));
            Assert.False(Directory.Exists(legacyFolder));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageGenerationAsync_WhenContentIsUnchanged_ReusesVerifiedSnapshotCache()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
        var downloadCount = 0;
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder,
            downloadPackageUiSnapshotAsync: async (snapshot, destination, cancellationToken) =>
            {
                downloadCount++;
                await RuntimeContractTestData.DownloadSnapshotAsync(snapshot, destination, cancellationToken);
            },
            uiDispatcher: TestUiDispatcher);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);
            await hostService.ApplyPackageDeltaAsync([package], [source], ["agent"]);

            Assert.Equal(1, downloadCount);
            Assert.Equal(1, hostService.CachedSnapshotCount);
            Assert.NotNull(hostService.GetOrCreateView("agent.chat"));
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageGenerationAsync_KeepsCandidateRegistrationsInvisibleUntilCommit()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        var gatePath = Path.Combine(rootPath, "activation-gate");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var firstSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [firstSource]);
            var currentView = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(currentView);
            File.WriteAllText(
                Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ActivationGatePathFileName),
                gatePath);
            var replacementSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);

            var replacement = hostService.ApplyPackageDeltaAsync([package], [replacementSource], ["agent"]);
            await WaitForFileAsync(gatePath + ".started");

            Assert.Same(currentView, hostService.GetOrCreateView("agent.chat"));
            Assert.False(GetIsDisposed(currentView));

            File.WriteAllText(gatePath + ".release", string.Empty);
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(currentView, hostService.GetOrCreateView("agent.chat"));
        }
        finally
        {
            File.WriteAllText(gatePath + ".release", string.Empty);
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageGenerationAsync_CommitsReplacementBeforeRetiringDetachedGeneration()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var firstSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
        var resourceAssemblies = new AppPackageResourceAssemblyRegistry();
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            resourceAssemblyRegistry: resourceAssemblies,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [firstSource]);
            var oldView = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(oldView);
            Assert.True(resourceAssemblies.TryGetAssembly(oldView.GetType().Assembly.GetName().Name!, out var oldResourceAssembly));
            Assert.Same(oldView.GetType().Assembly, oldResourceAssembly);
            File.WriteAllText(Path.Combine(packageSourceFolder, "replacement-content"), string.Empty);
            var replacementSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            object? replacementView = null;

            await hostService.ApplyPackageGenerationAsync(
                [package],
                [replacementSource],
                ["agent"],
                activePackages =>
                {
                    Assert.Single(activePackages);
                    Assert.False(GetIsDisposed(oldView));
                    replacementView = hostService.GetOrCreateView("agent.chat");
                    Assert.NotNull(replacementView);
                    Assert.NotSame(oldView, replacementView);
                    Assert.Equal(1, hostService.LoadedPackageCount);
                    Assert.True(resourceAssemblies.TryGetAssembly(replacementView.GetType().Assembly.GetName().Name!, out var replacementResourceAssembly));
                    Assert.Same(replacementView.GetType().Assembly, replacementResourceAssembly);
                },
                CancellationToken.None);
            await hostService.WaitForRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(GetIsDisposed(oldView));
            Assert.Same(replacementView, hostService.GetOrCreateView("agent.chat"));
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageGenerationAsync_WhenRetiredGenerationCleanupFails_KeepsCommittedCandidateLive()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var retirementCount = 0;
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher,
            retireGenerationAsync: generation => ++retirementCount == 2
                ? ValueTask.FromException(new InvalidOperationException("retirement fault"))
                : generation.DisposeAsync());

        try
        {
            var firstSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            await hostService.ApplyPackageDeltaAsync([package], [firstSource]);
            var retiredView = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(retiredView);
            File.WriteAllText(Path.Combine(packageSourceFolder, "replacement-content"), string.Empty);
            var replacementSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);

            await hostService.ApplyPackageDeltaAsync([package], [replacementSource], ["agent"]);
            await hostService.WaitForRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var committedView = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(committedView);
            Assert.NotSame(retiredView, committedView);
            Assert.False(GetIsDisposed(committedView));
            Assert.Equal(1, hostService.LoadedPackageCount);
            Assert.Equal(2, retirementCount);
            Assert.Equal(1, hostService.RetirementFailureCount);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageGenerationAsync_WhenRetirementHangs_ReturnsAfterCommitAndQuarantinesCleanup()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var retirementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quarantinedRetirementCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirementCount = 0;

        async ValueTask RetireAsync(AppPackageGeneration generation)
        {
            if (Interlocked.Increment(ref retirementCount) == 1)
            {
                retirementStarted.SetResult();
                await releaseRetirement.Task;
                await generation.DisposeAsync();
                quarantinedRetirementCompleted.SetResult();
                return;
            }
            await generation.DisposeAsync();
        }

        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher,
            retireGenerationAsync: RetireAsync,
            retirementBudget: TimeSpan.FromMilliseconds(40),
            retirementDrainBudget: TimeSpan.FromMilliseconds(250));

        try
        {
            var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);

            await hostService.ApplyPackageDeltaAsync([package], [source]).WaitAsync(TimeSpan.FromSeconds(2));
            await retirementStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var firstView = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(firstView);
            File.WriteAllText(Path.Combine(packageSourceFolder, "replacement-content"), string.Empty);
            var replacementSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);

            await hostService.ApplyPackageDeltaAsync([package], [replacementSource], ["agent"])
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotSame(firstView, hostService.GetOrCreateView("agent.chat"));
            await hostService.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(hostService.QuarantinedRetirementCount >= 1);
            Assert.True(hostService.RetirementFailureCount >= 1);
        }
        finally
        {
            releaseRetirement.TrySetResult();
            await quarantinedRetirementCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageDeltaAsync_WhenActivationFails_RollsBackRegisteredViewsBeforeReload()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowAfterViewMarkerFileName), string.Empty);
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
        var viewRegistry = new AppPackageViewRegistry();
        var hostService = new PackageViewHostService(
            viewRegistry,
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);

            Assert.Empty(viewRegistry.ListPackageViewIds("agent"));

            File.Delete(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowAfterViewMarkerFileName));
            File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.SkipViewMarkerFileName), string.Empty);
            var replacementSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            await hostService.ApplyPackageDeltaAsync([package], [replacementSource], ["agent"]);

            Assert.Empty(viewRegistry.ListPackageViewIds("agent"));
            Assert.Null(hostService.GetOrCreateView("agent.chat"));
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task DisablePackageAsync_WhenPackageLoaded_UnloadsResourcesAndKeepsPackageDisabled()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);
            var view = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(view);

            await hostService.DisablePackageAsync("agent", "Hosted view failed.", PackageFailureOrigin.AppHostedView);

            Assert.True(Assert.IsType<bool>(view.GetType().GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.IsDisposed))?.GetValue(view)));
            Assert.Null(hostService.GetOrCreateView("agent.chat"));
            Assert.Empty(hostService.FilterEnabledPackages([package]));
            Assert.Equal(0, hostService.LoadedPackageCount);
            Assert.Equal(0, hostService.OwnedDisposableCount);
            Assert.Equal(0, hostService.LoadContextCount);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageDeltaAsync_WhenPackageDisabled_DoesNotReloadUntilForced()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);
            await hostService.DisablePackageAsync("agent", "Hosted view failed.", PackageFailureOrigin.AppHostedView);

            await hostService.ApplyPackageDeltaAsync([package], [source]);

            Assert.Null(hostService.GetOrCreateView("agent.chat"));
            Assert.Empty(hostService.FilterEnabledPackages([package]));

            await hostService.ApplyPackageDeltaAsync([package], [source], ["agent"]);

            Assert.NotNull(hostService.GetOrCreateView("agent.chat"));
            Assert.NotEmpty(hostService.FilterEnabledPackages([package]));
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageGenerationAsync_WhenCandidateActivationFails_PreservesCurrentGeneration()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);
            var liveView = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(liveView);

            File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowAfterViewMarkerFileName), string.Empty);
            var failingSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hostService.ApplyPackageDeltaAsync([package], [failingSource], ["agent"]));

            Assert.Contains("candidate failed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Same(liveView, hostService.GetOrCreateView("agent.chat"));
            Assert.False(GetIsDisposed(liveView));
            Assert.NotEmpty(hostService.FilterEnabledPackages([package]));
            Assert.Equal(1, hostService.LoadedPackageCount);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageGenerationAsync_WhenRejected_DiscardsBufferedPackageSideEffects()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        var notificationCenter = new NotificationCenterService(Path.Combine(rootPath, "notifications.json"));
        var backgroundProcesses = new BackgroundProcessQueueService(maxParallelism: 1);
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            notificationCenter: notificationCenter,
            backgroundProcessQueue: backgroundProcesses,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);
        var sideEffectPath = Path.Combine(rootPath, "candidate-side-effect");

        try
        {
            var firstSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            await hostService.ApplyPackageDeltaAsync([package], [firstSource]);
            File.WriteAllText(
                Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.StageSideEffectsPathFileName),
                sideEffectPath);
            File.WriteAllText(
                Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowAfterViewMarkerFileName),
                string.Empty);
            var rejectedSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hostService.ApplyPackageDeltaAsync([package], [rejectedSource], ["agent"]));

            await Task.Delay(100);
            Assert.Empty(notificationCenter.ListNotifications());
            Assert.Empty(backgroundProcesses.ListProcesses());
            Assert.False(File.Exists(sideEffectPath));
        }
        finally
        {
            await hostService.DisposeAsync();
            await backgroundProcesses.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageGenerationAsync_WhenPackageRegistersReservedCapability_RejectsCandidateClearly()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);

        try
        {
            var firstSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            await hostService.ApplyPackageDeltaAsync([package], [firstSource]);
            File.WriteAllText(
                Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.RegisterReservedHostCapabilityMarkerFileName),
                string.Empty);
            var rejectedSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hostService.ApplyPackageDeltaAsync([package], [rejectedSource], ["agent"]));

            Assert.Contains("reserved host capability", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(typeof(IPackageContext).FullName!, error.ToString(), StringComparison.Ordinal);
            Assert.NotNull(hostService.GetOrCreateView("agent.chat"));
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageGenerationAsync_WhenCandidateIsCancelled_PreservesCurrentGeneration()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var blockedHash = string.Empty;
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            downloadPackageUiSnapshotAsync: async (snapshot, destination, cancellationToken) =>
            {
                if (string.Equals(snapshot.ContentHash, blockedHash, StringComparison.OrdinalIgnoreCase))
                {
                    downloadStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                await RuntimeContractTestData.DownloadSnapshotAsync(snapshot, destination, cancellationToken);
            },
            uiDispatcher: TestUiDispatcher);

        try
        {
            var firstSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            await hostService.ApplyPackageDeltaAsync([package], [firstSource]);
            var liveView = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(liveView);
            File.WriteAllText(Path.Combine(packageSourceFolder, "cancelled-content"), string.Empty);
            var cancelledSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            blockedHash = cancelledSource.ContentHash;
            using var cancellation = new CancellationTokenSource();

            var apply = hostService.ApplyPackageDeltaAsync([package], [cancelledSource], ["agent"], cancellation.Token);
            await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
            Assert.Same(liveView, hostService.GetOrCreateView("agent.chat"));
            Assert.False(GetIsDisposed(liveView));
            Assert.Equal(1, hostService.LoadedPackageCount);
            Assert.Equal(1, hostService.CachedSnapshotCount);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageSnapshotAsync_PublishesReloadIconBeforePresentationAndReleasesRetiredImage()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var images = new Queue<TrackedImage>([new TrackedImage(), new TrackedImage()]);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            loadPackageIconImageAsync: (_, _) =>
                Task.FromResult(PackageIconImageLoadResult.Success(images.Dequeue())),
            uiDispatcher: TestUiDispatcher);
        var coordinator = new AppPackageLifecycleCoordinator(hostService);

        try
        {
            var firstSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            await hostService.ApplyPackageDeltaAsync([package], [firstSource]);
            var firstSnapshot = CreateRuntimeSnapshot(package, firstSource, generation: 1);
            var firstActivePackages = hostService.FilterEnabledPackages([package]);
            await hostService.PrewarmPackageIconsAsync(firstSnapshot, firstActivePackages, CancellationToken.None);
            var icon = Assert.Single(firstActivePackages).Views.Single(view => view.ViewId == "agent.chat").Icon;
            var firstImage = Assert.IsType<TrackedImage>(hostService.PackageIconCache.GetImage("agent", icon));
            File.WriteAllText(Path.Combine(packageSourceFolder, "replacement-content"), string.Empty);
            var replacementSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            var replacementSnapshot = CreateRuntimeSnapshot(package, replacementSource, generation: 2);
            TrackedImage? replacementImage = null;

            await coordinator.ApplyPackageSnapshotAsync(
                replacementSnapshot,
                ["agent"],
                activePackages =>
                {
                    var replacementIcon = Assert.Single(activePackages).Views.Single(view => view.ViewId == "agent.chat").Icon;
                    replacementImage = Assert.IsType<TrackedImage>(
                        hostService.PackageIconCache.GetImage("agent", replacementIcon));
                    Assert.NotSame(firstImage, replacementImage);
                    Assert.False(firstImage.IsDisposed);
                });
            await hostService.WaitForRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(replacementImage);
            Assert.True(firstImage.IsDisposed);
            Assert.False(replacementImage.IsDisposed);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageSnapshotAsync_WhenCandidateFails_PreservesPublishedIconGeneration()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var loadCount = 0;
        var firstImage = new TrackedImage();
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            loadPackageIconImageAsync: (_, _) =>
            {
                Interlocked.Increment(ref loadCount);
                return Task.FromResult(PackageIconImageLoadResult.Success(firstImage));
            },
            uiDispatcher: TestUiDispatcher);
        var coordinator = new AppPackageLifecycleCoordinator(hostService);

        try
        {
            var firstSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            await hostService.ApplyPackageDeltaAsync([package], [firstSource]);
            var firstSnapshot = CreateRuntimeSnapshot(package, firstSource, generation: 1);
            var firstActivePackages = hostService.FilterEnabledPackages([package]);
            await hostService.PrewarmPackageIconsAsync(firstSnapshot, firstActivePackages, CancellationToken.None);
            var icon = Assert.Single(firstActivePackages).Views.Single(view => view.ViewId == "agent.chat").Icon;

            File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowAfterViewMarkerFileName), string.Empty);
            var failingSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
            var failingSnapshot = CreateRuntimeSnapshot(package, failingSource, generation: 2);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.ApplyPackageSnapshotAsync(failingSnapshot, ["agent"]));

            Assert.Same(firstImage, hostService.PackageIconCache.GetImage("agent", icon));
            Assert.False(firstImage.IsDisposed);
            Assert.Equal(1, loadCount);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task DisposeAsync_UnloadsCurrentGenerationLoadContext()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(), [], [], [], null, sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);

        await hostService.ApplyPackageDeltaAsync([package], [source]);
        var loadContext = Assert.Single(hostService.SnapshotLoadContextWeakReferences());

        await hostService.DisposeAsync();
        await WaitForCollectionAsync(loadContext);

        Assert.False(loadContext.IsAlive);
        TryDeleteDirectoryBestEffort(rootPath);
    }

    [Fact]
    public async Task ApplyPackageDeltaAsync_ProvidesDevelopmentSessionControlToPackageModules()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ResolveDevelopmentSessionControlMarkerFileName), string.Empty);
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: TestUiDispatcher);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);

            Assert.NotEmpty(Directory.EnumerateFiles(sessionFolder, ShellLifecycleTestPackageModule.DevelopmentSessionControlResolvedFileName, SearchOption.AllDirectories));
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    private static async Task<AppPreparedPackageSource?> PrepareSnapshotAsync(
        AppPackageSourcePreparer preparer,
        string packageId,
        string sourceFolder)
    {
        await using var archiveStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(archiveStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(sourceFolder, file).Replace('\\', '/'));
                await using var input = File.OpenRead(file);
                await using var output = entry.Open();
                await input.CopyToAsync(output);
            }
        }

        var bytes = archiveStream.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var snapshot = new PackageUiSnapshotDescriptor(packageId, PackageSourceKind.Dev, 1, hash, "snapshot", "packages/ui-snapshots/snapshot");
        return await preparer.PrepareAsync(
            snapshot,
            async (_, destination, cancellationToken) => await destination.WriteAsync(bytes, cancellationToken),
            CancellationToken.None);
    }

    private static bool GetIsDisposed(object view)
        => Assert.IsType<bool>(view.GetType().GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.IsDisposed))?.GetValue(view));

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 200 && !File.Exists(path); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(File.Exists(path), $"Timed out waiting for '{path}'.");
    }

    private static async Task WaitForCollectionAsync(WeakReference reference)
    {
        for (var attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(25);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteDirectoryBestEffort(string path)
    {
        try
        {
            TryDeleteDirectory(path);
        }
        catch
        {
            // Loaded package shadows can remain locked until process exit.
        }
    }

    private static ActivePackageDescriptor CreateActiveAgentPackage()
        => new(
            "agent",
            "Agent",
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            null,
            true,
            PackageReadinessState.Ready,
            [new PackageViewDescriptor("agent.chat", "agent", "Chat", null, "middle")]);

    private static ActivePackageDescriptor CreateActivePackage(string packageId)
        => new(
            packageId,
            packageId,
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            null,
            true,
            PackageReadinessState.Ready,
            [new PackageViewDescriptor($"{packageId}.view", packageId, packageId, null, "middle")]);

    private static RuntimePackageSnapshot CreateRuntimeSnapshot(
        ActivePackageDescriptor package,
        PackageUiSnapshotDescriptor source,
        long generation)
        => new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            generation,
            generation,
            RuntimeBootstrapState.Ready,
            [package],
            [],
            [source],
            [],
            []);

    private static string CreateAppPackageSource(string rootPath, string packageId)
    {
        var packageSourceFolder = Path.Combine(rootPath, "package-source");
        var libraryFolder = Path.Combine(packageSourceFolder, "lib");
        Directory.CreateDirectory(libraryFolder);

        var assemblyPath = typeof(ShellLifecycleTestPackageModule).Assembly.Location;
        var entryAssemblyFileName = Path.GetFileName(assemblyPath);
        File.WriteAllText(Path.Combine(packageSourceFolder, "sunder-package.json"), $$"""
            {
              "id": "{{packageId}}",
              "entryAssembly": "{{entryAssemblyFileName}}"
            }
            """);
        File.WriteAllBytes(Path.Combine(packageSourceFolder, "icon.png"), [1, 2, 3]);

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            File.Copy(file, Path.Combine(libraryFolder, Path.GetFileName(file)), overwrite: true);
        }

        var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (File.Exists(depsPath))
        {
            File.Copy(depsPath, Path.Combine(libraryFolder, Path.GetFileName(depsPath)), overwrite: true);
        }

        return packageSourceFolder;
    }

    private static class PackageStackFrame
    {
        public static void ThrowFrameworkException()
            => int.Parse("not an integer");
    }

    private sealed class TrackedImage : IImage, IDisposable
    {
        public Size Size => new(1, 1);

        public bool IsDisposed { get; private set; }

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class DispatcherControlPackageView(
        NavigationDispatcherProbe probe,
        IUiDispatcher dispatcher) : Control, IPackageViewNavigationTarget
    {
        private readonly NavigationDispatcherProbe _probe = RecordCreation(probe, dispatcher);

        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
            => _probe.OnNavigatedToAsync(context, cancellationToken);
    }

    private sealed class WarmupNavigationPackageView(WarmupNavigationProbe probe)
        : Control,
            IPackageViewWarmupTarget,
            IPackageViewNavigationTarget,
            IDisposable
    {
        public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
        {
            probe.WarmupCount++;
            probe.WarmupStarted.TrySetResult();
            if (!probe.WaitForCancellation)
            {
                await probe.ReleaseWarmup.Task;
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                probe.CancellationObserved.TrySetResult();
                await probe.ReleaseCancellationCleanup.Task;
                throw;
            }
        }

        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.NavigationStarted.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void Dispose() => probe.DisposalObserved.TrySetResult();
    }

    private sealed class ReentrantNavigationPackageView(ReentrantNavigationProbe probe)
        : Control,
            IPackageViewWarmupTarget,
            IPackageViewNavigationTarget
    {
        public ValueTask WarmupAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public async ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
        {
            probe.NavigationCount++;
            if (probe.OnNavigateAsync is not null)
            {
                await probe.OnNavigateAsync(cancellationToken);
            }
        }
    }

    private sealed class ReentrantNavigationProbe
    {
        public int NavigationCount { get; set; }

        public Func<CancellationToken, Task>? OnNavigateAsync { get; set; }
    }

    private sealed class WarmupNavigationProbe
    {
        public int WarmupCount { get; set; }

        public bool WaitForCancellation { get; init; }

        public TaskCompletionSource WarmupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseWarmup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource NavigationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCancellationCleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposalObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DispatcherDataContextPackageView : Control
    {
        public DispatcherDataContextPackageView(
            NavigationDispatcherProbe probe,
            IUiDispatcher dispatcher)
        {
            RecordCreation(probe, dispatcher);
            DataContext = new DispatcherDataContextTarget(probe);
        }
    }

    private sealed class DispatcherDataContextTarget(NavigationDispatcherProbe probe) : IPackageViewNavigationTarget
    {
        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
            => probe.OnNavigatedToAsync(context, cancellationToken);
    }

    private sealed class NavigationDispatcherProbe(IUiDispatcher dispatcher)
    {
        public bool WorkerContinuationHadDispatcherAccess { get; set; }

        public bool ControlCreatedWithDispatcherAccess { get; set; }

        public bool CallbackEnteredWithDispatcherAccess { get; private set; }

        public int NavigationCount { get; private set; }

        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallbackEnteredWithDispatcherAccess = dispatcher.CheckAccess();
            NavigationCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingUiDispatcher : IUiDispatcher
    {
        private readonly AsyncLocal<int> _accessDepth = new();

        public int InvocationCount { get; private set; }

        public bool CheckAccess() => _accessDepth.Value > 0;

        public Task InvokeAsync(Action action)
            => InvokeAsync(() =>
            {
                action();
                return Task.CompletedTask;
            });

        public async Task InvokeAsync(Func<Task> action)
        {
            await Task.Yield();
            InvocationCount++;
            _accessDepth.Value++;
            try
            {
                await action();
            }
            finally
            {
                _accessDepth.Value--;
            }
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
            => InvokeAsync(() => Task.FromResult(action()));

        public async Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            await Task.Yield();
            InvocationCount++;
            _accessDepth.Value++;
            try
            {
                return await action();
            }
            finally
            {
                _accessDepth.Value--;
            }
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action) => action();

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());

        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    private static NavigationDispatcherProbe RecordCreation(
        NavigationDispatcherProbe probe,
        IUiDispatcher dispatcher)
    {
        probe.ControlCreatedWithDispatcherAccess = dispatcher.CheckAccess();
        return probe;
    }
}
