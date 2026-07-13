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
    public void AppSharedAssemblyRegistry_ResolvesHostStackSdkAssembly()
    {
        using var registry = new AppSharedAssemblyRegistry([]);
        var stackSdkAssembly = typeof(IPackageStackContributor).Assembly;

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
            notificationCenter: notificationCenter);

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
    public async Task DisablePackageAsync_WaitsForPackageScopedBackgroundProcessesToStop()
    {
        var backgroundProcesses = new BackgroundProcessQueueService(maxParallelism: 1);
        var packageQueue = new PackageScopedBackgroundProcessQueue("agent", "Agent", backgroundProcesses);
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
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null,
            backgroundProcessQueue: backgroundProcesses);

        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disableTask = hostService.DisablePackageAsync("agent", "Activation failed.", PackageFailureOrigin.AppActivation);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(disableTask.IsCompleted);

        allowCleanup.SetResult();
        await disableTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(backgroundProcesses.ListProcesses(), process => Assert.Equal(BackgroundProcessState.Cancelled, process.State));
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
                sessionFolder);

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
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null);

        await hostService.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => hostService.ApplyPackageDeltaAsync([], []));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => hostService.DisablePackageAsync("agent", "Failed.", PackageFailureOrigin.AppActivation));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await hostService.NotifyViewNavigatedAsync("agent.chat", null));
        Assert.Throws<ObjectDisposedException>(() => hostService.FilterEnabledPackages([CreateActiveAgentPackage()]));
        Assert.Throws<ObjectDisposedException>(() => hostService.TryHandleUnhandledException(new InvalidOperationException("boom")));
        Assert.Throws<ObjectDisposedException>(() => hostService.GetOrCreateView("agent.chat"));
        Assert.Throws<ObjectDisposedException>(() => hostService.ReloadView("agent.chat"));
        Assert.Throws<ObjectDisposedException>(() => hostService.InvalidateView("agent.chat"));
        Assert.Throws<ObjectDisposedException>(() => hostService.HasSettingsView("agent"));
        Assert.Throws<ObjectDisposedException>(() => hostService.ListSettingsViewPackages());
        Assert.Throws<ObjectDisposedException>(() => hostService.GetOrCreateSettingsView("agent"));
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
    public async Task ApplyPackageDeltaAsync_WhenPackageReloads_DeletesOldSnapshotAfterDetach()
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
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);
            await hostService.ApplyPackageDeltaAsync([package], [source], ["agent"]);

            var shadowFolders = Directory.EnumerateDirectories(sessionFolder)
                .Select(Path.GetFileName)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var shadowFolder = Assert.Single(shadowFolders);
            Assert.StartsWith("0002-agent", shadowFolder);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task AppPackageDeltaCoordinator_WhenMultiplePackagesReload_UnloadsAllBeforeLoadingReplacements()
    {
        var packageA = CreateActivePackage("package.a");
        var packageB = CreateActivePackage("package.b");
        var sourceA = RuntimeContractTestData.Snapshot("package.a", PackageSourceKind.Dev, "package-a");
        var sourceB = RuntimeContractTestData.Snapshot("package.b", PackageSourceKind.Dev, "package-b");
        var loadedPackages = new Dictionary<string, AppLoadedPackageHandle>(StringComparer.OrdinalIgnoreCase)
        {
            ["package.a"] = new(packageA, sourceA, string.Empty, null!, null!),
            ["package.b"] = new(packageB, sourceB, string.Empty, null!, null!),
        };
        var operations = new List<string>();
        var coordinator = new AppPackageDeltaCoordinator(
            _ => [],
            packageId => loadedPackages.TryGetValue(packageId, out var handle) ? handle : null,
            _ => false,
            (packageId, _, _) =>
            {
                operations.Add($"unload:{packageId}");
                loadedPackages.Remove(packageId);
                return Task.FromResult(true);
            },
            (package, source, _) =>
            {
                operations.Add($"load:{package.PackageId}");
                loadedPackages[package.PackageId] = new AppLoadedPackageHandle(package, source, string.Empty, null!, null!);
                return Task.CompletedTask;
            },
            (_, _, _, _, _) => Task.CompletedTask);

        await coordinator.ApplyPackageDeltaAsync(
            [packageA, packageB],
            [sourceA, sourceB],
            ["package.a", "package.b"],
            CancellationToken.None);

        Assert.Equal([
            "unload:package.a",
            "unload:package.b",
            "load:package.a",
            "load:package.b",
        ], operations);
    }

    [Fact]
    public async Task AppPackageDeltaCoordinator_WhenSharedAssemblyResetRequired_UnloadsAllThenResetsAndReloadsAllPackages()
    {
        var packageA = CreateActivePackage("package.a");
        var packageB = CreateActivePackage("package.b");
        var sourceA = RuntimeContractTestData.Snapshot("package.a", PackageSourceKind.Dev, "package-a");
        var sourceB = RuntimeContractTestData.Snapshot("package.b", PackageSourceKind.Dev, "package-b");
        var loadedPackages = new Dictionary<string, AppLoadedPackageHandle>(StringComparer.OrdinalIgnoreCase)
        {
            ["package.a"] = new(packageA, sourceA, string.Empty, null!, null!),
            ["package.b"] = new(packageB, sourceB, string.Empty, null!, null!),
        };
        var operations = new List<string>();
        var coordinator = new AppPackageDeltaCoordinator(
            _ => loadedPackages.Keys.ToArray(),
            packageId => loadedPackages.TryGetValue(packageId, out var handle) ? handle : null,
            _ => false,
            (packageId, _, _) =>
            {
                operations.Add($"unload:{packageId}");
                loadedPackages.Remove(packageId);
                return Task.FromResult(true);
            },
            (package, source, _) =>
            {
                operations.Add($"load:{package.PackageId}");
                loadedPackages[package.PackageId] = new AppLoadedPackageHandle(package, source, string.Empty, null!, null!);
                return Task.CompletedTask;
            },
            (_, _, _, _, _) => Task.CompletedTask,
            _ => true,
            () => operations.Add("reset-shared-assemblies"));

        await coordinator.ApplyPackageDeltaAsync(
            [packageA, packageB],
            [sourceA, sourceB],
            forceReloadPackageIds: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal([
            "unload:package.a",
            "unload:package.b",
            "reset-shared-assemblies",
            "load:package.a",
            "load:package.b",
        ], operations);
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
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);

            Assert.Empty(viewRegistry.ListPackageViewIds("agent"));

            File.Delete(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowAfterViewMarkerFileName));
            File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.SkipViewMarkerFileName), string.Empty);
            await hostService.ApplyPackageDeltaAsync([package], [source], ["agent"]);

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
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);

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
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);

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
    public async Task PreflightPackageDeltaAsync_WhenActivationFails_DoesNotMutateLivePackageState()
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
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);
            var liveView = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(liveView);

            File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowAfterViewMarkerFileName), string.Empty);
            var failingSource = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder);

            var preflight = await hostService.PreflightPackageDeltaAsync([package], [failingSource], ["agent"]);

            Assert.False(preflight.Success);
            Assert.Contains(preflight.Errors, error => error.Contains("preflight failed", StringComparison.OrdinalIgnoreCase));
            Assert.Same(liveView, hostService.GetOrCreateView("agent.chat"));
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
    public async Task PreflightPackageDeltaAsync_WhenSharedAssemblyResetRequired_PreflightsAllActivePackages()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var packageASourceFolder = CreateAppPackageSource(rootPath, "package.a");
        var packageA = CreateActivePackage("package.a");
        var packageB = CreateActivePackage("package.b");
        var sourceA = RuntimeContractTestData.Snapshot("package.a", PackageSourceKind.Dev, packageASourceFolder);
        var coordinator = new AppPackagePreflightCoordinator(
            _ => null,
            _ => false,
            _ => true,
            RuntimeContractTestData.DownloadSnapshotAsync);

        try
        {
            var preflight = await coordinator.PreflightPackageDeltaAsync(
                [packageA, packageB],
                [sourceA],
                ["package.a"],
                CancellationToken.None);

            Assert.False(preflight.Success);
            Assert.NotEmpty(preflight.Errors);
        }
        finally
        {
            TryDeleteDirectoryBestEffort(rootPath);
        }
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
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);

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
            null,
            true,
            PackageReadinessState.Ready,
            [new PackageViewDescriptor("agent.chat", "agent", "Chat", null, "middle")]);

    private static ActivePackageDescriptor CreateActivePackage(string packageId)
        => new(
            packageId,
            packageId,
            "1.0.0",
            null,
            true,
            PackageReadinessState.Ready,
            [new PackageViewDescriptor($"{packageId}.view", packageId, packageId, null, "middle")]);

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
}
