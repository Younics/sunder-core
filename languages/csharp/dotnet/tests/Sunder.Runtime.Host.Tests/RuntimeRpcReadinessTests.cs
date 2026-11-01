using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;
using static Sunder.Runtime.Host.Tests.RuntimeRpcTestPackages;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcReadinessTests
{
    [Fact]
    public async Task RpcReadiness_RequiresCompatibleProviderAndEveryActionGrant()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(new RuntimePackagePaths(root));
            var descriptor = Contract();
            var caller = CreatePackage("caller.package", descriptor, null, usesContract: true);
            var provider = CreatePackage("provider.package", descriptor, new TestHandler(), usesContract: false);
            var session = CreateSession(caller, provider);
            var evaluator = new RuntimeRpcReadinessEvaluator(store);

            Assert.Equal(["caller.package"], evaluator.Evaluate(session));
            Assert.Equal(
                PackageReadinessState.Degraded,
                session.GetSessionPackages().Single(package => package.PackageId == "caller.package").Readiness);

            foreach (var action in new[]
                     {
                         SunderRpcProtocol.DiscoverAction,
                         SunderRpcProtocol.InvokeAction,
                         SunderRpcProtocol.SubscribeAction,
                     })
            {
                await store.SetAsync(
                    PermissionKey(action),
                    RuntimeRpcPermissionState.Granted,
                    CancellationToken.None);
            }
            Assert.Equal(["caller.package"], evaluator.Evaluate(session));
            Assert.Equal(
                PackageReadinessState.Ready,
                session.GetSessionPackages().Single(package => package.PackageId == "caller.package").Readiness);

            Assert.True(session.MarkPackageFailed(
                "provider.package",
                PackageFailureOrigin.RuntimeRpcProvider,
                "faulted",
                out _));
            Assert.Equal(["caller.package"], evaluator.Evaluate(session));
            Assert.Equal(
                PackageReadinessState.Degraded,
                session.GetSessionPackages().Single(package => package.PackageId == "caller.package").Readiness);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RpcReadiness_OwnerAndOptionalUsesDoNotRequirePublicGrant()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(new RuntimePackagePaths(root));
            var descriptor = Contract();
            var owner = CreatePackage(
                "owner.package",
                descriptor,
                new TestHandler(),
                usesContract: true,
                providerAudience: SunderPackageFormat.OwnerProviderAudience);
            var optional = CreatePackage("optional.package", descriptor, null, usesContract: true);
            var optionalUse = optional.RpcContractUses.Single();
            optional = optional with
            {
                RpcContractUses =
                [
                    new SunderPackageContractUseManifest
                    {
                        Role = optionalUse.Role,
                        ContractId = optionalUse.ContractId,
                        ContractVersion = optionalUse.ContractVersion,
                        AbiSha256 = optionalUse.AbiSha256,
                        Required = false,
                        Actions = optionalUse.Actions,
                    },
                ],
            };
            var evaluator = new RuntimeRpcReadinessEvaluator(store);
            var ownerSession = CreateSession(owner);
            var optionalSession = CreateSession(optional);

            Assert.Empty(evaluator.Evaluate(ownerSession));
            Assert.Empty(evaluator.Evaluate(optionalSession));
            Assert.All(ownerSession.GetSessionPackages(), package =>
                Assert.Equal(PackageReadinessState.Ready, package.Readiness));
            Assert.All(optionalSession.GetSessionPackages(), package =>
                Assert.Equal(PackageReadinessState.Ready, package.Readiness));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RpcReadiness_MetadataRefreshPreservesSessionGeneration()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        using var store = new RuntimeRpcPermissionStore(paths);
        var evaluator = new RuntimeRpcReadinessEvaluator(store);
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events,
            rpcReadiness: evaluator);
        var descriptor = Contract();
        var session = CreateSession(
            CreatePackage("caller.package", descriptor, null, usesContract: true),
            CreatePackage("provider.package", descriptor, new TestHandler(), usesContract: false));
        evaluator.Evaluate(session);
        try
        {
            await owner.PublishAsync(
                session,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0);
            var degraded = owner.GetSnapshot();
            Assert.Equal(1, degraded.SessionGeneration);
            Assert.Equal(
                PackageReadinessState.Degraded,
                degraded.SessionPackages.Single(package => package.PackageId == "caller.package").Readiness);

            foreach (var action in new[]
                     {
                         SunderRpcProtocol.DiscoverAction,
                         SunderRpcProtocol.InvokeAction,
                         SunderRpcProtocol.SubscribeAction,
                     })
            {
                await store.SetAsync(
                    PermissionKey(action),
                    RuntimeRpcPermissionState.Granted,
                    CancellationToken.None);
            }
            Assert.True(owner.RefreshRpcReadiness());
            var ready = owner.GetSnapshot();
            Assert.Equal(degraded.SessionGeneration, ready.SessionGeneration);
            Assert.True(ready.EventSequence > degraded.EventSequence);
            Assert.Equal(
                PackageReadinessState.Ready,
                ready.SessionPackages.Single(package => package.PackageId == "caller.package").Readiness);
            Assert.Contains(
                events.GetSnapshot(degraded.EventSequence).Events,
                runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.SnapshotMetadataChanged
                                && runtimeEvent.SessionGeneration == degraded.SessionGeneration);

            await store.SetAsync(
                PermissionKey(SunderRpcProtocol.InvokeAction),
                RuntimeRpcPermissionState.Denied,
                CancellationToken.None);
            Assert.True(owner.RefreshRpcReadiness());
            Assert.Equal(
                PackageReadinessState.Degraded,
                owner.GetSnapshot().SessionPackages.Single(package => package.PackageId == "caller.package").Readiness);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
