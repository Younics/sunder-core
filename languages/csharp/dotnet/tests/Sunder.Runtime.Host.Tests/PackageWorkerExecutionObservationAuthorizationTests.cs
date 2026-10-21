using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageWorkerExecutionObservationAuthorizationTests
{
    [Fact]
    public async Task QueryUsesCurrentAppPackageWithoutRequiringAnyProviderAndSuccessorCanObserve()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.OpenAsync();
        Assert.Equal(Fixture.PackageId, await fixture.ReadOwnerAsync(first.SessionId));
        fixture.AppSessions.Close(first.SessionId);
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.ReadOwnerAsync(first.SessionId));
        Assert.Equal("rpc.app-session.retired", retired.Error.Code);
        var successor = await fixture.OpenAsync();
        Assert.NotEqual(first.SessionId, successor.SessionId);
        Assert.Equal(Fixture.PackageId, await fixture.ReadOwnerAsync(successor.SessionId));
    }

    [Fact]
    public async Task ClosedCallScopeCannotBorrowCurrentCallerObservationAuthority()
    {
        await using var fixture = await Fixture.CreateAsync();
        var app = await fixture.OpenAsync();
        var scope = fixture.Broker.CreateAppCallScope(app.SessionId, null, default);
        await fixture.Broker.CloseAppCallScopeAsync(app.SessionId, scope.Id);
        var entered = false;
        await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Broker.WithAppExecutionObservationAsync(
            app.SessionId, scope.Id, null, (owner, _, _) => { entered = true; return Task.FromResult(owner); }, default));
        Assert.False(entered);
    }

    [Fact]
    public async Task AppRetirementDuringReadRejectsLateResultInsteadOfPublishingAnotherOwnersAuthority()
    {
        await using var fixture = await Fixture.CreateAsync();
        var app = await fixture.OpenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = fixture.Broker.WithAppExecutionObservationAsync(app.SessionId, null, null, async (owner, _, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.SetResult();
            await release.Task;
            return owner;
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            fixture.AppSessions.Close(app.SessionId);
            // Close invalidates currentness before asynchronous cancellation
            // propagation necessarily finishes. This case asserts the token
            // fence specifically, so witness that fence before releasing the read.
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Theory]
    [InlineData("packageId")]
    [InlineData("packageVersion")]
    [InlineData("providerId")]
    [InlineData("contractId")]
    [InlineData("contractVersion")]
    [InlineData("descriptorSha256")]
    [InlineData("abiSha256")]
    [InlineData("activationId")]
    [InlineData("activationEpoch")]
    [InlineData("sessionGeneration")]
    [InlineData("endpoint")]
    public async Task CaptureChecksEveryLocatorFieldBeforeReadingAnyWorkerReservation(string field)
    {
        await using var fixture = await Fixture.CreateAsync(provider: true);
        var app = await fixture.OpenAsync();
        var actual = fixture.Provider!;
        var request = field switch
        {
            "packageId" => actual with { PackageId = "another.package" },
            "packageVersion" => actual with { PackageVersion = "2.0.0" },
            "providerId" => actual with { ProviderId = "another.provider" },
            "contractId" => actual with { ContractId = "another.contract" },
            "contractVersion" => actual with { ContractVersion = "2.0.0" },
            "descriptorSha256" => actual with { DescriptorSha256 = new('b', 64) },
            "abiSha256" => actual with { AbiSha256 = new('b', 64) },
            "activationId" => actual with { ActivationId = Guid.NewGuid() },
            "activationEpoch" => actual with { ActivationEpoch = actual.ActivationEpoch + 1 },
            "sessionGeneration" => actual with { SessionGeneration = actual.SessionGeneration + 1 },
            _ => actual with { EndpointReference = "rpc1_not_the_selected_endpoint" },
        };
        var invoked = false;
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Broker.WithAppExecutionObservationAsync(
            app.SessionId, null, request, (owner, _, _) => { invoked = true; return Task.FromResult(owner); }, default));
        Assert.Equal(field == "endpoint" ? SunderRpcErrorKind.NotFound : SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
        Assert.False(invoked);
    }

    [Fact]
    public async Task DisplayMetadataDoesNotChangeIdentityAndNonProcessWorkerCannotFabricateReservation()
    {
        await using var fixture = await Fixture.CreateAsync(provider: true);
        var app = await fixture.OpenAsync();
        var request = fixture.Provider! with { CatalogRevision = long.MaxValue, State = RuntimeRpcProviderState.Faulted, FaultCode = "caller-invented" };
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Broker.WithAppExecutionObservationAsync(
            app.SessionId, null, request, (owner, _, _) => Task.FromResult(owner), default));
        Assert.Equal("rpc.execution.unsupported-worker", error.Error.Code);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string PackageId = "execution.observation.owner";
        private readonly string root = Path.Combine(Path.GetTempPath(), "sunder-execution-observation-auth", Guid.NewGuid().ToString("N"));
        private readonly RuntimeRpcPermissionStore permissions;
        private readonly RuntimeSessionOwner owner;
        private readonly RuntimePackageSource source;
        internal RuntimeRpcAppSessionManager AppSessions { get; }
        internal RuntimeRpcBroker Broker { get; }
        internal RuntimeRpcCatalog Catalog { get; } = new();
        internal RuntimeRpcProviderDescriptor? Provider { get; private set; }

        private Fixture()
        {
            Directory.CreateDirectory(root);
            var policy = new RuntimeRpcPolicyOptions();
            owner = new(NullLogger<RuntimeSessionOwner>.Instance, new RuntimeEventStreamService(), rpcCatalog: Catalog);
            permissions = new(new RuntimePackagePaths(root));
            var target = new SunderPackageTargetManifest
            {
                Role = "app", Rid = RuntimeInformation.RuntimeIdentifier, Kind = "web", EntryPoint = "index.html",
                SdkVersion = "1.1.0", RequiredHostCapabilities = ["sdk-baseline-1-1.v1", "rpc.v1"], Views = [],
            };
            var manifest = new SunderPackageManifest
            {
                ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
                ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
                Id = PackageId, Name = PackageId, Version = "1.0.0", Targets = [target], ContractBundles = [], UsesContracts = [], Provides = [],
            };
            source = new(PackageId, PackageSourceKind.Dev, root, Manifest: manifest,
                ContentIndex: new(SunderPackageFormat.CurrentContentIndexVersion,
                    [new(SunderPackageFormat.ManifestPath, new('a', 64), 1)]));
            AppSessions = new(owner.State, policy)
            { LoadContracts = (_, _) => Task.FromResult<IReadOnlyDictionary<string, SunderRpcContractDescriptor>>(new Dictionary<string, SunderRpcContractDescriptor>()) };
            Broker = new(Catalog, permissions, owner.State, owner, policy, TimeProvider.System, CancellationToken.None, AppSessions);
        }

        internal static async Task<Fixture> CreateAsync(bool provider = false)
        {
            var fixture = new Fixture();
            var descriptor = new ActivePackageDescriptor(PackageId, PackageId, "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime,
                null, true, PackageReadinessState.Ready, []);
            var loaded = new ActiveLoadedPackage(descriptor, fixture.source, null, null!, null!, null!, null!, new TestRuntimePackageWorker());
            if (provider)
            {
                var contract = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
                    {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"execution.test","version":"1.0.0",
                     "errors":{},"services":{"test":{"methods":{"read":{"kind":"unary","idempotency":"idempotent","errors":[],
                     "requestSchema":{"$ref":"#/$defs/Value"},"responseSchema":{"$ref":"#/$defs/Value"}}}}},"$defs":{"Value":{"type":"boolean"}}}
                    """));
                var declaration = new SunderPackageProviderManifest { ProviderId = "execution.test.provider", ContractId = contract.ContractId,
                    ContractVersion = contract.Version, DescriptorSha256 = contract.Sha256, AbiSha256 = contract.AbiSha256,
                    Role = "runtime", Audience = "owner" };
                loaded = loaded with { RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>
                { [declaration.ProviderId!] = new(declaration.ProviderId!, contract.ContractId, contract.Version, contract.Sha256, contract, new NeverInvokedHandler(), declaration) } };
            }
            var session = new ActivePackageSession(null, new Dictionary<string, ActiveLoadedPackage> { [PackageId] = loaded },
                new Dictionary<string, SessionPackageDescriptor> { [PackageId] = new(PackageId, PackageId, "1.0.0", descriptor.HostRoles,
                    null, true, PackageReadinessState.Ready, [], null, null, null, 0) });
            await fixture.owner.PublishAsync(session, fixture.owner.Sources.Snapshot(), [], [], fixture.owner.Generation);
            fixture.Catalog.ActivateSession(session, fixture.owner.Generation);
            if (provider) fixture.Provider = RuntimeRpcContractMapper.ToProtocol(Assert.Single(fixture.Catalog.GetSnapshot().Providers));
            return fixture;
        }

        internal Task<RuntimeRpcAppSessionDescriptor> OpenAsync()
            => AppSessions.OpenAsync(new(PackageId, "1.0.0", new('a', 64),
                PackageTargetSelection.ToDescriptor(source.Manifest!.Targets!.Single()!), owner.Generation, Guid.NewGuid()), default);
        internal Task<string> ReadOwnerAsync(string sessionId)
            => Broker.WithAppExecutionObservationAsync(sessionId, null, null, (package, reservation, _) =>
            { Assert.Null(reservation); return Task.FromResult(package); }, default);
        public async ValueTask DisposeAsync()
        {
            AppSessions.Dispose();
            await owner.State.ClearActiveSessionAsync();
            permissions.Dispose();
            Catalog.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NeverInvokedHandler : ISunderRpcServiceHandler
    {
        public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId,
            JsonElement request, CancellationToken token = default) => throw new InvalidOperationException("Observation must not invoke a provider.");
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId,
            JsonElement request, CancellationToken token = default) => throw new InvalidOperationException("Observation must not invoke a provider.");
    }
}
