using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;
using static Sunder.Runtime.Host.Tests.RuntimeRpcTestPackages;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcPermissionStoreTests
{
    [Fact]
    public async Task PermissionStore_IsDurableFencedAndCancelsOnDenial()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        try
        {
            CancellationToken revocation;
            var key = PermissionKey(SunderRpcProtocol.InvokeAction);
            using (var store = new RuntimeRpcPermissionStore(paths))
            {
                await store.SetAsync(
                    key,
                    RuntimeRpcPermissionState.Granted,
                    CancellationToken.None);
                Assert.True(store.TryAcquire(key, out var lease));
                revocation = lease!.RevocationToken;
                Assert.False(store.TryAcquire(
                    key with { CallerPackageVersion = "2.0.0" },
                    out _));
                Assert.False(store.TryAcquire(
                    key with { ManifestSha256 = new string('b', 64) },
                    out _));
                Assert.False(store.TryAcquire(
                    key with { CallerRole = SunderPackageFormat.AppHostRole },
                    out _));
                Assert.False(store.TryAcquire(
                    key with { ContractVersion = "2.0.0" },
                    out _));
                Assert.False(store.TryAcquire(
                    key with { AbiSha256 = new string('b', 64) },
                    out _));
                await store.SetAsync(
                    key,
                    RuntimeRpcPermissionState.Denied,
                    CancellationToken.None);
                Assert.True(revocation.IsCancellationRequested);
                using var document = JsonDocument.Parse(File.ReadAllBytes(paths.RpcPermissionFilePath));
                Assert.Equal(2, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            }
            using var reloaded = new RuntimeRpcPermissionStore(paths);
            Assert.False(reloaded.TryAcquire(key, out _));
            Assert.Contains(
                reloaded.GetSnapshot([]).Permissions,
                permission => permission.Key == key
                              && permission.State == RuntimeRpcPermissionState.Denied);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionStore_ExactRoleRevocationCancelsOnlyMatchingLease()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(new RuntimePackagePaths(root));
            var runtimeKey = PermissionKey(SunderRpcProtocol.InvokeAction);
            var appKey = runtimeKey with { CallerRole = SunderPackageFormat.AppHostRole };
            await store.SetAsync(runtimeKey, RuntimeRpcPermissionState.Granted, CancellationToken.None);
            await store.SetAsync(appKey, RuntimeRpcPermissionState.Granted, CancellationToken.None);
            Assert.True(store.TryAcquire(runtimeKey, out var runtimeLease));
            Assert.True(store.TryAcquire(appKey, out var appLease));
            using var runtimeChanges = store.ObserveChanges(runtimeKey);
            using var appChanges = store.ObserveChanges(appKey);
            using var closedObserver = store.ObserveChanges(runtimeKey);
            closedObserver.Dispose();

            await store.SetAsync(runtimeKey, RuntimeRpcPermissionState.Denied, CancellationToken.None);

            Assert.True(runtimeLease!.RevocationToken.IsCancellationRequested);
            Assert.True(runtimeChanges.RevocationToken.IsCancellationRequested);
            Assert.False(closedObserver.RevocationToken.IsCancellationRequested);
            Assert.False(appChanges.RevocationToken.IsCancellationRequested);
            Assert.False(appLease!.RevocationToken.IsCancellationRequested);
            Assert.True(store.TryAcquire(appKey, out _));
            using var freshChanges = store.ObserveChanges(runtimeKey);
            Assert.False(freshChanges.RevocationToken.IsCancellationRequested);
            store.Dispose();
            Assert.True(freshChanges.RevocationToken.IsCancellationRequested);
            Assert.True(appChanges.RevocationToken.IsCancellationRequested);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionStore_RevocationCallbacksRunAfterWriteGateIsReleased()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(new RuntimePackagePaths(root));
            var invokeKey = PermissionKey(SunderRpcProtocol.InvokeAction);
            var discoverKey = PermissionKey(SunderRpcProtocol.DiscoverAction);
            await store.SetAsync(invokeKey, RuntimeRpcPermissionState.Granted, CancellationToken.None);
            Assert.True(store.TryAcquire(invokeKey, out var lease));
            using var registration = lease!.RevocationToken.Register(() =>
                store.SetAsync(
                        discoverKey,
                        RuntimeRpcPermissionState.Granted,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());

            await Task.Run(() => store.SetAsync(
                    invokeKey,
                    RuntimeRpcPermissionState.Denied,
                    CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(store.IsGranted(discoverKey));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionStore_InstallPolicyGrantsDeclarationsAndPreservesExactDenialUntilUpdate()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        var subject = PermissionSubject(PackageSourceKind.Installed);
        var runtimeUse = Assert.Single(subject.ContractUses);
        subject = subject with
        {
            ContractUses =
            [
                runtimeUse,
                new SunderPackageContractUseManifest
                {
                    Role = SunderPackageFormat.AppHostRole,
                    ContractId = runtimeUse.ContractId,
                    ContractVersion = runtimeUse.ContractVersion,
                    AbiSha256 = runtimeUse.AbiSha256,
                    Required = runtimeUse.Required,
                    Actions = runtimeUse.Actions,
                },
            ],
        };
        try
        {
            using (var store = new RuntimeRpcPermissionStore(paths))
            {
                await store.GrantDeclaredInstalledActionsAsync([subject], CancellationToken.None);

                Assert.Equal(1, store.Revision);
                Assert.All(
                    new[] { SunderPackageFormat.AppHostRole, SunderPackageFormat.RuntimeHostRole },
                    role => Assert.All(
                        new[] { "discover", "invoke", "subscribe" },
                        action => Assert.True(store.TryAcquire(
                            PermissionKey(
                                action,
                                callerRole: role,
                                packageId: subject.PackageId,
                                packageVersion: subject.PackageVersion,
                                manifestSha256: subject.ManifestSha256),
                            out _))));

                var deniedKey = PermissionKey(
                    SunderRpcProtocol.InvokeAction,
                    packageId: subject.PackageId,
                    packageVersion: subject.PackageVersion,
                    manifestSha256: subject.ManifestSha256);
                await store.SetAsync(
                    deniedKey,
                    RuntimeRpcPermissionState.Denied,
                    CancellationToken.None);
                var deniedRevision = store.Revision;
                await store.GrantDeclaredInstalledActionsAsync([subject], CancellationToken.None);

                Assert.False(store.TryAcquire(deniedKey, out _));
                Assert.True(store.TryAcquire(
                    deniedKey with { CallerRole = SunderPackageFormat.AppHostRole },
                    out _));
                Assert.Equal(deniedRevision, store.Revision);

                var updatedSubject = subject with
                {
                    PackageVersion = "2.0.0",
                    ManifestSha256 = new string('b', 64),
                };
                await store.GrantDeclaredInstalledActionsAsync([updatedSubject], CancellationToken.None);
                Assert.True(store.TryAcquire(
                    deniedKey with
                    {
                        CallerPackageVersion = updatedSubject.PackageVersion,
                        ManifestSha256 = updatedSubject.ManifestSha256,
                    },
                    out _));
                subject = updatedSubject;
            }

            using var reloaded = new RuntimeRpcPermissionStore(paths);
            Assert.All(
                reloaded.GetSnapshot([subject]).Permissions,
                permission =>
                {
                    Assert.Equal(RuntimeRpcPermissionState.Granted, permission.State);
                    Assert.True(permission.Effective);
                });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PermissionStore_DevelopmentPolicyIsEffectiveWithoutDurableRecords()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(paths);
            var snapshot = store.GetSnapshot([PermissionSubject(PackageSourceKind.Dev)]);

            Assert.Equal(0, snapshot.Revision);
            Assert.Equal(3, snapshot.Permissions.Count);
            Assert.All(snapshot.Permissions, permission =>
            {
                Assert.Equal(RuntimeRpcPermissionState.Granted, permission.State);
                Assert.True(permission.Effective);
                Assert.Null(permission.UpdatedAtUtc);
            });
            Assert.False(File.Exists(paths.RpcPermissionFilePath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionService_RejectsDurableDevelopmentPermissionMutation()
    {
        await using var fixture = new RpcFixture();
        await fixture.PublishAsync(
            new TestHandler(),
            webCaller: true,
            callerSourceKind: PackageSourceKind.Dev);

        await Assert.ThrowsAsync<RuntimeValidationException>(() => fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(
                PermissionKey(SunderRpcProtocol.InvokeAction)),
            RuntimeRpcPermissionState.Denied));
        Assert.False(File.Exists(fixture.PermissionFilePath));
    }

    [Fact]
    public async Task PermissionService_UsesExactRoleAndRejectsStaleCallerFence()
    {
        await using var fixture = new RpcFixture();
        await fixture.PublishAsync(new TestHandler(), webCaller: true);
        var app = await fixture.OpenAppCallerAsync();
        var runtimeKey = PermissionKey(
            SunderRpcProtocol.InvokeAction,
            manifestSha256: app.ManifestSha256);
        var runtimeSnapshot = await fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(runtimeKey),
            RuntimeRpcPermissionState.Denied);
        var runtimePermission = Assert.Single(runtimeSnapshot.Permissions, permission =>
            permission.Key == runtimeKey);
        Assert.Equal(RuntimeRpcPermissionState.Denied, runtimePermission.State);

        var appKey = runtimeKey with { CallerRole = SunderPackageFormat.AppHostRole };
        var appSnapshot = await fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(appKey),
            RuntimeRpcPermissionState.Granted);
        Assert.Equal(
            RuntimeRpcPermissionState.Denied,
            Assert.Single(appSnapshot.Permissions, permission => permission.Key == runtimeKey).State);
        Assert.Equal(
            RuntimeRpcPermissionState.Granted,
            Assert.Single(appSnapshot.Permissions, permission => permission.Key == appKey).State);

        await Assert.ThrowsAsync<RuntimeValidationException>(() => fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(runtimeKey with
            {
                ManifestSha256 = new string('f', 64),
            }),
            RuntimeRpcPermissionState.Granted));
        await Assert.ThrowsAsync<RuntimeValidationException>(() => fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(runtimeKey with
            {
                AbiSha256 = new string('f', 64),
            }),
            RuntimeRpcPermissionState.Granted));
    }

    private static RuntimeRpcPermissionSubject PermissionSubject(PackageSourceKind sourceKind)
        => new(
            "caller.package",
            "1.0.0",
            new string('a', 64),
            sourceKind,
            [
                new SunderPackageContractUseManifest
                {
                    Role = SunderPackageFormat.RuntimeHostRole,
                    ContractId = "example.rpc",
                    ContractVersion = "1.0.0",
                    AbiSha256 = Contract().AbiSha256,
                    Required = true,
                    Actions = ["discover", "invoke", "subscribe"],
                },
            ]);
}
