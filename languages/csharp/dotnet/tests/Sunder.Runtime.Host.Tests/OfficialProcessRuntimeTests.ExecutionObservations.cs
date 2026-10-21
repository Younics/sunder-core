using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class OfficialProcessRuntimeTests
{
    [Fact]
    [Trait("Capability", "Process")]
    public async Task WorkerExecutionObservation_ExactActivatorLeaseIsConfirmedToAppSuccessorWithoutOldProvider()
    {
        var package = CreateExecutionObservationProviderPackage();
        using var apps = new RuntimeRpcAppSessionManager(fixture.Owner.State, new());
        var broker = new RuntimeRpcBroker(fixture.Catalog, fixture.Permissions, fixture.Owner.State, fixture.Owner,
            appSessions: apps, contentStore: fixture.Transfers);
        var observations = new PackageWorkerExecutionObservationService(broker, fixture.Store);
        var errors = new List<string>();
        var result = await new RuntimePackageActivator(NullLogger.Instance, fixture.Paths, broker,
                packageSessionState: fixture.Owner.State, executionRuntimeLaunchResolver: fixture.LaunchResolver)
            .ActivateAsync(package, new List<string>(), errors, default);
        Assert.True(result.Success, string.Join(" | ", errors));
        var loaded = Assert.IsType<ActiveLoadedPackage>(result.LoadedPackage);
        var worker = Assert.IsType<ProcessRuntimeWorker>(loaded.Worker);
        var registration = Assert.Single(loaded.RpcProviders).Value;
        // Count and FORWARD the real activator-created process handler; no replacement
        // Worker, lease, reservation identity or provider result is supplied by this test.
        var dispatches = new ExecutionObservationDispatchCounter(Assert.IsType<ProcessRpcProviderHandler>(registration.Handler));
        loaded = loaded with { RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>
        { [registration.ProviderId] = registration with { Handler = dispatches } } };
        var session = new ActivePackageSession(null,
            new Dictionary<string, ActiveLoadedPackage> { [package.PackageId] = loaded },
            new Dictionary<string, SessionPackageDescriptor> { [package.PackageId] = result.SessionPackage });
        try
        {
            await worker.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Owner.PublishAsync(session, fixture.Owner.Sources.Snapshot(), [], [], fixture.Owner.Generation)
                .WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Catalog.ActivateSession(session, fixture.Owner.Generation);
            var generation = new RuntimePackageGeneration(loaded.RuntimeActivationId, fixture.Owner.Generation);
            await worker.CommitGenerationAsync(generation).WaitAsync(TimeSpan.FromSeconds(10));
            await worker.PrepareStorageMigrationsAsync(generation).WaitAsync(TimeSpan.FromSeconds(10));
            await worker.ActivateGenerationAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var execution = Assert.Single(await fixture.ReadLeasesAsync());
            Assert.True(execution.HasContainment);
            Assert.True(execution.IsAttached);
            Assert.Equal(package.SessionId, execution.GenerationId); // String preparation ID, not numeric RPC generation.
            Assert.Equal(loaded.RuntimeActivationId, execution.ActivationId);
            Assert.Equal(new RuntimeExecutionReservationIdentity(execution.LeaseId, package.PackageId,
                package.SessionId, loaded.RuntimeActivationId), worker.ExecutionReservationIdentity);
            var appTarget = package.Source.Manifest!.Targets!.Single(target => target!.Role == SunderPackageFormat.AppHostRole)!;
            Task<RuntimeRpcAppSessionDescriptor> OpenAppAsync() => apps.OpenAsync(new(package.PackageId, package.Version,
                package.ManifestSha256!, PackageTargetSelection.ToDescriptor(appTarget), fixture.Owner.Generation, Guid.NewGuid()), default);
            var app = await OpenAppAsync();
            var catalog = await broker.DiscoverAppAsync(app.SessionId, registration.ContractId, default);
            var provider = Assert.Single(catalog.Providers);
            var locator = RuntimeRpcContractMapper.ToProtocol(provider);
            Assert.Equal(0, dispatches.Calls);
            var reference = await observations.CaptureAsync(new(app.SessionId, locator), default)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, dispatches.Calls); // Capture is metadata admission BEFORE the sole provider dispatch.
            Assert.Equal(execution.HostInstanceId, reference.RuntimeInstanceId);
            Assert.True(ExecutionRuntimeObservationReferences.TryGetLeaseId(reference, out var capturedLeaseId));
            Assert.Equal(execution.LeaseId, capturedLeaseId);
            var capture = JsonSerializer.Deserialize<ExecutionRuntimeObservationCaptureV1>(
                await File.ReadAllBytesAsync(fixture.Layout.GetExecutionObservationCapturePath(execution.LeaseId)), ExecutionRuntimeStore.JsonOptions)!;
            Assert.Equal(reference, capture.Reference);
            Assert.Equal(execution, capture.Execution);
            var pending = await observations.ObserveAsync(new(app.SessionId, reference), default);
            Assert.Equal(RuntimeWorkerExecutionState.CleanupPending, pending.State);
            Assert.Null(pending.ConfirmedAtUtc);
            var reply = await broker.InvokeAppAsync(app.SessionId, provider.Endpoint, "messages", "send",
                JsonSerializer.SerializeToElement(new { message = "after-capture" }), null, default)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reply.GetProperty("accepted").GetBoolean());
            Assert.Equal(1, dispatches.Calls);

            apps.Close(app.SessionId);
            // Normal session retirement owns stop/dispose and native drain. The successor
            // retains only the authorized App source, with no Runtime worker or provider.
            var successorSession = new ActivePackageSession(null, new Dictionary<string, ActiveLoadedPackage>(),
                new Dictionary<string, SessionPackageDescriptor>
                { [package.PackageId] = result.SessionPackage with { HostRoles = PackageHostRoles.App } },
                readySources: [package.Source with { HostRoles = PackageHostRoles.App }]);
            Assert.Empty(await fixture.Owner.PublishAsync(successorSession, fixture.Owner.Sources.Snapshot(), [], [], fixture.Owner.Generation)
                .WaitAsync(TimeSpan.FromSeconds(10)));
            fixture.Catalog.ActivateSession(successorSession, fixture.Owner.Generation);
            Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
            Assert.Empty(await fixture.ReadLeasesAsync());
            Assert.Equal(ExecutionRuntimeContainmentProbe.Empty,
                new SystemExecutionRuntimeProcessContainmentController(new SystemExecutionRuntimeProcessController()).Probe(execution));
            var cleanup = JsonSerializer.Deserialize<ExecutionRuntimeObservationCleanupV1>(
                await File.ReadAllBytesAsync(fixture.Layout.GetExecutionObservationCleanupPath(execution.LeaseId)), ExecutionRuntimeStore.JsonOptions)!;
            Assert.Equal(reference, cleanup.Reference);
            Assert.Equal(execution, cleanup.Execution);
            Assert.Equal(ExecutionRuntimeObservationCleanupV1.ContainmentEmptyProof, cleanup.ProofKind);

            var successor = await OpenAppAsync();
            Assert.NotEqual(app.SessionId, successor.SessionId);
            var confirmed = await observations.ObserveAsync(new(successor.SessionId, reference), default);
            Assert.Equal(reference, confirmed.Reference);
            Assert.Equal(RuntimeWorkerExecutionState.CleanupConfirmed, confirmed.State);
            Assert.Equal(cleanup.ConfirmedAtUtc, confirmed.ConfirmedAtUtc);
            Assert.Equal(1, dispatches.Calls); // Successor query cannot redispatch the retired provider.
        }
        finally
        {
            apps.Dispose();
            try { await fixture.Owner.State.ClearActiveSessionAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                fixture.Catalog.DeactivateAll(fixture.Owner.Generation);
                await session.DisposeAsync();
            }
        }
    }

    private PreparedRuntimePackage CreateExecutionObservationProviderPackage()
    {
        var package = fixture.CreatePackage(SyntheticRuntimeKind.DotNet, "official.observation.owner", "normal");
        var contract = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"official.observation","version":"1.0.0",
             "errors":{},"services":{"messages":{"methods":{"send":{"kind":"unary","idempotency":"idempotent","errors":[],
             "requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Reply"}}}}},
             "$defs":{"Request":{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false},
             "Reply":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}}}
            """));
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = package.PackageId, Name = package.PackageId, Version = package.Version, DependsOn = [],
            Targets = [package.SelectedTarget!, new SunderPackageTargetManifest
            {
                Role = "app", Rid = package.SelectedTarget!.Rid, Kind = "web", EntryPoint = "index.html",
                SdkVersion = "1.1.0", RequiredHostCapabilities = ["sdk-baseline-1-1.v1", "rpc.v1"], Views = [],
            }],
            ContractBundles = [new SunderPackageContractBundleManifest
            {
                ContractId = contract.ContractId, Version = contract.Version, DescriptorPath = "contracts/observation.json",
                DescriptorSha256 = contract.Sha256, AbiSha256 = contract.AbiSha256, Exposure = "private",
                Source = "owned", AuthorityKind = "package", AuthorityId = package.PackageId,
            }],
            UsesContracts = [new SunderPackageContractUseManifest
            {
                Role = "app", ContractId = contract.ContractId, ContractVersion = contract.Version, AbiSha256 = contract.AbiSha256,
                Actions = [SunderRpcProtocol.DiscoverAction, SunderRpcProtocol.InvokeAction],
            }],
            Provides = [new SunderPackageProviderManifest
            {
                ProviderId = "official.observation.provider", ContractId = contract.ContractId, ContractVersion = contract.Version,
                DescriptorSha256 = contract.Sha256, AbiSha256 = contract.AbiSha256, Role = "runtime", Audience = "owner",
            }],
        };
        var entries = package.Source.ContentIndex!.Files!.ToList();
        void AddSourceFile(string relative, byte[] bytes)
        {
            var path = Path.Combine(package.SourceFolder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            entries.Add(new(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength));
        }
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var contractBytes = contract.GetCanonicalUtf8();
        AddSourceFile(SunderPackageFormat.ManifestPath, manifestBytes);
        AddSourceFile("payload/shared/contracts/observation.json", contractBytes);
        AddSourceFile($"payload/app/{package.SelectedTarget!.Rid}/index.html", Encoding.UTF8.GetBytes("<!doctype html><title>Observation caller</title>"));
        Directory.CreateDirectory(Path.Combine(package.ShadowFolder, "contracts"));
        File.WriteAllBytes(Path.Combine(package.ShadowFolder, "contracts", "observation.json"), contractBytes);
        var content = new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion,
            entries.OrderBy(entry => entry!.Path, StringComparer.Ordinal).ToArray());
        var roles = PackageHostRoles.App | PackageHostRoles.Runtime;
        return package with
        {
            Source = package.Source with { Manifest = manifest, ContentIndex = content, HostRoles = roles,
                ContentIdentity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))) },
            HostRoles = roles,
            Activation = package.Activation with { HostRoles = roles },
            RpcContracts = new Dictionary<string, SunderRpcContractDescriptor>
            { [PackageSessionPreparer.ContractKey(contract.ContractId, contract.Version)] = contract },
            ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
        };
    }

    private sealed class ExecutionObservationDispatchCounter(ISunderRpcServiceHandler inner) : ISunderRpcServiceHandler
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId,
            JsonElement request, CancellationToken token = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.InvokeUnaryAsync(context, serviceId, methodId, request, token);
        }
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId,
            JsonElement request, CancellationToken token = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.InvokeServerStreamAsync(context, serviceId, methodId, request, token);
        }
    }
}
