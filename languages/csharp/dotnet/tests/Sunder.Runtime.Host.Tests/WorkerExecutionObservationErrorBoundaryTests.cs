using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class WorkerExecutionObservationErrorBoundaryTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("direct-denial")]
    [InlineData("io-denial")]
    [InlineData("indeterminate-denial")]
    [InlineData("nested-denial")]
    [InlineData("native-1")]
    [InlineData("native-5")]
    [InlineData("native-13")]
    [InlineData("native-other")]
    [InlineData("ordinary-io")]
    [InlineData("indeterminate-io")]
    [InlineData("corrupt")]
    [InlineData("corrupt-with-denial")]
    [InlineData("invalid-data")]
    [InlineData("invalid-json")]
    public async Task FaultTranslationKeepsDenialAndIntegrityOutOfAvailabilityRecovery(string fault)
    {
        var kind = SunderRpcErrorKind.PermissionDenied;
        Exception failure;
        switch (fault)
        {
            case "direct-denial": failure = new UnauthorizedAccessException(); break;
            case "io-denial": failure = new IOException("IO wrapper", new UnauthorizedAccessException()); break;
            case "indeterminate-denial": failure = new DurableWriteIndeterminateException("unused", new UnauthorizedAccessException()); break;
            case "nested-denial": failure = new DurableWriteIndeterminateException("unused",
                new IOException("IO wrapper", new IOException("Nested IO wrapper", new UnauthorizedAccessException()))); break;
            case "native-1":
            case "native-5":
            case "native-13":
            case "native-other":
                var code = fault switch { "native-1" => 1, "native-5" => 5, "native-13" => 13, _ => 28 };
                var denied = OperatingSystem.IsWindows() ? code == 5 : code is 1 or 13;
                kind = denied ? SunderRpcErrorKind.PermissionDenied : SunderRpcErrorKind.Unavailable;
                failure = new DurableWriteIndeterminateException("unused", new IOException("Native IO wrapper", new Win32Exception(code)));
                break;
            case "ordinary-io": kind = SunderRpcErrorKind.Unavailable; failure = new IOException(); break;
            case "indeterminate-io": kind = SunderRpcErrorKind.Unavailable;
                failure = new DurableWriteIndeterminateException("unused", new IOException()); break;
            case "invalid-data": kind = SunderRpcErrorKind.Protocol; failure = new InvalidDataException(); break;
            case "invalid-json": kind = SunderRpcErrorKind.Protocol; failure = new JsonException(); break;
            default: kind = SunderRpcErrorKind.Protocol;
                failure = new ExecutionRuntimeObservationCorruptException("Invalid evidence",
                    fault == "corrupt-with-denial" ? new UnauthorizedAccessException() : null); break;
        }
        var result = await Assert.ThrowsAsync<SunderRpcException>(() => PackageWorkerExecutionObservationService.TranslateAsync(
            () => Task.FromException<string>(failure)));
        Assert.Equal(kind, result.Error.Kind);
        Assert.Equal(kind switch
        {
            SunderRpcErrorKind.PermissionDenied => "rpc.execution.access-denied",
            SunderRpcErrorKind.Protocol => "rpc.execution.invalid-evidence",
            _ => "rpc.execution.unavailable",
        }, result.Error.Code);
    }

    [Fact]
    public async Task ExistingRpcAvailabilityIsNotBlanketRecodedByObservationFacade()
    {
        var expected = SunderRpcException.Infrastructure(SunderRpcErrorKind.Unavailable, "rpc.app-session.retired", "Retired.");
        var actual = await Assert.ThrowsAsync<SunderRpcException>(() => PackageWorkerExecutionObservationService.TranslateAsync(
            () => Task.FromException<string>(expected)));
        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData("missing-session")]
    [InlineData("null-session")]
    [InlineData("empty-session")]
    [InlineData("blank-session")]
    public async Task RawCaptureRejectsInvalidSessionBeforeBrokerOrUninitializedStore(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var wire = JsonSerializer.SerializeToNode(new RuntimeRpcAppExecutionCaptureRequest(fixture.App.SessionId, Provider()), WireJson)!.AsObject();
        if (mutation == "missing-session") wire.Remove("sessionId");
        else wire["sessionId"] = mutation switch { "null-session" => null, "empty-session" => "", _ => " " };
        var request = wire.Deserialize<RuntimeRpcAppExecutionCaptureRequest>(WireJson)!;
        var result = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Service.CaptureAsync(request, default));
        Assert.Equal(SunderRpcErrorKind.Validation, result.Error.Kind);
        Assert.Equal("rpc.execution.invalid-request", result.Error.Code);
        fixture.AssertNoAuthorityOrStoreEntry();
    }

    [Theory]
    [InlineData("missing-session")]
    [InlineData("null-session")]
    [InlineData("empty-session")]
    [InlineData("blank-session")]
    [InlineData("null-reference")]
    [InlineData("missing-host")]
    [InlineData("empty-host")]
    [InlineData("missing-value")]
    [InlineData("null-value")]
    [InlineData("empty-value")]
    [InlineData("blank-value")]
    [InlineData("excessive-value")]
    public async Task RawObserveRejectsMalformedPrimitivesBeforeBrokerOrUninitializedStore(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var wire = JsonSerializer.SerializeToNode(new RuntimeRpcAppExecutionObserveRequest(fixture.App.SessionId,
            new(Guid.NewGuid(), "bounded-legacy-reference")), WireJson)!.AsObject();
        var reference = wire["reference"]!.AsObject();
        switch (mutation)
        {
            case "missing-session": wire.Remove("sessionId"); break;
            case "null-session": wire["sessionId"] = null; break;
            case "empty-session": wire["sessionId"] = ""; break;
            case "blank-session": wire["sessionId"] = " "; break;
            case "null-reference": wire["reference"] = null; break;
            case "missing-host": reference.Remove("runtimeInstanceId"); break;
            case "empty-host": reference["runtimeInstanceId"] = Guid.Empty.ToString("D"); break;
            case "missing-value": reference.Remove("value"); break;
            case "null-value": reference["value"] = null; break;
            case "empty-value": reference["value"] = ""; break;
            case "blank-value": reference["value"] = " "; break;
            default: reference["value"] = new string('x', 257); break;
        }
        var request = wire.Deserialize<RuntimeRpcAppExecutionObserveRequest>(WireJson)!;
        var result = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Service.ObserveAsync(request, default));
        Assert.Equal(SunderRpcErrorKind.Validation, result.Error.Kind);
        Assert.Equal("rpc.execution.invalid-request", result.Error.Code);
        fixture.AssertNoAuthorityOrStoreEntry();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    public async Task BoundedLegacyReferenceIsAdmittedAndUninitializedStoreStillReportsAvailability(int length)
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Service.ObserveAsync(
            new(fixture.App.SessionId, new(Guid.NewGuid(), new string('x', length))), default));
        Assert.Equal(SunderRpcErrorKind.Unavailable, result.Error.Kind);
        Assert.Equal("rpc.execution.unavailable", result.Error.Code);
        Assert.True(fixture.AuthorityEntries > 0);
        Assert.Equal(0, fixture.GarbageCollection.Entries);
        Assert.False(Directory.Exists(fixture.Layout.ExecutionObservationRootPath));
        // Existing owning-store coverage retains Unknown for legacy/missing/wrong-nonce evidence.
    }

    [Theory]
    [InlineData("blocked", SunderRpcErrorKind.PermissionDenied, PackagePolicyErrorCodes.SecurityBlocked)]
    [InlineData("revoked", SunderRpcErrorKind.PermissionDenied, PackagePolicyErrorCodes.ContractAuthorityRevoked)]
    [InlineData("expired", SunderRpcErrorKind.Unavailable, PackagePolicyErrorCodes.PolicyExpired)]
    public async Task CurrentAppWithoutProviderRechecksLearnedPolicyBeforeObservationDelegate(
        string decision, SunderRpcErrorKind expected, string code)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(ActivePolicyTestContext.Package.PackageId, await fixture.ReadOwnerAsync());
        Assert.Equal(1, fixture.MetadataEntries);
        fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(1));
        if (decision == "blocked")
        {
            fixture.Policy.Response = request => ActivePolicyTestContext.Allowed(request, 2) with
            { Packages = [new(ActivePolicyTestContext.Package, "SecurityBlocked", "Listed", "Denied", code, null, [])] };
            await fixture.Policy.RefreshAsync();
        }
        else if (decision == "revoked")
        {
            fixture.Policy.Response = request => ActivePolicyTestContext.Allowed(request, 2) with
            { PlatformCatalogs = [new(ActivePolicyTestContext.Platform, "Revoked", "Denied", code, ActivePolicyTestContext.ObservedAt)] };
            await fixture.Policy.RefreshAsync([], [ActivePolicyTestContext.Platform]);
        }
        else fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(RuntimePolicyTiming.LeaseSeconds + 1));
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.ReadOwnerAsync());
        Assert.Equal(expected, error.Error.Kind);
        Assert.Equal(code, error.Error.Code);
        Assert.Equal(1, fixture.MetadataEntries);
        Assert.Equal(0, fixture.GarbageCollection.Entries);
        Assert.False(Directory.Exists(fixture.Layout.ExecutionObservationRootPath));
    }

    private static RuntimeRpcProviderDescriptor Provider()
        => new("example.package", "1.0.0", "example.provider", "example.contract", "1.0.0",
            new('a', 64), new('b', 64), Guid.NewGuid(), 2, 3, "rpc1_exact", 4, RuntimeRpcProviderState.Active, null);

    private sealed class Fixture : IAsyncDisposable
    {
        internal ActivePolicyTestContext Policy { get; } = new();
        internal RuntimeRpcCatalog Catalog { get; } = new();
        internal NoGarbageCollection GarbageCollection { get; } = new();
        internal ExecutionRuntimeStoreLayout Layout { get; }
        internal PackageWorkerExecutionObservationService Service { get; }
        internal RuntimeRpcAppSessionDescriptor App { get; private set; } = null!;
        internal int AuthorityEntries;
        internal int MetadataEntries;
        private readonly RuntimeSessionOwner owner;
        private readonly RuntimeRpcPermissionStore permissions;
        private readonly RuntimeRpcAppSessionManager apps;
        private readonly RuntimeRpcBroker broker;
        private readonly RuntimePackageSource source;
        private readonly SunderPackageTargetManifest target;

        private Fixture()
        {
            var package = ActivePolicyTestContext.Package;
            owner = new(NullLogger<RuntimeSessionOwner>.Instance, new RuntimeEventStreamService(), rpcCatalog: Catalog);
            permissions = new(Policy.Paths);
            target = new() { Role = "app", Rid = PackageTargetSelection.GetCurrentRuntimeIdentifier(), Kind = "web", EntryPoint = "index.html",
                SdkVersion = "1.1.0", RequiredHostCapabilities = ["sdk-baseline-1-1.v1", "rpc.v1"], Views = [] };
            source = new(package.PackageId, PackageSourceKind.Installed, Policy.Paths.RootPath, HostRoles: PackageHostRoles.App,
                ContentIdentity: package.PackageContentIdentity,
                Manifest: new() { ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
                    ManifestVersion = SunderPackageFormat.CurrentManifestVersion, Id = package.PackageId, Name = package.PackageId,
                    Version = package.Version, Targets = [target], DependsOn = [], ContractBundles = [], UsesContracts = [], Provides = [] },
                ContentIndex: new(SunderPackageFormat.CurrentContentIndexVersion, [new(SunderPackageFormat.ManifestPath, new('a', 64), 1)]))
            { RegistryPolicyOrigin = ActivePolicyTestContext.Origin, RegistryPolicyContentIdentity = package.PackageContentIdentity };
            apps = new(owner.State, new());
            broker = new(Catalog, permissions, owner.State, owner, appSessions: apps, packagePolicy: new(Policy.Cache));
            Layout = new(Policy.Paths);
            Service = new(broker, new ExecutionRuntimeStore(Layout, GarbageCollection)); // Deliberately never initialized.
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Policy.RefreshAsync(platforms: [ActivePolicyTestContext.Platform]);
            var source = fixture.source;
            var session = new ActivePackageSession(null, new Dictionary<string, ActiveLoadedPackage>(),
                new Dictionary<string, SessionPackageDescriptor> { [source.PackageId] = new(source.PackageId, source.PackageId, source.Manifest!.Version!,
                    PackageHostRoles.App, null, true, PackageReadinessState.Ready, [], null, null, null, 0) }, readySources: [source]);
            await fixture.owner.PublishAsync(session, fixture.owner.Sources.Snapshot(), [], [], fixture.owner.Generation);
            fixture.Catalog.ActivateSession(session, fixture.owner.Generation);
            fixture.App = await fixture.apps.OpenAsync(new(source.PackageId, source.Manifest!.Version!, new('a', 64),
                PackageTargetSelection.ToDescriptor(fixture.target), fixture.owner.Generation, Guid.NewGuid()), default);
            // This existing clock delegate proves invalid primitives never enter the
            // real broker authority gate, without adding a production IO/test abstraction.
            fixture.Policy.Clock.ReadOverride = () => { fixture.AuthorityEntries++; return fixture.Policy.Clock.Value; };
            return fixture;
        }

        internal Task<string> ReadOwnerAsync() => broker.WithAppExecutionObservationAsync(App.SessionId, null, null,
            (package, reservation, _) => { Assert.Null(reservation); MetadataEntries++; return Task.FromResult(package); }, default);
        internal void AssertNoAuthorityOrStoreEntry()
        {
            Assert.Equal(0, AuthorityEntries);
            Assert.Equal(0, MetadataEntries);
            Assert.Equal(0, GarbageCollection.Entries);
            Assert.False(Directory.Exists(Layout.ExecutionObservationRootPath));
        }
        public async ValueTask DisposeAsync()
        {
            apps.Dispose();
            await owner.State.ClearActiveSessionAsync();
            permissions.Dispose();
            Catalog.Dispose();
            Policy.Dispose();
        }
    }

    private sealed class NoGarbageCollection : IExecutionRuntimeGarbageCollectionAuthority
    {
        internal int Entries;
        public Task<TResult> ExecuteAsync<TResult>(Func<ExecutionRuntimeGarbageCollectionAuthoritySnapshot, CancellationToken, Task<TResult>> operation,
            CancellationToken token) { Entries++; throw new InvalidOperationException("Observation must not initialize the store."); }
    }
}
