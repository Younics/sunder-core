using Microsoft.Extensions.DependencyInjection;
using Sunder.Protocol;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ActiveDevPackageSessionTests
{
    [Fact]
    public void MarkPackageFailed_DisablesSessionPackageAndReturnsLoadedPackageForDeactivation()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActiveDevPackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedDevPackage>(StringComparer.OrdinalIgnoreCase)
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
        extensionCatalog.Add("test.package", extensionPoint, new TestContribution("test"));
        extensionCatalog.Add("other.package", extensionPoint, new TestContribution("other"));
        var session = new ActiveDevPackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedDevPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateLoadedPackage("test.package"),
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
        var session = new ActiveDevPackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedDevPackage>(StringComparer.OrdinalIgnoreCase)
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
        var session = new ActiveDevPackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedDevPackage>(StringComparer.OrdinalIgnoreCase)
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

    private static ActiveLoadedDevPackage CreateLoadedPackage(string packageId)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var assemblyPath = typeof(DevPackageLoadPlanner).Assembly.Location;
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        return new ActiveLoadedDevPackage(
            CreateActivePackage(packageId, isEnabled: true, PackageReadinessState.Ready),
            new PackageSourceDescriptor(packageId, PackageSourceKind.Dev, tempDirectory),
            ConfigurationSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(tempDirectory, "state.json")),
            new JsonPackageSecretsStore(Path.Combine(tempDirectory, "secrets.json")),
            AuthHandler: null,
            BackgroundServices: [new TestBackgroundService()],
            serviceProvider,
            new ActiveDevPackageLoadContext(
                packageId,
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])));
    }

    private static ActivePackageDescriptor CreateActivePackage(
        string packageId,
        bool isEnabled,
        PackageReadinessState readiness)
        => new(packageId, packageId, "1.0.0", Icon: null, isEnabled, readiness, Views: []);

    private static SessionPackageDescriptor CreateSessionPackage(string packageId, bool isEnabled)
        => new(
            packageId,
            packageId,
            "1.0.0",
            Icon: null,
            isEnabled,
            isEnabled ? PackageReadinessState.Ready : PackageReadinessState.Failed,
            Views: [],
            FailureOrigin: null,
            LastError: null,
            LastFailureAtUtc: null,
            FailureCount: 0);

    private sealed class TestBackgroundService : IPackageBackgroundService
    {
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private interface ITestContribution
    {
        string Name { get; }
    }

    private sealed record TestContribution(string Name) : ITestContribution;
}
