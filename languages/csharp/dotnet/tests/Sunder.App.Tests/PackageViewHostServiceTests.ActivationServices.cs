using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class PackageViewHostServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppActivationServicesStartOnlyAfterPublicationWithoutViewNavigationAndDrainOnRevocation(bool publish)
    {
        using var scope = TemporaryDirectoryScope.Create("app-activation-service");
        var package = CreateActiveAgentPackage();
        var folder = CreateAppPackageSource(scope.RootPath, package.PackageId);
        var evidence = scope.GetPath("startup-evidence");
        File.WriteAllText(Path.Combine(folder, ShellLifecycleTestPackageModule.ActivationServicePathFileName), evidence);
        File.WriteAllText(Path.Combine(folder, ShellLifecycleTestPackageModule.SkipViewMarkerFileName), string.Empty);
        var source = RuntimeContractTestData.Snapshot(package.PackageId, PackageSourceKind.Dev, folder);
        await using var host = TestAppPackageHost.Create(new AppPackageViewRegistry(), [], [], [], scope.GetPath("session"),
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync, uiDispatcher: TestUiDispatcher);
        var preparationObserved = false;
        var application = host.ApplyPackageGenerationAsync(CreateRuntimeSnapshot(package, source, 1), [source], null,
            (_, _, _) =>
            {
                preparationObserved = true;
                Assert.False(File.Exists(evidence));
                if (!publish) throw new InvalidOperationException("Reject the prepared candidate before publication.");
                return Task.FromResult<Action?>(null);
            }, default);
        if (!publish)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => application);
            await host.WaitForRetirementsAsync();
            Assert.True(preparationObserved);
            Assert.False(File.Exists(evidence));
            return;
        }
        await application;
        Assert.True(preparationObserved);
        Assert.Equal(["started"], File.ReadAllLines(evidence));
        // This fixture has no tracked retention fingerprint, so the next
        // generation deliberately creates a fresh activation rather than reuse.
        await host.ApplyPackageDeltaAsync([package], [source]);
        await host.WaitForRetirementsAsync();
        Assert.Equal(2, File.ReadAllLines(evidence).Count(line => line == "started"));
        Assert.Single(File.ReadAllLines(evidence), line => line == "revoked");
        Assert.Single(File.ReadAllLines(evidence), line => line == "disposed");
        await host.ApplyPackageDeltaAsync([], []);
        await host.WaitForRetirementsAsync();
        Assert.Equal(2, File.ReadAllLines(evidence).Count(line => line == "revoked"));
        Assert.Equal(2, File.ReadAllLines(evidence).Count(line => line == "disposed"));
    }
}

public sealed class ShellLifecycleActivationService(string evidence) : IPackageAppActivationService, IAsyncDisposable
{
    private CancellationTokenRegistration _registration;
    public void Start(CancellationToken activationCancellationToken)
    {
        activationCancellationToken.ThrowIfCancellationRequested();
        File.AppendAllLines(evidence, ["started"]);
        _registration = activationCancellationToken.Register(() => File.AppendAllLines(evidence, ["revoked"]));
    }
    public ValueTask DisposeAsync()
    {
        _registration.Dispose();
        File.AppendAllLines(evidence, ["disposed"]);
        return ValueTask.CompletedTask;
    }
}
