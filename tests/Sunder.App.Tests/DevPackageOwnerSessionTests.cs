using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class DevPackageOwnerSessionTests
{
    [Fact]
    public async Task Acquire_UsesInvocationCredentialsAndCompleteWatchSettings()
    {
        var runtimeId = Guid.NewGuid();
        var runtimeUrl = new Uri("http://127.0.0.1:5275/");
        var connection = new RuntimeConnectionState(runtimeUrl);
        connection.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "runtime-bearer-token"));
        var client = new OwnerClient(runtimeId);
        await using var session = new DevPackageOwnerSession(new OwnerClientFactory(client), connection);
        var folders = new[] { Path.Combine(".", "dev-a"), Path.Combine(".", "dev-b") };

        var lease = await session.AcquireAsync(folders, watch: true);

        Assert.NotEqual("runtime-bearer-token", session.OwnerToken);
        Assert.Equal(session.OwnerId, client.OwnerId);
        Assert.Equal(session.OwnerToken, client.Mutation?.OwnerToken);
        Assert.Equal(runtimeId, client.Mutation?.RuntimeInstanceId);
        Assert.Equal(1, client.Mutation?.Revision);
        Assert.Equal(2, client.Mutation?.Folders.Count);
        Assert.All(client.Mutation!.Folders, folder => Assert.True(folder.Watch));
        Assert.Equal(runtimeId, lease.RuntimeInstanceId);

        await session.ReleaseAsync();
        Assert.NotNull(client.Release);
        Assert.Equal(session.OwnerToken, client.Release?.OwnerToken);
    }

    [Fact]
    public async Task Acquire_RetriesUncertainMutationWithSameIdAndRevision()
    {
        var runtimeId = Guid.NewGuid();
        var runtimeUrl = new Uri("http://127.0.0.1:5275/");
        var connection = new RuntimeConnectionState(runtimeUrl);
        connection.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "runtime-bearer-token"));
        var client = new OwnerClient(runtimeId) { FailFirstMutation = true };
        await using var session = new DevPackageOwnerSession(new OwnerClientFactory(client), connection);
        var folders = new[] { Path.Combine(".", "dev-a") };

        await Assert.ThrowsAsync<HttpRequestException>(() => session.AcquireAsync(folders, watch: false));
        await session.AcquireAsync(folders, watch: false);

        Assert.Equal(2, client.Mutations.Count);
        Assert.Equal(client.Mutations[0].MutationId, client.Mutations[1].MutationId);
        Assert.Equal(client.Mutations[0].Revision, client.Mutations[1].Revision);
    }

    [Fact]
    public async Task Heartbeat_ReacquiresDesiredSetAfterRuntimeWorkerReplacement()
    {
        var firstRuntimeId = Guid.NewGuid();
        var secondRuntimeId = Guid.NewGuid();
        var runtimeUrl = new Uri("http://127.0.0.1:5275/");
        var connection = new RuntimeConnectionState(runtimeUrl);
        connection.SetConnection(new RuntimeConnectionInfo(runtimeUrl, "runtime-bearer-token"));
        var client = new OwnerClient(firstRuntimeId) { LeaseLifetime = TimeSpan.FromMilliseconds(30) };
        await using var session = new DevPackageOwnerSession(new OwnerClientFactory(client), connection);
        var folders = new[] { Path.Combine(".", "dev-a"), Path.Combine(".", "dev-b") };
        await session.AcquireAsync(folders, watch: true);

        client.RuntimeId = secondRuntimeId;
        await client.SecondMutation.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, client.Mutations.Count);
        var replacement = client.Mutations[1];
        Assert.Equal(secondRuntimeId, replacement.RuntimeInstanceId);
        Assert.Equal(1, replacement.Revision);
        Assert.Equal(client.Mutations[0].Folders, replacement.Folders);
    }

    private sealed class OwnerClientFactory(OwnerClient client) : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
            => typeof(TClient) == typeof(IRuntimeDevPackageOwnerClient)
                ? (TClient)(object)client
                : throw new InvalidOperationException($"Unexpected client type {typeof(TClient).Name}.");
    }

    private sealed class OwnerClient(Guid runtimeId) : IRuntimeDevPackageOwnerClient
    {
        private Guid _runtimeId = runtimeId;
        public List<DevPackageOwnerMutationRequest> Mutations { get; } = [];
        public DevPackageOwnerMutationRequest? Mutation => Mutations.LastOrDefault();
        public string? OwnerId { get; private set; }
        public DevPackageOwnerReleaseRequest? Release { get; private set; }
        public bool FailFirstMutation { get; init; }
        public TimeSpan LeaseLifetime { get; init; } = TimeSpan.FromMinutes(1);
        public TaskCompletionSource SecondMutation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                new RuntimeProductVersionDiagnostics("Sunder.Runtime.Host", "1.1.0", "test")));

        public Task<DevPackageOwnerLeaseResponse> ReplaceDevPackageOwnerAsync(
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
            if (FailFirstMutation && Mutations.Count == 1)
            {
                throw new HttpRequestException("response lost");
            }
            return Task.FromResult(new DevPackageOwnerLeaseResponse(
                RuntimeId,
                ownerId,
                request.MutationId,
                request.Revision,
                DateTimeOffset.UtcNow.Add(LeaseLifetime),
                request.Revision,
                []));
        }

        public Task<DevPackageOwnerLeaseResponse> HeartbeatDevPackageOwnerAsync(
            string ownerId,
            DevPackageOwnerHeartbeatRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DevPackageOwnerLeaseResponse(
                RuntimeId,
                ownerId,
                Mutation!.MutationId,
                Mutation.Revision,
                DateTimeOffset.UtcNow.Add(LeaseLifetime),
                Mutation.Revision,
                []));

        public Task ReleaseDevPackageOwnerAsync(
            string ownerId,
            DevPackageOwnerReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            Release = request;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
