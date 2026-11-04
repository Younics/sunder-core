using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sunder.App.Bridge;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Package.Projections;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class AppBridgeIsolationTests
{
    [Theory]
    [Trait("Capability", "Process")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task RetiredProjectionRemainsLeasedUntilItsPackageContextIsCollected(bool rollback, bool releaseFailure)
        => TemporaryDirectoryScope.RunAsync("sunder-app-projection-lifetime", async scope =>
        {
            var source = scope.CreateDirectory("source");
            CopyBridgeFixture(source);
            var files = Directory.EnumerateFiles(source).Select(path => new PackageProjectionSourceFile(
                Path.GetFileName(path), Path.GetFileName(path), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length)).ToArray();
            var cache = new PackageProjectionCache(scope.GetPath("runtime"), new() { GarbageCollectionGracePeriod = TimeSpan.Zero });
            await using var projection = await cache.GetOrBuildAndLeaseAsync(new(source, "test.retired", new string('a', 64),
                PackageProjectionRole.App, PackageProjectionCache.DefaultRulesIdentity, [$"app/{AppPackageTargetEnvironment.CurrentRid}"], files),
                new("app", Guid.NewGuid(), "test.retired", "1"));
            var leasePath = Assert.Single(Directory.EnumerateFiles(Path.Combine(cache.RootPath, "leases"), "*.json", SearchOption.AllDirectories));
            var leaseBytes = File.ReadAllBytes(leasePath);
            // A failed durable release must leave ownership in place, rather than
            // treating the successful local drain/ALC collection as deletion proof.
            var reference = ActivateAndRetire(scope.CreateDirectory("state"), projection.RootPath, rollback, cache, projection,
                beforeCollection: releaseFailure ? () => File.WriteAllText(leasePath, "invalid lease") : null);
            try
            {
                CollectContext(reference);
                await AppPackageFileReclamation.ReclaimAsync();
                if (releaseFailure) Assert.True(File.Exists(leasePath));
            }
            finally { if (releaseFailure) File.WriteAllBytes(leasePath, leaseBytes); }
            if (releaseFailure)
            {
                Assert.Equal(1, (await cache.CollectGarbageAsync(new HashSet<string>())).ActiveLeaseCount);
                Assert.True(Directory.Exists(projection.RootPath));
                return;
            }
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(cache.RootPath, "leases"), "*.json", SearchOption.AllDirectories));
            await cache.CollectGarbageAsync(new HashSet<string>());
            var collected = await cache.CollectGarbageAsync(new HashSet<string>());
            Assert.Equal(1, collected.TombstonedEntryCount);
            Assert.False(Directory.Exists(projection.RootPath));
        });

    [Theory]
    [Trait("Capability", "Process")]
    [InlineData(false)]
    [InlineData(true)]
    public Task RetiredDevelopmentGenerationPreservesLoadedChildFiles(bool rollback)
        => TemporaryDirectoryScope.RunAsync("sunder-app-development-lifetime", async scope =>
        {
            var generation = scope.CreateDirectory("generation");
            var folder = scope.CreateDirectory("generation", "package");
            CopyBridgeFixture(folder);
            var reference = ActivateAndRetire(scope.CreateDirectory("state"), folder, rollback,
                generationDirectory: new AppPackageGenerationDirectory(generation));
            CollectContext(reference);
            await AppPackageFileReclamation.ReclaimAsync();
            Assert.False(Directory.Exists(generation));
        });

    private static void CopyBridgeFixture(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "bridge-fixtures", "one"), "*.dll"))
            File.Copy(path, Path.Combine(folder, Path.GetFileName(path)));
        WriteDeps(folder, 1);
    }

    private static void CollectContext(WeakReference reference)
    {
        for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(reference.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ActivateAndRetire(
        string state, string folder, bool rollback, PackageProjectionCache? cache = null,
        PackageProjectionAcquisition? projection = null, AppPackageGenerationDirectory? generationDirectory = null,
        Action? beforeCollection = null)
    {
        using var shared = new AppSharedAssemblyRegistry();
        var context = new AppPackageLoadContext("test.retired", Path.Combine(folder, "Package.dll"),
            AppPackageTargetEnvironment.CurrentRid, shared, (_, _) => { });
        using var reflection = context.EnterContextualReflection();
        var entry = (IAppPackageEntryPointV1)Activator.CreateInstance(context.LoadPackageEntryAssembly().GetType("EntryPoint")!)!;
        var sink = new Sink();
        var activation = entry.CreateAsync(new ProbeHost(state, "test.retired"), sink).GetAwaiter().GetResult();
        activation.Start(default);
        var view = sink.Factory!();
        using var runtimeWork = new BackgroundTaskQueueService();
        var unload = new AppPackageUnloadCoordinator(new AppPackageViewRegistry(new InlineUiDispatcher()),
            new AppPackageRuntimeWorkStopper(runtimeWork), new AppPackageAssemblyTracker(), _ => { }, _ => { });
        var package = new ActivePackageDescriptor("test.retired", "Retired", "1.0.0", PackageHostRoles.App, null,
            true, PackageReadinessState.Ready, []);
        var snapshot = new PackageUiSnapshotDescriptor("test.retired", projection is null ? PackageSourceKind.Dev : PackageSourceKind.Installed, 1,
            RuntimeContractTestData.AppTarget("Package.dll"), projection?.Reference.CacheKey ?? new string('a', 64), new string('b', 32), "");
        var outcome = (rollback
            ? unload.RollBackActivationAsync("test.retired", new(package, folder, new(), snapshot), null, context, activation, projection, stopRuntimeWork: false)
            : unload.UnloadPackageAsync("test.retired", new(package, snapshot, folder, null, context, activation, projection))).GetAwaiter().GetResult();
        Assert.Equal(AppPackageUiCleanupOutcome.Proven, outcome);
        Assert.True(File.Exists(Path.Combine(state, "disposed")));
        // A lingering view callback still owns package code after local work drains.
        // Maintenance must preserve its immutable files until the ALC really dies.
        if (cache is not null)
        {
            Assert.Single(Directory.EnumerateFiles(Path.Combine(cache.RootPath, "leases"), "*.json", SearchOption.AllDirectories));
            var maintenance = cache.CollectGarbageAsync(new HashSet<string>()).GetAwaiter().GetResult();
            Assert.Equal(1, maintenance.ActiveLeaseCount);
            Assert.Equal(1, maintenance.PreservedEntryCount);
        }
        generationDirectory?.Release();
        AppPackageFileReclamation.ReclaimAsync().GetAwaiter().GetResult();
        Assert.True(File.Exists(Path.Combine(folder, "Package.dll")));
        beforeCollection?.Invoke();
        GC.KeepAlive(view);
        return new WeakReference(context, trackResurrection: true);
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
