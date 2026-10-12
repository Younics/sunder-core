using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.ActivePolicyTestContext;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeRpcBrokerTests
{
    [Theory]
    [InlineData("caller.package")]
    [InlineData("provider.package")]
    [InlineData("expiry")]
    [InlineData("platform")]
    public async Task ActivePolicy_DeniesNextDispatchBeforePeriodicWorkerRetirement(string blocked)
    {
        using var policy = new ActivePolicyTestContext();
        var admission = new PackagePolicyAdmission(policy.Cache);
        await using var fixture = new RpcFixture(packagePolicy: admission);
        var called = 0;
        var handler = new TestHandler { Unary = (_, _) => { called++; return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true })); } };
        var activation = await fixture.PublishAsync(handler, transform: AttachPolicy);
        var subjects = activation.Session.LoadedPackageMap.Values.Select(value => new RegistryPackageVersionIdentity(value.Descriptor.PackageId, "1.0.0", Hash(value.Descriptor.PackageId))).ToArray();
        await policy.RefreshAsync(subjects, [Platform]);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        await client.InvokeAsync(endpoint, "messages", "send", JsonSerializer.SerializeToElement(new { message = "before" }));
        Assert.Equal(1, called);
        if (blocked == "expiry") policy.Clock.Advance(TimeSpan.FromHours(24));
        else
        {
            policy.Response = request =>
            {
                var result = Allowed(request, 2);
                return blocked == "platform"
                    ? result with { PlatformCatalogs = [new(Platform, "Revoked", "Denied", PackagePolicyErrorCodes.ContractAuthorityRevoked, ObservedAt)] }
                    : result with { Packages = result.Packages.Select(value => value.Package.PackageId != blocked ? value : value with
                    { PackageState = "SecurityBlocked", Disposition = "Denied", ErrorCode = PackagePolicyErrorCodes.SecurityBlocked }).ToArray() };
            };
            if (blocked == "platform") await policy.RefreshAsync([], [Platform]);
            else await policy.RefreshAsync(subjects, [Platform]);
        }
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(endpoint, "messages", "send", JsonSerializer.SerializeToElement(new { message = "after" })).AsTask());
        Assert.Equal(blocked switch { "expiry" => PackagePolicyErrorCodes.PolicyExpired, "platform" => PackagePolicyErrorCodes.ContractAuthorityRevoked, _ => PackagePolicyErrorCodes.SecurityBlocked }, failure.Error.Code);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, failure.Error.DispatchState);
        Assert.Equal(1, called);
        if (blocked == "provider.package") Assert.Empty((await client.DiscoverAsync("example.rpc")).Providers);
    }

    [Fact]
    public async Task ActivePolicy_HostRefreshWithdrawsProvidersStopsWorkerAndPreservesData()
    {
        using var policy = new ActivePolicyTestContext();
        var admission = new PackagePolicyAdmission(policy.Cache);
        await using var fixture = new RpcFixture(packagePolicy: admission);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = await fixture.PublishAsync(new TestHandler(), transform: package =>
        {
            var result = AttachPolicy(package);
            return package.Descriptor.PackageId == "provider.package"
                ? result with
                {
                    Worker = new TestRuntimePackageWorker(disposeAsync: () => { disposed.TrySetResult(); return ValueTask.CompletedTask; }),
                    ExecutionRuntimeBinding = TestExecutionRuntimeLaunchResolver.CreateBinding("runtime", "osx-arm64"),
                }
                : result;
        });
        await activation.Session.CommitRuntimeGenerationAsync(fixture.Owner.Generation);
        var provider = activation.Session.LoadedPackageMap["provider.package"];
        var sentinel = Path.Combine(provider.Source.SourceFolder, "package-data-sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserved");
        using var network = new RegistryPackagePolicySynchronizer(policy.Client, policy.Cache);
        using var service = new ActivePackagePolicySynchronizer(new(policy.Paths), fixture.Owner.State, fixture.Owner, network, admission, policy.Cache,
            policy.Clock, policy.Time, NullLogger<ActivePackagePolicySynchronizer>.Instance);
        await service.RefreshInstalledAsync(default);
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
        policy.Response = request => Allowed(request, 2) with
        {
            Packages = request.Packages.Select(subject => new RegistryPackagePolicyDecision(subject, subject.PackageId == "provider.package" ? "SecurityBlocked" : "Active",
                "Listed", subject.PackageId == "provider.package" ? "Denied" : "Allowed", subject.PackageId == "provider.package" ? PackagePolicyErrorCodes.SecurityBlocked : null, null, [])).ToArray(),
        };
        await service.RefreshInstalledAsync(default);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
        Assert.Equal("preserved", await File.ReadAllTextAsync(sentinel));
    }

    private static ActiveLoadedPackage AttachPolicy(ActiveLoadedPackage package) => package with
    {
        Source = package.Source with
        {
            RegistryPolicyOrigin = Origin,
            RegistryPolicyContentIdentity = Hash(package.Descriptor.PackageId),
            ContentIdentity = Hash("local-projection-" + package.Descriptor.PackageId),
        },
    };

    [Fact]
    public async Task ActivePolicy_LeaseExpiryStopsWorkersWhileBackgroundHttpIsStillWaiting()
    {
        using var policy = new ActivePolicyTestContext();
        var admission = new PackagePolicyAdmission(policy.Cache);
        await using var fixture = new RpcFixture(packagePolicy: admission);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = await fixture.PublishAsync(new TestHandler(), transform: package => AttachPolicy(package) with
        {
            Worker = new TestRuntimePackageWorker(disposeAsync: () => { if (package.Descriptor.PackageId == "provider.package") disposed.TrySetResult(); return ValueTask.CompletedTask; }),
            ExecutionRuntimeBinding = TestExecutionRuntimeLaunchResolver.CreateBinding("runtime", "osx-arm64"),
        });
        await activation.Session.CommitRuntimeGenerationAsync(fixture.Owner.Generation);
        var subjects = activation.Session.LoadedPackageMap.Values.Select(value => new RegistryPackageVersionIdentity(value.Descriptor.PackageId, "1.0.0", Hash(value.Descriptor.PackageId))).ToArray();
        await policy.RefreshAsync(subjects);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        policy.Send = async (_, token) => { waiting.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); };
        using var network = new RegistryPackagePolicySynchronizer(policy.Client, policy.Cache);
        using var service = new ActivePackagePolicySynchronizer(new(policy.Paths), fixture.Owner.State, fixture.Owner, network, admission, policy.Cache,
            policy.Clock, policy.Time, NullLogger<ActivePackagePolicySynchronizer>.Instance);
        await service.StartAsync(default);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            policy.Clock.Advance(TimeSpan.FromHours(24));
            policy.Time.Advance(TimeSpan.FromSeconds(1));
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
        }
        finally { await service.StopAsync(default); }
    }
}
