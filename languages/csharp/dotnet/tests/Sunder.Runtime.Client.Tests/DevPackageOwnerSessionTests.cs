using System.Net;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed class DevPackageOwnerSessionTests
{
    private static readonly DevPackageOwnerSessionOptions FastOptions = new()
    {
        ReleaseTimeout = TimeSpan.FromMilliseconds(100),
        MinimumHeartbeatDelay = TimeSpan.FromMilliseconds(10),
        MaximumHeartbeatDelay = TimeSpan.FromMilliseconds(20),
    };

    [Fact]
    public async Task Acquire_SeparatesOwnerCredentialsAndIncludesLocalProcessFence()
    {
        var runtimeId = Guid.NewGuid();
        var connection = new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "runtime-bearer-token");
        var client = new OwnerClient(runtimeId);
        await using var session = CreateSession(client, () => connection);

        var lease = await session.AcquireAsync([Path.Combine(".", "dev-a")], watch: true);

        var mutation = Assert.Single(client.Mutations);
        Assert.NotEqual(connection.BearerToken, session.OwnerToken);
        Assert.Equal(session.OwnerId, client.OwnerId);
        Assert.Equal(session.OwnerToken, mutation.OwnerToken);
        Assert.Equal(runtimeId, mutation.RuntimeInstanceId);
        Assert.Equal(1, mutation.Revision);
        Assert.Equal(Environment.ProcessId, mutation.ProcessId);
        Assert.NotNull(mutation.ProcessStartedAtUtc);
        Assert.True(Assert.Single(mutation.Folders).Watch);
        Assert.Equal(runtimeId, lease.RuntimeInstanceId);

        await session.ReleaseAsync();
        Assert.Equal(session.OwnerToken, Assert.Single(client.Releases).OwnerToken);
    }

    [Fact]
    public async Task Acquire_RetriesUncertainMutationWithSameIdentity()
    {
        var client = new OwnerClient(Guid.NewGuid()) { FailMutationOnCall = 1 };
        await using var session = CreateSession(client);
        var folders = new[] { Path.Combine(".", "dev-a") };

        await Assert.ThrowsAsync<HttpRequestException>(() => session.AcquireAsync(folders, watch: false));
        await session.AcquireAsync(folders, watch: false);

        Assert.Equal(2, client.Mutations.Count);
        Assert.Equal(client.Mutations[0].MutationId, client.Mutations[1].MutationId);
        Assert.Equal(client.Mutations[0].Revision, client.Mutations[1].Revision);
    }

    [Fact]
    public async Task Acquire_ReconcilesUncertainMutationBeforeApplyingChangedDesiredSet()
    {
        var client = new OwnerClient(Guid.NewGuid()) { FailMutationOnCall = 1 };
        await using var session = CreateSession(client);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => session.AcquireAsync([Path.Combine(".", "dev-a")], watch: false));
        await session.AcquireAsync([Path.Combine(".", "dev-b")], watch: true);

        Assert.Equal(3, client.Mutations.Count);
        Assert.Equal(client.Mutations[0].MutationId, client.Mutations[1].MutationId);
        Assert.Equal(1, client.Mutations[1].Revision);
        Assert.Equal(2, client.Mutations[2].Revision);
        Assert.EndsWith("dev-a", Assert.Single(client.Mutations[1].Folders).Folder, StringComparison.Ordinal);
        Assert.EndsWith("dev-b", Assert.Single(client.Mutations[2].Folders).Folder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Heartbeat_ReconcilesAnUncertainCommittedRevision()
    {
        var client = new OwnerClient(Guid.NewGuid())
        {
            FailMutationOnCall = 2,
            LeaseLifetime = TimeSpan.FromMilliseconds(30),
        };
        await using var session = CreateSession(client);
        await session.AcquireAsync([Path.Combine(".", "dev-a")], watch: false);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => session.AcquireAsync([Path.Combine(".", "dev-b")], watch: true));
        await client.HeartbeatObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.AcquireAsync([Path.Combine(".", "dev-b")], watch: true);

        Assert.Equal(2, client.Mutations.Count);
        Assert.Equal(2, client.Mutations[1].Revision);
    }

    [Fact]
    public async Task Release_AfterUncertainAcquireUsesPendingRuntimeIdentityAndCanRetry()
    {
        var runtimeId = Guid.NewGuid();
        var client = new OwnerClient(runtimeId)
        {
            FailMutationOnCall = 1,
            FailReleaseOnCall = 1,
        };
        await using var session = CreateSession(client);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => session.AcquireAsync([Path.Combine(".", "dev-a")], watch: false));
        await Assert.ThrowsAsync<HttpRequestException>(() => session.ReleaseAsync());
        await session.ReleaseAsync();

        Assert.Equal(2, client.Releases.Count);
        Assert.All(client.Releases, release => Assert.Equal(runtimeId, release.RuntimeInstanceId));
    }

    [Fact]
    public async Task Heartbeat_ReacquiresDesiredSetAfterRuntimeReplacement()
    {
        var firstRuntimeId = Guid.NewGuid();
        var secondRuntimeId = Guid.NewGuid();
        var client = new OwnerClient(firstRuntimeId) { LeaseLifetime = TimeSpan.FromMilliseconds(30) };
        await using var session = CreateSession(client);
        await session.AcquireAsync(
            [Path.Combine(".", "dev-a"), Path.Combine(".", "dev-b")],
            watch: true);

        client.RuntimeId = secondRuntimeId;
        await client.SecondMutation.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = client.Mutations[1];
        Assert.Equal(secondRuntimeId, replacement.RuntimeInstanceId);
        Assert.Equal(1, replacement.Revision);
        Assert.Equal(client.Mutations[0].Folders, replacement.Folders);
    }

    [Fact]
    public async Task Heartbeat_ReacquiresDesiredSetWhenSameRuntimeReapsLease()
    {
        var runtimeId = Guid.NewGuid();
        var client = new OwnerClient(runtimeId)
        {
            LeaseLifetime = TimeSpan.FromMilliseconds(30),
            RejectFirstHeartbeat = true,
        };
        await using var session = CreateSession(client);
        await session.AcquireAsync([Path.Combine(".", "dev-a")], watch: true);

        await client.SecondMutation.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = client.Mutations[1];
        Assert.Equal(runtimeId, replacement.RuntimeInstanceId);
        Assert.Equal(1, replacement.Revision);
        Assert.NotEqual(client.Mutations[0].MutationId, replacement.MutationId);
    }

    [Fact]
    public async Task HeartbeatFault_IsReportedWithoutTerminatingOwnerMaintenance()
    {
        var faultObserved = new TaskCompletionSource<(string Operation, Exception Exception)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new OwnerClient(Guid.NewGuid())
        {
            LeaseLifetime = TimeSpan.FromMilliseconds(30),
            FailHeartbeatOnCall = 1,
        };
        await using var session = CreateSession(
            client,
            reportFault: (operation, exception) => faultObserved.TrySetResult((operation, exception)));
        await session.AcquireAsync([Path.Combine(".", "dev-a")], watch: false);

        var fault = await faultObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("heartbeat", fault.Operation);
        Assert.IsType<HttpRequestException>(fault.Exception);
    }

    [Fact]
    public async Task Dispose_CancelsBlockedHeartbeatReacquisitionBeforeWaitingForMutationGate()
    {
        var faults = new List<(string Operation, Exception Exception)>();
        var client = new OwnerClient(Guid.NewGuid())
        {
            BlockMutationOnCall = 2,
            LeaseLifetime = TimeSpan.FromMilliseconds(30),
        };
        var session = CreateSession(
            client,
            reportFault: (operation, exception) => faults.Add((operation, exception)),
            options: new DevPackageOwnerSessionOptions
            {
                ReleaseTimeout = TimeSpan.FromSeconds(1),
                MinimumHeartbeatDelay = TimeSpan.FromMilliseconds(10),
                MaximumHeartbeatDelay = TimeSpan.FromMilliseconds(20),
            });
        await session.AcquireAsync([Path.Combine(".", "dev-a")], watch: false);

        client.RuntimeId = Guid.NewGuid();
        await client.SecondMutation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync();

        Assert.Empty(faults);
        Assert.Single(client.Releases);
    }

    [Fact]
    public async Task ReleaseIsBoundedAndDisposeReportsCleanupFault()
    {
        var faults = new List<(string Operation, Exception Exception)>();
        var client = new OwnerClient(Guid.NewGuid()) { BlockRelease = true };
        var session = CreateSession(client, reportFault: (operation, exception) => faults.Add((operation, exception)));
        await session.AcquireAsync([Path.Combine(".", "dev-a")], watch: false);

        await Assert.ThrowsAsync<TimeoutException>(() => session.ReleaseAsync());
        await session.DisposeAsync();

        var fault = Assert.Single(faults);
        Assert.Equal("release", fault.Operation);
        Assert.IsType<TimeoutException>(fault.Exception);
        Assert.True(client.Releases.Count >= 2);
    }

    private static DevPackageOwnerSession CreateSession(
        OwnerClient client,
        Func<RuntimeConnectionInfo?>? getConnection = null,
        Action<string, Exception>? reportFault = null,
        DevPackageOwnerSessionOptions? options = null)
    {
        var connection = new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), "runtime-bearer-token");
        return new DevPackageOwnerSession(
            () => client,
            getConnection ?? (() => connection),
            "test",
            reportFault,
            options ?? FastOptions);
    }

    private sealed class OwnerClient(Guid runtimeId) : IDevPackageOwnerClient
    {
        private Guid _runtimeId = runtimeId;
        private int _heartbeatCount;

        public List<DevPackageOwnerMutationRequest> Mutations { get; } = [];
        public List<DevPackageOwnerReleaseRequest> Releases { get; } = [];
        public string? OwnerId { get; private set; }
        public int? FailMutationOnCall { get; init; }
        public int? BlockMutationOnCall { get; init; }
        public int? FailReleaseOnCall { get; init; }
        public int? FailHeartbeatOnCall { get; init; }
        public bool RejectFirstHeartbeat { get; init; }
        public bool BlockRelease { get; init; }
        public TimeSpan LeaseLifetime { get; init; } = TimeSpan.FromMinutes(1);
        public TaskCompletionSource SecondMutation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeartbeatObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid RuntimeId
        {
            get => _runtimeId;
            set => _runtimeId = value;
        }

        public Task<RuntimeHandshakeResponse> GetRuntimeHandshakeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new RuntimeHandshakeResponse(
                RuntimeProtocol.Identity,
                RuntimeProtocol.CurrentRevision,
                RuntimeProtocol.MinimumSupportedRevision,
                RuntimeProtocol.MaximumSupportedRevision,
                RuntimeId,
                [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.DevPackageOwnerLeasesV1],
                new RuntimeProductVersionDiagnostics("Sunder.Runtime.Host", "1.0.0", "test")));

        public async Task<DevPackageOwnerLeaseResponse> ReplaceDevPackageOwnerAsync(
            string ownerId,
            DevPackageOwnerMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            OwnerId = ownerId;
            Mutations.Add(request);
            if (Mutations.Count == 2)
            {
                SecondMutation.TrySetResult();
            }
            if (Mutations.Count == FailMutationOnCall)
            {
                throw new HttpRequestException("response lost");
            }
            if (Mutations.Count == BlockMutationOnCall)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Response(ownerId, request);
        }

        public Task<DevPackageOwnerLeaseResponse> HeartbeatDevPackageOwnerAsync(
            string ownerId,
            DevPackageOwnerHeartbeatRequest request,
            CancellationToken cancellationToken = default)
        {
            HeartbeatObserved.TrySetResult();
            var heartbeatCount = Interlocked.Increment(ref _heartbeatCount);
            if (RejectFirstHeartbeat && heartbeatCount == 1)
            {
                throw new RuntimeClientException(
                    HttpStatusCode.Unauthorized,
                    "Authentication required",
                    "Development package owner credentials are invalid.");
            }
            if (heartbeatCount == FailHeartbeatOnCall)
            {
                throw new HttpRequestException("heartbeat response lost");
            }

            return Task.FromResult(Response(ownerId, Mutations[^1]));
        }

        public async Task ReleaseDevPackageOwnerAsync(
            string ownerId,
            DevPackageOwnerReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            Releases.Add(request);
            if (Releases.Count == FailReleaseOnCall)
            {
                throw new HttpRequestException("release response lost");
            }
            if (BlockRelease)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        private DevPackageOwnerLeaseResponse Response(
            string ownerId,
            DevPackageOwnerMutationRequest request)
            => new(
                request.RuntimeInstanceId,
                ownerId,
                request.MutationId,
                request.Revision,
                DateTimeOffset.UtcNow.Add(LeaseLifetime),
                request.Revision,
                request.Folders.Select(folder => new DevPackageOwnerPackage(Path.GetFileName(folder.Folder), folder.Watch)).ToArray());

        public void Dispose()
        {
        }
    }
}
