using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageExtensionCatalogTests
{
    private static readonly PackageExtensionPoint<object> TestPoint = new("test:ownership");

    [Theory]
    [InlineData("")]
    [InlineData("Package.Owner")]
    public void Add_RejectsMissingOrNoncanonicalOwnershipBeforeMutation(string packageId)
    {
        var catalog = new AppPackageExtensionCatalog();

        Assert.Throws<ArgumentException>(() => catalog.Add(packageId, TestPoint, new object()));

        Assert.Empty(catalog.GetExtensions(TestPoint));
    }

    [Fact]
    public void SameExtensionPointId_WithDifferentContractTypes_KeepsBucketsIsolated()
    {
        var catalog = new AppPackageExtensionCatalog();
        var textPoint = new PackageExtensionPoint<string>("test:shared");
        var numberPoint = new PackageExtensionPoint<int>("test:shared");

        catalog.Add("test.package", textPoint, "value");
        catalog.Add("test.package", numberPoint, 42);

        Assert.Equal(["value"], catalog.GetExtensions(textPoint));
        Assert.Equal([42], catalog.GetExtensions(numberPoint));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" test:point")]
    [InlineData("test:\npoint")]
    public void Add_RejectsInvalidExtensionPointId(string extensionPointId)
    {
        var catalog = new AppPackageExtensionCatalog();
        var extensionPoint = new PackageExtensionPoint<object>(extensionPointId);

        Assert.Throws<ArgumentException>(() => catalog.Add("test.package", extensionPoint, new object()));
    }

    [Fact]
    public async Task FaultReporter_AcceptsOnlyExactCurrentOwnerReference()
    {
        var reports = new List<(string PackageId, PackageFailureOrigin Origin, Exception? Exception)>();
        var catalog = new AppPackageExtensionCatalog();
        Func<bool>? reportedOwnerIsActive = null;
        catalog.ConfigureFaultReporting((ownerToken, _, origin, exception) =>
        {
            reports.Add((ownerToken.PackageId, origin, exception));
            reportedOwnerIsActive = () => catalog.IsActiveOwner(ownerToken);
        });
        var firstOwner = catalog.BeginOwnerActivation("first.package");
        var secondOwner = catalog.BeginOwnerActivation("second.package");
        catalog.Add(firstOwner, TestPoint, new object());
        catalog.Add(secondOwner, TestPoint, new object());
        var references = catalog.GetExtensionReferences(TestPoint);
        var exception = new InvalidOperationException("contributor invariant");

        Assert.True(catalog.TryReportOwnerInvariantViolation(references[1], exception));
        var report = Assert.Single(reports);
        Assert.Equal("second.package", report.PackageId);
        Assert.Equal(PackageFailureOrigin.AppUnhandledUi, report.Origin);
        Assert.Same(exception, report.Exception);
        Assert.True(reportedOwnerIsActive!());

        await catalog.BeginOwnerRetirement(secondOwner).Completion;
        var replacementOwner = catalog.BeginOwnerActivation("second.package");
        catalog.Add(replacementOwner, TestPoint, new object());

        Assert.False(catalog.TryReportOwnerInvariantViolation(references[1], new InvalidOperationException("stale")));
        Assert.False(reportedOwnerIsActive!());
        Assert.Single(reports);

        await catalog.BeginOwnerRetirement(firstOwner).Completion;
        await catalog.BeginOwnerRetirement(replacementOwner).Completion;
    }

    [Fact]
    public async Task InvocationCatalog_ReservesInvariantReportingForAgentOrchestrator()
    {
        var reports = new List<string>();
        var catalog = new AppPackageExtensionCatalog();
        catalog.ConfigureFaultReporting((ownerToken, _, _, _) => reports.Add(ownerToken.PackageId));
        var owner = catalog.BeginOwnerActivation("contributor.package");
        catalog.Add(owner, TestPoint, new object());
        var reference = Assert.Single(catalog.GetExtensionReferences(TestPoint));
        var unauthorized = catalog.CreateInvocationCatalog("untrusted.package");
        var authorized = catalog.CreateInvocationCatalog("sunder.package.agent");

        Assert.False(unauthorized.TryReportInvariantViolation(
            reference,
            new InvalidOperationException("must not disable another package")));
        Assert.Empty(reports);

        Assert.True(authorized.TryReportInvariantViolation(
            reference,
            new InvalidOperationException("contributor invariant")));
        Assert.Equal(["contributor.package"], reports);

        await catalog.BeginOwnerRetirement(owner).Completion;
    }

    [Fact]
    public async Task FaultReporter_DisablesExactOwnerAndLeavesHealthyLoadedPackageCataloged()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var firstFolder = Path.Combine(root, "first");
        var secondFolder = Path.Combine(root, "second");
        var firstLibraryFolder = Path.Combine(firstFolder, "lib");
        var secondLibraryFolder = Path.Combine(secondFolder, "lib");
        Directory.CreateDirectory(firstLibraryFolder);
        Directory.CreateDirectory(secondLibraryFolder);
        var firstProvider = new ServiceCollection().BuildServiceProvider();
        var secondProvider = new ServiceCollection().BuildServiceProvider();
        var sharedAssemblies = new AppSharedAssemblyRegistry([firstLibraryFolder, secondLibraryFolder]);
        var firstLoadContext = new AppPackageLoadContext(
            "first.package",
            typeof(AppPackageExtensionCatalog).Assembly.Location,
            sharedAssemblies,
            static (_, _) => { });
        var secondLoadContext = new AppPackageLoadContext(
            "second.package",
            typeof(AppPackageExtensionCatalog).Assembly.Location,
            sharedAssemblies,
            static (_, _) => { });
        var state = new AppPackageHostState(
            [],
            [firstProvider, secondProvider],
            [firstLoadContext, secondLoadContext]);
        await using var backgroundProcesses = new BackgroundProcessQueueService();
        AppPackageHostComposition? composition = null;
        Task? disableTask = null;
        PackageFailureOrigin? reportedOrigin = null;
        composition = new AppPackageHostComposition(
            new object(),
            Guid.NewGuid(),
            new AppPackageViewRegistry(new ImmediateUiDispatcher()),
            state,
            static (_, _, _, _, _) => { },
            (_, ownerToken, message, origin, exception) =>
            {
                reportedOrigin = origin;
                disableTask = composition!.DisablePackageAsync(ownerToken.PackageId, message, origin, exception);
            },
            sharedAssemblies,
            extensionCatalog: null,
            shellViewService: null,
            settingsNavigationService: null,
            notificationCenter: null,
            backgroundProcesses,
            getRuntimeConnectionInfo: null);
        var firstPackage = CreatePackage("first.package", "First Package");
        var secondPackage = CreatePackage("second.package", "Second Package");
        var firstOwner = composition.ExtensionCatalog.BeginOwnerActivation(firstPackage.PackageId);
        var secondOwner = composition.ExtensionCatalog.BeginOwnerActivation(secondPackage.PackageId);
        composition.ExtensionCatalog.Add(firstOwner, TestPoint, new object());
        composition.ExtensionCatalog.Add(secondOwner, TestPoint, new object());
        state.SetLoadedPackage(
            firstPackage.PackageId,
            new AppLoadedPackageHandle(
                firstPackage,
                CreateSnapshot(firstPackage.PackageId),
                firstFolder,
                firstProvider,
                firstLoadContext)
            {
                ExtensionOwner = firstOwner,
            });
        state.SetLoadedPackage(
            secondPackage.PackageId,
            new AppLoadedPackageHandle(
                secondPackage,
                CreateSnapshot(secondPackage.PackageId),
                secondFolder,
                secondProvider,
                secondLoadContext)
            {
                ExtensionOwner = secondOwner,
            });

        try
        {
            var references = composition.ExtensionCatalog.GetExtensionReferences(TestPoint);

            Assert.True(composition.ExtensionCatalog.TryReportOwnerInvariantViolation(
                references[1],
                new InvalidOperationException("second owner invariant")));
            await Assert.IsAssignableFrom<Task>(disableTask).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(PackageFailureOrigin.AppUnhandledUi, reportedOrigin);
            Assert.NotNull(state.GetLoadedPackage(firstPackage.PackageId));
            Assert.Null(state.GetLoadedPackage(secondPackage.PackageId));
            Assert.False(state.IsPackageDisabled(firstPackage.PackageId));
            Assert.True(state.IsPackageDisabled(secondPackage.PackageId));
            var remaining = Assert.Single(composition.ExtensionCatalog.GetExtensionContributions(TestPoint));
            Assert.Equal(firstPackage.PackageId, remaining.PackageId);
        }
        finally
        {
            await composition.UnloadPackageAsync(firstPackage.PackageId);
            composition.DisposeSharedAssemblies();
            composition.Dispose();
            firstProvider.Dispose();
            secondProvider.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnloadPackageAsync_WaitsForOwnerLeaseBeforeProviderAndLoadContextDisposal()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var libraryFolder = Path.Combine(root, "lib");
        Directory.CreateDirectory(libraryFolder);
        var catalog = new AppPackageExtensionCatalog();
        var owner = catalog.BeginOwnerActivation("test.package");
        catalog.Add(owner, TestPoint, new object());
        var reference = Assert.Single(catalog.GetExtensionReferences(TestPoint));
        Assert.True(reference.TryAcquire(out var lease));
        var callbackExited = false;
        var providerDisposedDuringCallback = false;
        var loadContextUnloadedDuringCallback = false;
        var services = new ServiceCollection();
        var trackingDisposable = new TrackingDisposable(
            () => providerDisposedDuringCallback = !Volatile.Read(ref callbackExited));
        services.AddSingleton(_ => trackingDisposable);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TrackingDisposable>();
        using var sharedAssemblies = new AppSharedAssemblyRegistry([libraryFolder]);
        var loadContext = new AppPackageLoadContext(
            "test.package",
            typeof(AppPackageExtensionCatalog).Assembly.Location,
            sharedAssemblies,
            static (_, _) => { });
        loadContext.Unloading += _ =>
            loadContextUnloadedDuringCallback = !Volatile.Read(ref callbackExited);
        await using var backgroundProcesses = new BackgroundProcessQueueService();
        var coordinator = new AppPackageUnloadCoordinator(
            new AppPackageViewRegistry(new ImmediateUiDispatcher()),
            catalog,
            new AppPackageRuntimeWorkStopper(backgroundProcesses),
            new AppPackageAssemblyTracker(),
            sharedAssemblies,
            static _ => { },
            static _ => { });
        var package = new ActivePackageDescriptor(
            "test.package",
            "Test Package",
            "1.0.0",
            PackageHostRoles.App,
            Icon: null,
            IsEnabled: true,
            PackageReadinessState.Ready,
            Views: []);
        var snapshot = new PackageUiSnapshotDescriptor(
            "test.package",
            PackageSourceKind.Dev,
            1,
            new string('a', 64),
            "snapshot",
            "snapshot");
        var handle = new AppLoadedPackageHandle(package, snapshot, root, provider, loadContext)
        {
            ExtensionOwner = owner,
        };

        try
        {
            var unload = coordinator.UnloadPackageAsync(
                "test.package",
                handle,
                useRetirementDeadline: false);
            await Task.Yield();

            Assert.False(unload.IsCompleted);
            Assert.True(lease.RetirementToken.IsCancellationRequested);
            Assert.False(reference.TryAcquire(out _));
            Assert.False(providerDisposedDuringCallback);
            Assert.False(loadContextUnloadedDuringCallback);

            Volatile.Write(ref callbackExited, true);
            lease.Dispose();
            await unload.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(trackingDisposable.IsDisposed);
            Assert.False(providerDisposedDuringCallback);
            Assert.False(loadContextUnloadedDuringCallback);
        }
        finally
        {
            Volatile.Write(ref callbackExited, true);
            lease.Dispose();
            provider.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GenerationRetirement_BlockedFirstOwnerFencesAndDrainsSecondBeforeQuarantine()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var firstFolder = Path.Combine(root, "first");
        var secondFolder = Path.Combine(root, "second");
        var firstLibraryFolder = Path.Combine(firstFolder, "lib");
        var secondLibraryFolder = Path.Combine(secondFolder, "lib");
        Directory.CreateDirectory(firstLibraryFolder);
        Directory.CreateDirectory(secondLibraryFolder);
        var catalog = new AppPackageExtensionCatalog();
        var firstOwner = catalog.BeginOwnerActivation("first.package");
        var secondOwner = catalog.BeginOwnerActivation("second.package");
        catalog.Add(firstOwner, TestPoint, new object());
        catalog.Add(secondOwner, TestPoint, new object());
        var references = catalog.GetExtensionReferences(TestPoint);
        Assert.True(references[0].TryAcquire(out var firstLease));
        Assert.True(references[1].TryAcquire(out var secondLease));
        Assert.NotNull(firstLease);
        Assert.NotNull(secondLease);

        var firstDisposable = new TrackingDisposable(static () => { });
        var secondDisposable = new TrackingDisposable(static () => { });
        var firstProvider = new ServiceCollection()
            .AddSingleton(_ => firstDisposable)
            .BuildServiceProvider();
        var secondProvider = new ServiceCollection()
            .AddSingleton(_ => secondDisposable)
            .BuildServiceProvider();
        _ = firstProvider.GetRequiredService<TrackingDisposable>();
        _ = secondProvider.GetRequiredService<TrackingDisposable>();
        var sharedAssemblies = new AppSharedAssemblyRegistry([firstLibraryFolder, secondLibraryFolder]);
        var firstLoadContext = new AppPackageLoadContext(
            "first.package",
            typeof(AppPackageExtensionCatalog).Assembly.Location,
            sharedAssemblies,
            static (_, _) => { });
        var secondLoadContext = new AppPackageLoadContext(
            "second.package",
            typeof(AppPackageExtensionCatalog).Assembly.Location,
            sharedAssemblies,
            static (_, _) => { });
        var firstLoadContextUnloaded = false;
        var secondLoadContextUnloaded = false;
        firstLoadContext.Unloading += _ => firstLoadContextUnloaded = true;
        secondLoadContext.Unloading += _ => secondLoadContextUnloaded = true;
        var firstPackage = CreatePackage("first.package", "First Package");
        var secondPackage = CreatePackage("second.package", "Second Package");
        var firstSnapshot = CreateSnapshot("first.package");
        var secondSnapshot = CreateSnapshot("second.package");
        var state = new AppPackageHostState(
            [],
            [firstProvider, secondProvider],
            [firstLoadContext, secondLoadContext]);
        state.SetLoadedPackage(
            firstPackage.PackageId,
            new AppLoadedPackageHandle(firstPackage, firstSnapshot, firstFolder, firstProvider, firstLoadContext)
            {
                ExtensionOwner = firstOwner,
            });
        state.SetLoadedPackage(
            secondPackage.PackageId,
            new AppLoadedPackageHandle(secondPackage, secondSnapshot, secondFolder, secondProvider, secondLoadContext)
            {
                ExtensionOwner = secondOwner,
            });
        var viewRegistry = new AppPackageViewRegistry(new ImmediateUiDispatcher());
        await using var backgroundProcesses = new BackgroundProcessQueueService();
        var generationId = Guid.NewGuid();
        var composition = new AppPackageHostComposition(
            new object(),
            generationId,
            viewRegistry,
            state,
            static (_, _, _, _, _) => { },
            static (_, _, _, _, _) => { },
            sharedAssemblies,
            catalog,
            shellViewService: null,
            settingsNavigationService: null,
            notificationCenter: null,
            backgroundProcesses,
            getRuntimeConnectionInfo: null);
        var generation = new AppPackageGeneration(
            generationId,
            folder: null,
            [firstPackage, secondPackage],
            viewRegistry,
            state,
            composition,
            PackageIconGeneration.Empty());
        var secondCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondCancellation = secondLease!.RetirementToken.Register(() =>
        {
            secondCancellationObserved.TrySetResult();
            secondLease.Dispose();
        });
        Task? generationRetirement = null;
        await using var retirements = new AppPackageGenerationRetirementQueue(
            retireGenerationAsync: candidate => new ValueTask(
                generationRetirement = candidate.DisposeAsync().AsTask()),
            retirementBudget: TimeSpan.FromMilliseconds(100),
            drainBudget: TimeSpan.FromSeconds(2));

        try
        {
            retirements.Enqueue(generation);

            await secondCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(firstLease!.RetirementToken.IsCancellationRequested);
            Assert.False(references[0].TryAcquire(out _));
            Assert.False(references[1].TryAcquire(out _));
            await retirements.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, retirements.QuarantinedCount);
            Assert.Equal(1, retirements.FailureCount);
            await WaitUntilAsync(() => secondDisposable.IsDisposed);
            Assert.True(secondLoadContextUnloaded);
            Assert.False(firstDisposable.IsDisposed);
            Assert.False(firstLoadContextUnloaded);
        }
        finally
        {
            firstLease!.Dispose();
            secondLease!.Dispose();
            if (generationRetirement is not null)
            {
                await generationRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await WaitUntilAsync(() => firstDisposable.IsDisposed);
            Assert.True(firstLoadContextUnloaded);
            firstProvider.Dispose();
            secondProvider.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RollBackActivationAsync_WithoutOwnerDoesNotRetireCurrentSameIdEpoch()
    {
        var catalog = new AppPackageExtensionCatalog();
        var owner = catalog.BeginOwnerActivation("test.package");
        catalog.Add(owner, TestPoint, new object());
        var reference = Assert.Single(catalog.GetExtensionReferences(TestPoint));
        using var sharedAssemblies = new AppSharedAssemblyRegistry([]);
        await using var backgroundProcesses = new BackgroundProcessQueueService();
        var assemblyTracker = new AppPackageAssemblyTracker();
        assemblyTracker.RegisterPackageAssembly("test.package", typeof(AppPackageExtensionCatalogTests).Assembly);
        var coordinator = new AppPackageUnloadCoordinator(
            new AppPackageViewRegistry(new ImmediateUiDispatcher()),
            catalog,
            new AppPackageRuntimeWorkStopper(backgroundProcesses),
            assemblyTracker,
            sharedAssemblies,
            static _ => { },
            static _ => { });

        await coordinator.RollBackActivationAsync(
            "test.package",
            packageInfo: null,
            serviceProvider: null,
            loadContext: null,
            extensionOwner: null,
            stopRuntimeWork: false);

        Assert.True(reference.TryAcquire(out var lease));
        Assert.Contains(
            assemblyTracker.SnapshotPackageAssemblies(),
            entry => entry.PackageId == "test.package"
                     && ReferenceEquals(entry.Assembly, typeof(AppPackageExtensionCatalogTests).Assembly));
        lease.Dispose();
        await catalog.BeginOwnerRetirement(owner).Completion;
    }

    private static ActivePackageDescriptor CreatePackage(string packageId, string displayName)
        => new(
            packageId,
            displayName,
            "1.0.0",
            PackageHostRoles.App,
            Icon: null,
            IsEnabled: true,
            PackageReadinessState.Ready,
            Views: []);

    private static PackageUiSnapshotDescriptor CreateSnapshot(string packageId)
        => new(
            packageId,
            PackageSourceKind.Dev,
            1,
            new string('a', 64),
            "snapshot",
            "snapshot");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for App package generation retirement.");
            }
            await Task.Delay(20);
        }
    }

    private sealed class TrackingDisposable(Action onDispose) : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            onDispose();
            IsDisposed = true;
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
}
