using System.Reflection;
using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageViewHostServiceTests
{
    [Fact]
    public async Task DisablePackageAsync_WaitsForBackgroundServicesToStop()
    {
        var backgroundServices = new AppPackageBackgroundServiceCoordinator();
        var backgroundService = new BlockingBackgroundService();
        backgroundServices.Register("test.package", backgroundService);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            backgroundServices,
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null);

        var disableTask = hostService.DisablePackageAsync(
            "test.package",
            "Activation failed.",
            PackageFailureOrigin.AppActivation);

        await backgroundService.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disableTask.IsCompleted);

        backgroundService.AllowStop.SetResult();
        await disableTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, backgroundService.StopCount);
    }

    [Fact]
    public async Task DisablePackageAsync_StopsPackageOnlyOnce()
    {
        var backgroundServices = new AppPackageBackgroundServiceCoordinator();
        var backgroundService = new BlockingBackgroundService();
        backgroundServices.Register("test.package", backgroundService);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            backgroundServices,
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null);

        backgroundService.AllowStop.SetResult();
        await hostService.DisablePackageAsync("test.package", "First failure.", PackageFailureOrigin.AppActivation);
        await hostService.DisablePackageAsync("test.package", "Second failure.", PackageFailureOrigin.AppActivation);

        Assert.Equal(1, backgroundService.StopCount);
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
            new AppPackageBackgroundServiceCoordinator(),
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
                new AppPackageBackgroundServiceCoordinator(),
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
    public void AppPackageSourcePreparer_Prepare_WhenManifestIdMissing_DeletesShadowFolder()
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
            var preparedSource = preparer.Prepare(new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder));

            Assert.Null(preparedSource);
            Assert.Empty(Directory.EnumerateDirectories(sessionFolder));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task DisposeAsync_RejectsPublicOperations()
    {
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
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
    public void AppSharedAssemblyRegistry_WhenSameIdentityContractExistsAtDifferentPaths_DoesNotThrow()
    {
        using var registry = new AppSharedAssemblyRegistry([]);
        var registerMethod = typeof(AppSharedAssemblyRegistry).GetMethod("TryRegisterSharedAssemblyPath", BindingFlags.Instance | BindingFlags.NonPublic);
        var candidateType = typeof(AppSharedAssemblyRegistry).GetNestedType("AssemblyCandidate", BindingFlags.NonPublic);
        Assert.NotNull(registerMethod);
        Assert.NotNull(candidateType);
        var assemblyName = new AssemblyName("Example.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        var firstCandidate = Activator.CreateInstance(candidateType, typeof(PackageViewHostServiceTests).Assembly.Location, assemblyName);
        var secondCandidate = Activator.CreateInstance(candidateType, typeof(ISunderPackageModule).Assembly.Location, assemblyName);

        registerMethod.Invoke(registry, [firstCandidate, null]);
        registerMethod.Invoke(registry, [secondCandidate, null]);
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
    public async Task ApplyPackageDeltaAsync_WhenPackageReloads_UsesNewShadowFolder()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);
            await hostService.ApplyPackageDeltaAsync([package], [source], ["agent"]);

            var shadowFolders = Directory.EnumerateDirectories(sessionFolder)
                .Select(Path.GetFileName)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(2, shadowFolders.Length);
            Assert.StartsWith("0001-agent", shadowFolders[0]);
            Assert.StartsWith("0002-agent", shadowFolders[1]);
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
        var sourceA = new PackageSourceDescriptor("package.a", PackageSourceKind.Dev, "/tmp/package-a");
        var sourceB = new PackageSourceDescriptor("package.b", PackageSourceKind.Dev, "/tmp/package-b");
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
    public async Task ApplyPackageDeltaAsync_WhenActivationFails_RollsBackRegisteredViewsBeforeReload()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowAfterViewMarkerFileName), string.Empty);
        var package = CreateActiveAgentPackage();
        var source = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var viewRegistry = new AppPackageViewRegistry();
        var hostService = new PackageViewHostService(
            viewRegistry,
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder);

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
    public async Task ApplyPackageDeltaAsync_WhenBackgroundServiceStartFails_StopsRegisteredBackgroundService()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.RegisterBackgroundServiceMarkerFileName), string.Empty);
        File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowOnBackgroundStartMarkerFileName), string.Empty);
        var package = CreateActiveAgentPackage();
        var source = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);

            Assert.NotEmpty(Directory.EnumerateFiles(sessionFolder, ShellLifecycleTestPackageModule.BackgroundServiceStartedFileName, SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.EnumerateFiles(sessionFolder, ShellLifecycleTestPackageModule.BackgroundServiceStoppedFileName, SearchOption.AllDirectories));
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
    public async Task DisablePackageAsync_WhenPackageLoaded_UnloadsResourcesAndKeepsPackageDisabled()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder);

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
        var source = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder);

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
        var source = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);
            var liveView = hostService.GetOrCreateView("agent.chat");
            Assert.NotNull(liveView);

            File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowAfterViewMarkerFileName), string.Empty);

            var preflight = await hostService.PreflightPackageDeltaAsync([package], [source], ["agent"]);

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
    public async Task PreflightPackageDeltaAsync_DoesNotStartBackgroundServices()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.RegisterBackgroundServiceMarkerFileName), string.Empty);
        File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.ThrowOnBackgroundStartMarkerFileName), string.Empty);
        var package = CreateActiveAgentPackage();
        var source = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder);

        try
        {
            var preflight = await hostService.PreflightPackageDeltaAsync([package], [source], ["agent"]);

            Assert.True(preflight.Success, string.Join(Environment.NewLine, preflight.Errors));
            Assert.Empty(Directory.EnumerateFiles(packageSourceFolder, ShellLifecycleTestPackageModule.BackgroundServiceStartedFileName, SearchOption.AllDirectories));
            Assert.Equal(0, hostService.LoadedPackageCount);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageDeltaAsync_ProvidesPackageSessionServiceToPackageModules()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        File.WriteAllText(Path.Combine(packageSourceFolder, ShellLifecycleTestPackageModule.RequirePackageSessionServiceMarkerFileName), string.Empty);
        var package = CreateActiveAgentPackage();
        var source = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);

            Assert.NotEmpty(Directory.EnumerateFiles(sessionFolder, ShellLifecycleTestPackageModule.PackageSessionServiceResolvedFileName, SearchOption.AllDirectories));
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
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

    private sealed class BlockingBackgroundService : IPackageBackgroundService
    {
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            StopStarted.TrySetResult();
            await AllowStop.Task.WaitAsync(cancellationToken);
        }
    }
}
