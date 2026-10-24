using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeRpcBrokerTests
{
    [Fact]
    public async Task FailedScopeContentCleanupCompletesItsOriginalTeardownWithoutLosingTheFailure()
    {
        var caller = new RuntimeRpcHostCallerActivation(Contract(), CancellationToken.None);
        var registry = new RuntimeRpcCallScopeRegistry(1, TimeProvider.System, CancellationToken.None,
            _ => throw new IOException("Content cleanup failed."));
        Assert.Equal(RuntimeRpcCallScopeRegistry.Outcome.Success, registry.Register(caller, 1, new object(), DateTimeOffset.UtcNow.AddMinutes(1),
            null, false, default, default, TimeSpan.FromMinutes(1), out var scope));
        await Assert.ThrowsAsync<IOException>(() => registry.CloseAsync(scope!).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAsync<IOException>(() => registry.CloseAsync(scope!).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(scope!.IsRevoked);
        Assert.True(scope.TeardownCompletion.IsFaulted);
    }

    [Fact]
    public async Task AppScopeCleanupRemainsAuthorizedAfterCallerSessionRetiresButCannotCloseAnotherOwnersScope()
    {
        await using var fixture = await RetiringRpcFixture.CreateAsync(new TestHandler());
        var id = Guid.NewGuid();
        var app = await fixture.OpenAsync(id, await fixture.PermitAsync(id));
        var scope = fixture.Broker.CreateAppCallScope(app.SessionId, null, default);
        var foreign = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Broker.CloseAppCallScopeAsync("foreign-session", scope.Id).AsTask());
        Assert.Equal("rpc.scope.unavailable", foreign.Error.Code);
        Assert.False(scope.IsRevoked);
        fixture.Manager.Close(app.SessionId);
        await fixture.Broker.CloseAppCallScopeAsync(app.SessionId, scope.Id);
        await fixture.Broker.CloseAppCallScopeAsync(app.SessionId, scope.Id);
        Assert.True(scope.IsRevoked);
        await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Broker.DiscoverAppAsync(app.SessionId, "example.rpc", default).AsTask());
    }

    [Fact]
    public async Task ActivationRetirementDrainsIgnoredCancellationAndEveryTransportScopeWithoutRetiringAnotherActivation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await RetiringRpcFixture.CreateAsync(new TestHandler { Unary = async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await finish.Task;
            return JsonSerializer.SerializeToElement(new { accepted = true });
        } });
        var id = Guid.NewGuid();
        var permit = await fixture.PermitAsync(id);
        var first = await fixture.OpenAsync(id, permit);
        var second = await fixture.OpenAsync(id, permit);
        var unrelatedId = Guid.NewGuid();
        var unrelatedPermit = await fixture.PermitAsync(unrelatedId);
        var unrelated = await fixture.OpenAsync(unrelatedId, unrelatedPermit);
        var scope = fixture.Broker.CreateAppCallScope(second.SessionId, null, default);
        var endpoint = Assert.Single((await fixture.Broker.DiscoverAppAsync(first.SessionId, "example.rpc", default)).Providers).Endpoint;
        using var content = new MemoryStream([1, 2, 3]);
        await fixture.Broker.RegisterScopeContentAsync(scope, endpoint, content, new("application/octet-stream", "test.bin"), default);
        using var uploadSource = new NonCancellingReadStream();
        var upload = fixture.Broker.RegisterScopeContentAsync(scope, endpoint, uploadSource, new("application/octet-stream", "pending.bin"), default).AsTask();
        await uploadSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var call = fixture.Broker.InvokeAppAsync(first.SessionId, endpoint, "messages", "send", JsonSerializer.SerializeToElement(new { message = "drain" }), null, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retiring = fixture.Policies.RetireActivationAsync(new(fixture.Session, id, permit.PermitId!), fixture.Peer, default);
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retiring.IsCompleted);
            await Assert.ThrowsAsync<RuntimeAppPolicyException>(() => fixture.OpenAsync(id, permit));
            await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Broker.DiscoverAppAsync(second.SessionId, "example.rpc", default).AsTask());
            Assert.Single((await fixture.Broker.DiscoverAppAsync(unrelated.SessionId, "example.rpc", default)).Providers);
            finish.TrySetResult();
            await Assert.ThrowsAsync<SunderRpcException>(() => call);
            Assert.False(retiring.IsCompleted); // The uncooperative content registration is still executing.
        }
        finally { finish.TrySetResult(); uploadSource.Finish.TrySetResult(); }
        await Assert.ThrowsAsync<SunderRpcException>(() => upload);
        Assert.Equal("retired", (await retiring.WaitAsync(TimeSpan.FromSeconds(5))).State);
        Assert.True(scope.IsRevoked);
        Assert.Empty(Directory.GetFiles(fixture.Policy.Paths.TransferRootPath));
        var empty = ActivePackageSession.CreateEmpty();
        Assert.Single(fixture.Policies.RetirementRequirements(empty));
        await fixture.Policies.RetireActivationAsync(new(fixture.Session, unrelatedId, unrelatedPermit.PermitId!), fixture.Peer, default);
        using var publication = await fixture.Policies.FencePublicationAsync(empty, default);
        Assert.False((await fixture.Policies.HeartbeatAsync(fixture.Session, fixture.Peer, default)).ShutdownRequired);
        await empty.DisposeAsync();
    }

    [Fact]
    public async Task OpeningCallerCannotEscapeRetirementWhileMetadataIsLoading()
    {
        await using var fixture = await RetiringRpcFixture.CreateAsync(new TestHandler());
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new RuntimeRpcAppSessionManager(fixture.Raw.Owner.State, new(), fixture.Policies)
        {
            LoadContracts = async (source, token) => { loading.TrySetResult(); await finish.Task; return await RuntimeRpcPackageMetadata.LoadContractsAsync(source, token); },
        };
        var id = Guid.NewGuid();
        var permit = await fixture.PermitAsync(id);
        var opening = manager.OpenAsync(fixture.OpenRequest(id, permit), default, fixture.Peer);
        await loading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retiring = fixture.Policies.RetireActivationAsync(new(fixture.Session, id, permit.PermitId!), fixture.Peer, default);
        Assert.False(retiring.IsCompleted);
        finish.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => opening);
        Assert.Equal("retired", (await retiring.WaitAsync(TimeSpan.FromSeconds(5))).State);
        await Assert.ThrowsAsync<RuntimeAppPolicyException>(() => manager.OpenAsync(fixture.OpenRequest(id, permit), default, fixture.Peer));
    }

    [Fact]
    public async Task ContentReadAlreadyExecutingRemainsInActivationDrainAfterStreamDisposal()
    {
        var authority = new AppPolicyActivationAuthority();
        var source = new NonCancellingReadStream();
        using var streamLifetime = authority.Acquire();
        using var stream = new RuntimeRpcContentReadStream(source, streamLifetime.Dispose, authority.RetirementToken,
            acquireOperation: authority.Acquire);
        var read = stream.ReadAsync(new byte[1]).AsTask();
        await source.Entered.Task;
        var retirement = authority.RetireAsync();
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(retirement.IsCompleted);
        source.Finish.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<SunderRpcException>(() => stream.ReadByte());
    }

    private sealed class NonCancellingReadStream : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Finish.Task; return 0; }
        protected override void Dispose(bool disposing) { Disposed.TrySetResult(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task FailedContentDisposalCannotAcknowledgeActivationRetirement()
    {
        var authority = new AppPolicyActivationAuthority();
        using var lifetime = authority.Acquire();
        using var stream = new RuntimeRpcContentReadStream(new FailingDisposalStream(), lifetime.Dispose,
            authority.RetirementToken, acquireOperation: authority.Acquire, cleanupFailed: authority.RecordCleanupFailure);
        await Assert.ThrowsAsync<RuntimeUnavailableException>(() => authority.RetireAsync());
        Assert.False(authority.IsActive);
        await Assert.ThrowsAsync<RuntimeUnavailableException>(() => authority.RetireAsync());
    }

    private sealed class FailingDisposalStream : MemoryStream
    {
        protected override void Dispose(bool disposing) => throw new IOException("Resource release was not acknowledged.");
    }

    private sealed class RetiringRpcFixture : IAsyncDisposable
    {
        internal RpcFixture Raw { get; } = new();
        internal ActivePolicyTestContext Policy { get; } = new();
        internal AppPolicyPeer Peer { get; } = new(123, null);
        internal AppProcessPolicySessions Policies { get; private set; } = null!;
        internal RuntimeRpcAppSessionManager Manager { get; private set; } = null!;
        internal RuntimeRpcBroker Broker { get; private set; } = null!;
        internal RuntimeAppPolicySessionRequest Session { get; private set; } = null!;
        private RuntimePackageSource _source = null!;
        private PackageTargetDescriptor _target = null!;
        private RuntimeRpcPermissionStore _permissions = null!;
        private RuntimeContentTransferStore _content = null!;
        internal static async Task<RetiringRpcFixture> CreateAsync(TestHandler handler, IAppPolicyProcessController? process = null)
        {
            var value = new RetiringRpcFixture();
            var published = await value.Raw.PublishAsync(handler, webCaller: true, callerSourceKind: PackageSourceKind.Dev,
                transform: package => package.Descriptor.PackageId == "caller.package" ? package with { Source = package.Source with { ContentIdentity = new('c', 64) } } : package);
            value._source = published.Session.LoadedPackageMap["caller.package"].Source;
            value._target = PackageTargetSelection.ToDescriptor(value._source.Manifest!.Targets!.Single()!);
            value._permissions = new(value.Policy.Paths);
            value._content = new(value.Policy.Paths);
            var protocol = new RuntimeProtocolDescriptor();
            value.Policies = new(value.Policy.Paths, protocol, new RequiredProviderSnapshots(value._source), new(value.Policy.Cache), value.Policy.Cache,
                value.Policy.Clock, value.Policy.Time, process ?? new RequiredProviderProcess(), NullLogger<AppProcessPolicySessions>.Instance, value.Raw.Owner.State, new(value._permissions));
            var identity = new RuntimeAppProcessIdentity(123, "birth", new('a', 64), "test", Guid.NewGuid(), protocol.RuntimeInstanceId);
            var session = await value.Policies.OpenAsync(new(identity), value.Peer, default);
            value.Session = new(session.SessionId, identity.AppGenerationId, protocol.RuntimeInstanceId);
            value.Manager = new(value.Raw.Owner.State, new(), value.Policies);
            value.Broker = new(value.Raw.Catalog, value._permissions, value.Raw.Owner.State, value.Raw.Owner, appSessions: value.Manager, contentStore: value._content);
            return value;
        }
        internal Task<RuntimeAppPackageLoadPermitResult> PermitAsync(Guid id) => Policies.PermitAsync(new(Session, PackageUiSnapshotStore.PolicySubject(_source)!)
            { SnapshotId = new('a', 32), AppActivationId = id, AppTarget = _target }, Peer, default);
        internal RuntimeRpcAppSessionOpenRequest OpenRequest(Guid id, RuntimeAppPackageLoadPermitResult permit)
            => new(_source.PackageId, "1.0.0", RuntimeRpcPackageMetadata.GetManifestSha256(_source.ContentIndex!), _target, Raw.Owner.Generation, id) { LoadPermitId = permit.PermitId };
        internal Task<RuntimeRpcAppSessionDescriptor> OpenAsync(Guid id, RuntimeAppPackageLoadPermitResult permit) => Manager.OpenAsync(OpenRequest(id, permit), default, Peer);
        public async ValueTask DisposeAsync() { Manager.Dispose(); Policies.Dispose(); _content.Dispose(); _permissions.Dispose(); await Raw.DisposeAsync(); Policy.Dispose(); }
    }
}
