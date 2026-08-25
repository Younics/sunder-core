using System.Text;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeRpcCallAcquisitionPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EndpointAndProviderGeneration_ExposeExactAcquisitionFailures()
    {
        Assert.True(RuntimeRpcCallAcquisitionPolicy.ValidateCallerGeneration(isCurrent: true).Succeeded);
        AssertFailure(
            RuntimeRpcCallAcquisitionPolicy.ValidateCallerGeneration(isCurrent: false).Failure,
            SunderRpcErrorKind.Unavailable,
            "rpc.caller-retired",
            "The caller package activation is retired.");
        Assert.True(RuntimeRpcCallAcquisitionPolicy.EvaluateEndpoint(found: true, stale: true).Succeeded);

        var stale = RuntimeRpcCallAcquisitionPolicy.EvaluateEndpoint(found: false, stale: true);
        AssertFailure(
            stale.Failure,
            SunderRpcErrorKind.StaleEndpoint,
            "rpc.endpoint.stale",
            "The RPC endpoint identifies a retired provider activation.");

        var missing = RuntimeRpcCallAcquisitionPolicy.EvaluateEndpoint(found: false, stale: false);
        AssertFailure(
            missing.Failure,
            SunderRpcErrorKind.NotFound,
            "rpc.endpoint.not-found",
            "The RPC endpoint was not found.");

        var activationId = Guid.NewGuid();
        var provider = ProviderSnapshot(activationId, sessionGeneration: 7);
        Assert.True(RuntimeRpcCallAcquisitionPolicy.ValidateProviderGeneration(
            activationId,
            provider,
            sessionGeneration: 7).Succeeded);
        foreach (var retired in new[]
                 {
                     RuntimeRpcCallAcquisitionPolicy.ValidateProviderGeneration(
                         loadedPackageActivationId: null,
                         provider,
                         sessionGeneration: 7),
                     RuntimeRpcCallAcquisitionPolicy.ValidateProviderGeneration(
                         Guid.NewGuid(),
                         provider,
                         sessionGeneration: 7),
                     RuntimeRpcCallAcquisitionPolicy.ValidateProviderGeneration(
                         activationId,
                         provider,
                         sessionGeneration: 8),
                 })
        {
            AssertFailure(
                retired.Failure,
                SunderRpcErrorKind.Unavailable,
                "rpc.provider-retired",
                "The provider activation is no longer in the active Runtime session.");
        }
    }

    [Fact]
    public void CallerAction_ResolvesContractBeforeSelectingAuthoritativePermissionSource()
    {
        var contract = Contract();
        var provider = ProviderSnapshot(Guid.NewGuid(), sessionGeneration: 7, contract);
        var publicRegistration = Registration(contract, SunderPackageFormat.PublicProviderAudience);
        var installedCaller = Caller(contract, PackageSourceKind.Installed);

        var persisted = RuntimeRpcCallAcquisitionPolicy.EvaluateCallerAction(
            installedCaller,
            provider,
            publicRegistration,
            SunderRpcProtocol.InvokeAction,
            requirePermission: true);

        Assert.True(persisted.Succeeded);
        Assert.Same(contract, persisted.Contract);
        var persistedPermission = Assert.IsType<RuntimeRpcCallPermissionRequirement>(persisted.Permission);
        Assert.Equal(RuntimeRpcCallPermissionAcquisitionKind.PermissionStore, persistedPermission.Kind);
        Assert.Equal(
            new RuntimeRpcPermissionKey(
                "caller.package",
                "1.0.0",
                new string('a', 64),
                SunderPackageFormat.RuntimeHostRole,
                contract.ContractId,
                contract.Version,
                contract.AbiSha256!,
                SunderRpcProtocol.InvokeAction),
            persistedPermission.Key);

        var host = RuntimeRpcCallAcquisitionPolicy.EvaluateCallerAction(
            installedCaller,
            provider,
            publicRegistration,
            SunderRpcProtocol.InvokeAction,
            requirePermission: false);
        Assert.Equal(RuntimeRpcCallPermissionAcquisitionKind.None, host.Permission!.Kind);

        var development = RuntimeRpcCallAcquisitionPolicy.EvaluateCallerAction(
            Caller(contract, PackageSourceKind.Dev),
            provider,
            publicRegistration,
            SunderRpcProtocol.InvokeAction,
            requirePermission: true);
        Assert.Equal(RuntimeRpcCallPermissionAcquisitionKind.CallerLifetime, development.Permission!.Kind);

        var ownerProvider = provider with { PackageId = "owner.package" };
        var owner = RuntimeRpcCallAcquisitionPolicy.EvaluateCallerAction(
            Caller(contract, PackageSourceKind.Installed, "owner.package"),
            ownerProvider,
            Registration(contract, SunderPackageFormat.OwnerProviderAudience),
            SunderRpcProtocol.InvokeAction,
            requirePermission: true);
        Assert.Equal(RuntimeRpcCallPermissionAcquisitionKind.CallerLifetime, owner.Permission!.Kind);

        var incompatible = RuntimeRpcCallAcquisitionPolicy.EvaluateCallerAction(
            Caller(contract, PackageSourceKind.Installed, actions: [SunderRpcProtocol.DiscoverAction]),
            provider,
            publicRegistration,
            SunderRpcProtocol.InvokeAction,
            requirePermission: true);
        Assert.Null(incompatible.Contract);
        Assert.Null(incompatible.Permission);
        AssertPermissionFailure(incompatible.Failure, contract.ContractId, SunderRpcProtocol.InvokeAction);

        var denied = RuntimeRpcCallAcquisitionPolicy.ValidatePermissionAcquisition(
            persistedPermission,
            acquired: false);
        AssertPermissionFailure(denied.Failure, contract.ContractId, SunderRpcProtocol.InvokeAction);
    }

    [Theory]
    [InlineData((int)RuntimeRpcCallScopeRegistry.Outcome.CallerLimitReached, SunderRpcErrorKind.ResourceExhausted, "rpc.scope.limit")]
    [InlineData((int)RuntimeRpcCallScopeRegistry.Outcome.AllocationFailed, SunderRpcErrorKind.Unavailable, "rpc.scope.allocation")]
    [InlineData((int)RuntimeRpcCallScopeRegistry.Outcome.Unavailable, SunderRpcErrorKind.Unavailable, "rpc.scope.unavailable")]
    [InlineData((int)RuntimeRpcCallScopeRegistry.Outcome.DeadlineExceeded, SunderRpcErrorKind.DeadlineExceeded, "rpc.call.deadline-exceeded")]
    public void ScopeRegistration_MapsBoundedRegistryOutcomes(
        int outcomeValue,
        SunderRpcErrorKind expectedKind,
        string expectedCode)
    {
        var outcome = (RuntimeRpcCallScopeRegistry.Outcome)outcomeValue;
        var result = RuntimeRpcCallAcquisitionPolicy.EvaluateScopeRegistration(outcome, scope: null);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedKind, result.Failure!.Kind);
        Assert.Equal(expectedCode, result.Failure.Code);
    }

    [Fact]
    public void ScopeValidation_PreservesDeadlineBeforeUnavailableMapping()
    {
        var lookupDeadline = RuntimeRpcCallAcquisitionPolicy.EvaluateScopeLookup(
            RuntimeRpcCallScopeRegistry.Outcome.DeadlineExceeded,
            scope: null);
        Assert.Equal("rpc.call.deadline-exceeded", lookupDeadline.Failure!.Code);
        var missing = RuntimeRpcCallAcquisitionPolicy.EvaluateScopeLookup(
            RuntimeRpcCallScopeRegistry.Outcome.NotFound,
            scope: null);
        Assert.Equal("rpc.scope.unavailable", missing.Failure!.Code);

        var deadline = RuntimeRpcCallAcquisitionPolicy.ValidateScope(
            RuntimeRpcCallScopeRegistry.Outcome.DeadlineExceeded);
        Assert.Equal("rpc.call.deadline-exceeded", deadline.Failure!.Code);

        var unavailable = RuntimeRpcCallAcquisitionPolicy.ValidateScope(
            RuntimeRpcCallScopeRegistry.Outcome.CallerMismatch);
        Assert.Equal("rpc.scope.unavailable", unavailable.Failure!.Code);
        Assert.True(RuntimeRpcCallAcquisitionPolicy.ValidateScope(
            RuntimeRpcCallScopeRegistry.Outcome.Success).Succeeded);
    }

    [Fact]
    public void FrameAcquisition_RejectsDepthBeforeReadingClockThenAppliesDeadline()
    {
        var policy = new RuntimeRpcPolicyOptions
        {
            DefaultDeadline = TimeSpan.FromMinutes(5),
            MaximumExtendedDeadline = TimeSpan.FromMinutes(35),
            MaxNestedCallDepth = 1,
        };
        var clock = new CountingTimeProvider(Now);
        var depth = RuntimeRpcCallAcquisitionPolicy.AcquireFrame(
            policy,
            new SunderRpcCallOptions(Now),
            new RuntimeRpcCallFrame(1, Now),
            scope: null,
            clock,
            targetSupportsExtendedDeadline: true);

        Assert.Equal("rpc.call.depth-limit", depth.Failure!.Code);
        Assert.Equal(0, clock.UtcNowReads);

        var deadline = RuntimeRpcCallAcquisitionPolicy.AcquireFrame(
            policy,
            new SunderRpcCallOptions(Now),
            currentFrame: null,
            scope: null,
            clock,
            targetSupportsExtendedDeadline: true);
        Assert.Equal("rpc.call.deadline-exceeded", deadline.Failure!.Code);
        Assert.Equal(Now, deadline.AcquiredAtUtc);
        Assert.Equal(1, clock.UtcNowReads);

        var success = RuntimeRpcCallAcquisitionPolicy.AcquireFrame(
            policy,
            new SunderRpcCallOptions(Now.AddMinutes(30)),
            currentFrame: null,
            scope: null,
            clock,
            targetSupportsExtendedDeadline: true);
        Assert.True(success.Succeeded);
        Assert.Equal(1, success.Frame!.Depth);
        Assert.Equal(Now.AddMinutes(30), success.Frame.DeadlineUtc);
        Assert.Equal(2, clock.UtcNowReads);
    }

    [Theory]
    [InlineData((int)RuntimeRpcCallLeaseKind.Caller, "rpc.caller.concurrency-limit")]
    [InlineData((int)RuntimeRpcCallLeaseKind.Provider, "rpc.provider.concurrency-limit")]
    public void LeaseAcquisition_ReportsExactBoundedAuthority(
        int kindValue,
        string expectedCode)
    {
        var kind = (RuntimeRpcCallLeaseKind)kindValue;
        Assert.True(RuntimeRpcCallAcquisitionPolicy.ValidateLeaseAcquisition(kind, acquired: true).Succeeded);
        var rejected = RuntimeRpcCallAcquisitionPolicy.ValidateLeaseAcquisition(kind, acquired: false);
        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, rejected.Failure!.Kind);
        Assert.Equal(expectedCode, rejected.Failure.Code);
    }

    private static void AssertPermissionFailure(
        RuntimeRpcCallAcquisitionFailure? failure,
        string contractId,
        string action)
    {
        AssertFailure(
            failure,
            SunderRpcErrorKind.PermissionDenied,
            "rpc.permission.denied",
            $"RPC action '{action}' is not granted for contract '{contractId}'.");
        Assert.Equal(contractId, failure!.ContractId);
        Assert.Equal(action, failure.Action);
    }

    private static void AssertFailure(
        RuntimeRpcCallAcquisitionFailure? failure,
        SunderRpcErrorKind kind,
        string code,
        string message)
    {
        var actual = Assert.IsType<RuntimeRpcCallAcquisitionFailure>(failure);
        Assert.Equal(kind, actual.Kind);
        Assert.Equal(code, actual.Code);
        Assert.Equal(message, actual.Message);
    }

    private static TestCaller Caller(
        SunderRpcContractDescriptor contract,
        PackageSourceKind sourceKind,
        string packageId = "caller.package",
        IReadOnlyList<string>? actions = null)
        => new(
            packageId,
            sourceKind,
            new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal)
            {
                [PackageSessionPreparer.ContractKey(contract.ContractId, contract.Version)] = contract,
            },
            [
                new SunderPackageContractUseManifest
                {
                    Role = SunderPackageFormat.RuntimeHostRole,
                    ContractId = contract.ContractId,
                    ContractVersion = contract.Version,
                    AbiSha256 = contract.AbiSha256,
                    Required = true,
                    Actions = (actions ?? [SunderRpcProtocol.InvokeAction]).Cast<string?>().ToArray(),
                },
            ]);

    private static RuntimeRpcProviderRegistration Registration(
        SunderRpcContractDescriptor contract,
        string audience)
    {
        var manifest = new SunderPackageProviderManifest
        {
            ProviderId = "example.provider",
            ContractId = contract.ContractId,
            ContractVersion = contract.Version,
            DescriptorSha256 = contract.Sha256,
            AbiSha256 = contract.AbiSha256,
            Role = SunderPackageFormat.RuntimeHostRole,
            Audience = audience,
        };
        return new RuntimeRpcProviderRegistration(
            manifest.ProviderId!,
            contract.ContractId,
            contract.Version,
            contract.Sha256,
            contract,
            TestHandler.Instance,
            manifest);
    }

    private static SunderRpcProviderSnapshot ProviderSnapshot(
        Guid activationId,
        long sessionGeneration,
        SunderRpcContractDescriptor? contract = null)
    {
        contract ??= Contract();
        return new SunderRpcProviderSnapshot(
            "provider.package",
            "1.0.0",
            "example.provider",
            contract.ContractId,
            contract.Version,
            contract.Sha256,
            contract.AbiSha256,
            activationId,
            1,
            sessionGeneration,
            new SunderRpcEndpointReference("rpc1_policy"),
            1,
            SunderRpcProviderState.Active);
    }

    private static SunderRpcContractDescriptor Contract()
        => SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "descriptorVersion":2,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.rpc",
              "version":"1.0.0",
              "errors":{},
              "services":{"messages":{"methods":{
                "send":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}}
              }}},
              "$defs":{
                "Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128}},"required":["message"],"additionalProperties":false},
                "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}
              }
            }
            """));

    private sealed class CountingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public int UtcNowReads { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            UtcNowReads++;
            return now;
        }
    }

    private sealed class TestCaller(
        string packageId,
        PackageSourceKind sourceKind,
        IReadOnlyDictionary<string, SunderRpcContractDescriptor> contracts,
        IReadOnlyList<SunderPackageContractUseManifest> uses) : IRuntimeRpcCallerActivation
    {
        public string PackageId { get; } = packageId;
        public string PackageVersion => "1.0.0";
        public Guid? ActivationId { get; } = Guid.NewGuid();
        public string Role => SunderPackageFormat.RuntimeHostRole;
        public string ManifestSha256 { get; } = new string('a', 64);
        public PackageSourceKind? SourceKind { get; } = sourceKind;
        public IReadOnlyDictionary<string, SunderRpcContractDescriptor> RpcContracts { get; } = contracts;
        public IReadOnlyList<SunderPackageContractUseManifest> RpcContractUses { get; } = uses;
        public CancellationToken RetirementToken => CancellationToken.None;

        public bool IsCurrent(PackageSessionLease sessionLease) => true;

        public bool TryAcquireCaller(int maximumCalls, out RuntimeRpcActivationLease? lease)
        {
            lease = new RuntimeRpcActivationLease(CancellationToken.None, static () => { });
            return true;
        }
    }

    private sealed class TestHandler : ISunderRpcServiceHandler
    {
        public static TestHandler Instance { get; } = new();

        public ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken = default)
            => ThrowAsync();

        private static async IAsyncEnumerable<JsonElement> ThrowAsync()
        {
            await Task.Yield();
            throw new NotSupportedException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
