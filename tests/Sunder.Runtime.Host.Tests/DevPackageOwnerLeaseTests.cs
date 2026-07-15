using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class DevPackageOwnerLeaseTests
{
    [Fact]
    public async Task WarmRuntime_LeaseKeepsInstanceAndReleaseRestoresInstalledPackage()
    {
        var fixture = CreateFixture();
        var installed = CreatePackageLayout(fixture.Paths.RootPath, "installed", "test.package", "1.0.0");
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "2.0.0").InstallPath;
        try
        {
            await AddInstalledPackageAsync(fixture.Store, installed);
            Assert.True((await fixture.Host.LoadInstalledPackagesAsync()).Success);
            var runtimeInstanceId = fixture.Host.RuntimeInstanceId;

            var lease = await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(runtimeInstanceId, "owner-token-aaaaaaaaaaaaaaaaaaaa", "mutation-1", 1, dev, watch: true));

            Assert.Equal(runtimeInstanceId, lease.RuntimeInstanceId);
            Assert.Equal("2.0.0", Assert.Single(fixture.Host.GetActivePackages()).Version);
            await fixture.Host.ReleaseDevPackageOwnerAsync(
                "owner-a",
                new DevPackageOwnerReleaseRequest(runtimeInstanceId, "owner-token-aaaaaaaaaaaaaaaaaaaa"));
            Assert.Equal("1.0.0", Assert.Single(fixture.Host.GetActivePackages()).Version);
            Assert.Equal(runtimeInstanceId, fixture.Host.RuntimeInstanceId);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task DesiredSetReplacement_RemovesDevOnlyPackageNotInNextRevision()
    {
        var fixture = CreateFixture();
        var first = CreatePackageLayout(fixture.Paths.RootPath, "dev-a", "a.package", "1.0.0").InstallPath;
        var second = CreatePackageLayout(fixture.Paths.RootPath, "dev-b", "b.package", "1.0.0").InstallPath;
        const string token = "owner-token-bbbbbbbbbbbbbbbbbbbb";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, first, watch: false));
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-2", 2, second, watch: false));

            Assert.Equal(["b.package"], fixture.Host.GetActivePackages().Select(package => package.PackageId));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task MutationRetry_IsIdempotent()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        var request = Mutation(
            fixture.Host.RuntimeInstanceId,
            "owner-token-cccccccccccccccccccc",
            "mutation-1",
            1,
            dev,
            watch: false);
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            var first = await fixture.Host.ReplaceDevPackageOwnerAsync("owner-a", request);
            var generation = fixture.Host.SessionGeneration;

            var retry = await fixture.Host.ReplaceDevPackageOwnerAsync("owner-a", request);

            Assert.Equal(first.Revision, retry.Revision);
            Assert.Equal(first.MutationId, retry.MutationId);
            Assert.Equal(generation, fixture.Host.SessionGeneration);
            Assert.True(retry.ExpiresAtUtc >= first.ExpiresAtUtc);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task MutationRetry_WithChangedPayload_IsRejected()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        const string token = "owner-token-payload-payload-payload";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, dev, watch: false));

            await Assert.ThrowsAsync<RuntimeConflictException>(() =>
                fixture.Host.ReplaceDevPackageOwnerAsync(
                    "owner-a",
                    Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, dev, watch: true)));

            Assert.False((await fixture.Host.GetPackageSessionStatusAsync("test.package"))?.WatchEnabled);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReleaseRetry_IsIdempotent()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        const string token = "owner-token-release-release-release";
        var release = new DevPackageOwnerReleaseRequest(fixture.Host.RuntimeInstanceId, token);
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, dev, watch: false));

            await fixture.Host.ReleaseDevPackageOwnerAsync("owner-a", release);
            await fixture.Host.ReleaseDevPackageOwnerAsync("owner-a", release);

            Assert.Empty(fixture.Host.GetActivePackages());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task WrongOwnerToken_IsRejectedWithoutChangingSession()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        const string token = "owner-token-dddddddddddddddddddd";
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, token, "mutation-1", 1, dev, watch: false));
            var generation = fixture.Host.SessionGeneration;

            await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => fixture.Host.HeartbeatDevPackageOwnerAsync(
                "owner-a",
                new DevPackageOwnerHeartbeatRequest(
                    fixture.Host.RuntimeInstanceId,
                    "wrong-owner-token-xxxxxxxxxxxxxxxx")));

            Assert.Equal(generation, fixture.Host.SessionGeneration);
            Assert.True(fixture.Host.ContainsDevPackageOwner("owner-a"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task DifferentFoldersForSamePackageId_AreRejectedAtomically()
    {
        var fixture = CreateFixture();
        var first = CreatePackageLayout(fixture.Paths.RootPath, "dev-a", "test.package", "1.0.0").InstallPath;
        var second = CreatePackageLayout(fixture.Paths.RootPath, "dev-b", "test.package", "2.0.0").InstallPath;
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-eeeeeeeeeeeeeeeeeeee", "mutation-1", 1, first, watch: false));
            var generation = fixture.Host.SessionGeneration;

            await Assert.ThrowsAsync<RuntimeConflictException>(() => fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-b",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-ffffffffffffffffffff", "mutation-1", 1, second, watch: false)));

            Assert.Equal(generation, fixture.Host.SessionGeneration);
            Assert.Equal("1.0.0", Assert.Single(fixture.Host.GetActivePackages()).Version);
            Assert.False(fixture.Host.ContainsDevPackageOwner("owner-b"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameFolderCoownership_AggregatesWatchAndReleasesIndependently()
    {
        var fixture = CreateFixture();
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-gggggggggggggggggggg", "mutation-1", 1, dev, watch: false));
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-b",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-hhhhhhhhhhhhhhhhhhhh", "mutation-1", 1, dev, watch: true));
            Assert.True((await fixture.Host.GetPackageSessionStatusAsync("test.package"))?.WatchEnabled);

            await fixture.Host.ReleaseDevPackageOwnerAsync(
                "owner-b",
                new DevPackageOwnerReleaseRequest(fixture.Host.RuntimeInstanceId, "owner-token-hhhhhhhhhhhhhhhhhhhh"));
            Assert.False((await fixture.Host.GetPackageSessionStatusAsync("test.package"))?.WatchEnabled);
            Assert.Single(fixture.Host.GetActivePackages());

            await fixture.Host.ReleaseDevPackageOwnerAsync(
                "owner-a",
                new DevPackageOwnerReleaseRequest(fixture.Host.RuntimeInstanceId, "owner-token-gggggggggggggggggggg"));
            Assert.Empty(fixture.Host.GetActivePackages());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExpiredLease_IsReapedAndRemovesDevOnlyPackage()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var fixture = CreateFixture(
            new RuntimeLifecyclePolicyOptions { DevPackageOwnerLeaseLifetime = TimeSpan.FromSeconds(10) },
            clock);
        var dev = CreatePackageLayout(fixture.Paths.RootPath, "dev", "test.package", "1.0.0").InstallPath;
        try
        {
            await fixture.Host.LoadInstalledPackagesAsync();
            await fixture.Host.ReplaceDevPackageOwnerAsync(
                "owner-a",
                Mutation(fixture.Host.RuntimeInstanceId, "owner-token-iiiiiiiiiiiiiiiiiiii", "mutation-1", 1, dev, watch: false));

            clock.Advance(TimeSpan.FromSeconds(11));
            await fixture.Host.ReapDevPackageOwnersAsync();

            Assert.Empty(fixture.Host.GetActivePackages());
            Assert.False(fixture.Host.ContainsDevPackageOwner("owner-a"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static DevPackageOwnerMutationRequest Mutation(
        Guid runtimeInstanceId,
        string token,
        string mutationId,
        long revision,
        string folder,
        bool watch)
        => new(
            runtimeInstanceId,
            token,
            mutationId,
            revision,
            [new DevPackageOwnerFolder(folder, watch)]);

    private static TestFixture CreateFixture(
        RuntimeLifecyclePolicyOptions? policy = null,
        TimeProvider? timeProvider = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-dev-owner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        var store = new InstalledPackageStore(paths);
        var installer = new SunderPackageArchiveInstaller(paths);
        var host = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            store,
            installer,
            lifecyclePolicy: policy,
            timeProvider: timeProvider);
        host.MarkBootstrapReady();
        return new TestFixture(paths, store, host);
    }

    private static InstalledPackageRecord CreatePackageLayout(
        string rootPath,
        string folderName,
        string packageId,
        string version)
    {
        var packageFolder = Path.Combine(rootPath, folderName, packageId, version);
        var libraryFolder = Path.Combine(packageFolder, "lib");
        Directory.CreateDirectory(libraryFolder);
        var assemblyPath = typeof(PackageSessionOverlayTestPackageModule).Assembly.Location;
        var entryAssemblyFileName = Path.GetFileName(assemblyPath);
        File.WriteAllText(Path.Combine(packageFolder, "sunder-package.json"), $$"""
            {
              "manifestVersion": 1,
              "id": "{{packageId}}",
              "name": "{{packageId}}",
              "version": "{{version}}",
              "hostRoles": ["app", "runtime"],
              "sdkApiVersion": 1,
              "sdkPackageVersion": "1.1.0",
              "requiredSdkCapabilities": ["sdk-baseline-1-1.v1", "core.v1"],
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
        return new InstalledPackageRecord(
            packageId,
            packageId,
            Summary: null,
            version,
            entryAssemblyFileName,
            Icon: null,
            DependsOn: [],
            packageFolder,
            IsEnabled: true,
            DateTimeOffset.UtcNow);
    }

    private static async Task AddInstalledPackageAsync(InstalledPackageStore store, InstalledPackageRecord package)
        => await store.WriteAsync((await store.ListAsync()).Append(package).ToArray());

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed record TestFixture(
        RuntimePackagePaths Paths,
        InstalledPackageStore Store,
        RuntimePackageSessionTestHost Host) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Host.ShutdownAsync();
            try
            {
                Directory.Delete(Paths.RootPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}
