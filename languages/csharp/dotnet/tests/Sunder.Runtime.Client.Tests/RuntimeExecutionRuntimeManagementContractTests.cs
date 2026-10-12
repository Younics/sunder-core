using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed partial class RuntimeExecutionRuntimeManagementContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Hash = new('a', 64);
    private static readonly RuntimeExecutionRuntimeRequirement Requirement = new(
        "sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1");

    [Fact]
    public void MutationWireRequiresConfirmationAndDefaultsToNoRestart()
    {
        Assert.True(new RuntimeExecutionRuntimeSettings(1).AutomaticUpdatesEnabled);
        Assert.True(new RuntimeExecutionRuntimeInstallPreviewRequest(Requirement).RetainUntilUninstall);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuntimeExecutionRuntimeSettingsUpdateRequest>("{\"expectedRevision\":1}", Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuntimeExecutionRuntimePlanConfirmation>("{\"planId\":\"plan\"}", Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuntimeExecutionRuntimePlanConfirmation>(
            $$"""{"planId":"plan","expectedFingerprint":"{{Hash}}","artifactUrl":"https://untrusted.example/"}""", Json));

        var apply = JsonSerializer.Deserialize<RuntimeExecutionRuntimeUpdateApplyRequest>(
            $$"""{"confirmation":{"planId":"plan","expectedFingerprint":"{{Hash}}"},"expectedImpactFingerprint":"{{Hash}}"}""", Json)!;
        Assert.Equal(RuntimeExecutionRuntimeApplyMode.WhenStopped, apply.Mode);
        Assert.Equal(Hash, apply.Confirmation.ExpectedFingerprint);
        Assert.Contains("\"mode\":\"WhenStopped\"", JsonSerializer.Serialize(apply, Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuntimeExecutionRuntimeUpdateApplyRequest>(
            JsonSerializer.Serialize(apply, Json).Replace("WhenStopped", "GuessIdle", StringComparison.Ordinal), Json));
    }

    [Fact]
    public void PrerequisiteRequestSnapshotsNestedSelectionsAndPreservesResumeFence()
    {
        var targets = new List<RuntimeRegistryPackageTargetRequest> { new("runtime", "osx-arm64") };
        var selection = new RuntimeRegistryPackageChangeRequest("example.package", "1.0.0", targets);
        var expected = new RuntimePackageRuntimePrerequisiteExpectation(Hash, "osx-arm64");
        var registry = new RuntimeRegistryPackageBatchRequest("https://registry.example/", [selection])
        {
            RuntimePrerequisiteExpectation = expected,
        };
        var request = new RuntimePackageRuntimePrerequisiteRequest(Registry: registry, ExpectedSourceFingerprint: Hash);
        var before = JsonSerializer.Serialize(request, Json);
        targets.Clear();

        Assert.Equal(before, JsonSerializer.Serialize(request, Json));
        var copy = JsonSerializer.Deserialize<RuntimePackageRuntimePrerequisiteRequest>(before, Json)!;
        Assert.Equal(expected, copy.Registry!.RuntimePrerequisiteExpectation);
        Assert.Equal("osx-arm64", Assert.Single(Assert.Single(copy.Registry.Packages).DesiredTargets).Rid);
        Assert.Throws<NotSupportedException>(() => ((IList<RuntimeRegistryPackageTargetRequest>)copy.Registry.Packages[0].DesiredTargets).Clear());

        var local = new RuntimePackageRuntimePrerequisiteRequest(Local: new([new(PackageStoreMutationKind.Install, UploadId: "upload")])
        {
            RuntimePrerequisiteExpectation = expected,
        });
        var localCopy = JsonSerializer.Deserialize<RuntimePackageRuntimePrerequisiteRequest>(JsonSerializer.Serialize(local, Json), Json)!;
        Assert.Equal("upload", Assert.Single(localCopy.Local!.Mutations).UploadId);
        Assert.Equal(expected, localCopy.Local.RuntimePrerequisiteExpectation);
    }

    [Fact]
    public void UsageAuthorityAndPendingCollectionsCannotBeMutatedOrSilentlyTruncated()
    {
        var references = new List<RuntimeExecutionRuntimePackageReference> { new("example.package", "1.0.0", Hash, "runtime", "osx-arm64") };
        var running = new List<string> { "example.package" };
        var usage = new RuntimeExecutionRuntimeUsage(Hash, references, running, running, 1, 1, 1, 1, false);
        var before = JsonSerializer.Serialize(usage, Json);
        references.Clear();
        running.Clear();
        Assert.Equal(before, JsonSerializer.Serialize(usage, Json));
        Assert.Equal(before, JsonSerializer.Serialize(JsonSerializer.Deserialize<RuntimeExecutionRuntimeUsage>(before, Json), Json));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)usage.RunningPackageIds).Clear());
        Assert.Throws<ArgumentException>(() => new RuntimeExecutionRuntimeUsage(Hash, [], [null!], [], 0, 0, 0, 0, false));
        Assert.Throws<ArgumentException>(() => new RuntimeExecutionRuntimeUsage(Hash, [],
            Enumerable.Repeat("package", RuntimeExecutionRuntimeManagementLimits.MaximumItems + 1).ToArray(), [], 0, 0, 0, 0, false));
        Assert.Throws<ArgumentException>(() => new RuntimePackageRuntimePrerequisiteRequest(Local: new(
            Enumerable.Repeat(new PackageStoreMutationRequest(PackageStoreMutationKind.Install, UploadId: "upload"),
                RuntimeExecutionRuntimeManagementLimits.MaximumPackageSources + 1).ToArray())));

        var candidates = new List<RuntimeExecutionRuntimeUpdateCandidate>();
        var bindings = new List<RuntimeExecutionRuntimeBindingChange>();
        var pending = new RuntimeExecutionRuntimePreparedUpdate("update", Hash, RuntimeExecutionRuntimeUpdateState.Deferred,
            DateTimeOffset.Parse("2026-10-07T12:00:00Z"), candidates, bindings);
        candidates.Add(null!);
        bindings.Add(null!);
        Assert.Empty(pending.Runtimes);
        Assert.Empty(pending.BindingChanges);
        var copy = JsonSerializer.Deserialize<RuntimeExecutionRuntimePreparedUpdate>(JsonSerializer.Serialize(pending, Json), Json)!;
        Assert.Equal(RuntimeExecutionRuntimeUpdateState.Deferred, copy.State);
        var updates = Enumerable.Repeat(pending, RuntimeExecutionRuntimeManagementLimits.MaximumPreparedUpdates).ToArray();
        var status = new RuntimeExecutionRuntimeUpdateStatus(false, null, null, null, updates);
        updates[0] = pending with { UpdateId = "changed" };
        Assert.Equal("update", status.PreparedUpdates[0].UpdateId);
        Assert.Equal(32, status.PreparedUpdates.Count);
        Assert.Throws<ArgumentException>(() => status with { PreparedUpdates = [.. status.PreparedUpdates, pending] });
        Assert.Throws<ArgumentException>(() => new RuntimeExecutionRuntimeUpdateStatus(false, null, null, null,
            Enumerable.Repeat(pending, RuntimeExecutionRuntimeManagementLimits.MaximumPreparedUpdates + 1).ToArray()));
    }

    [Fact]
    public async Task ClientUsesExactV1RoutesAndTransmitsConsentWithoutImplicitAcquisitionOrApply()
    {
        var seen = new List<(string Method, string Path)>();
        var confirmation = new RuntimeExecutionRuntimePlanConfirmation("preview", Hash);
        var usage = new RuntimeExecutionRuntimeUsage(Hash, [], [], [], 0, 0, 0, 0, false);
        var identity = new RuntimeExecutionRuntimeIdentity(
            ConfiguredOfficialSourceId.DotNet,
            Requirement.RuntimeId,
            "10.0.1",
            "osx-arm64",
            Requirement.ModuleAbi,
            RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
            new string('b', 128));
        var operation = new RuntimeExecutionRuntimeOperationResult(true, null, "Installed.", RuntimeExecutionRuntimeOperationActions.Install, null, false);
        using var handler = new Handler(async request =>
        {
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/handshake") return Reply(Handshake(true));
            seen.Add((request.Method.Method, path));
            if (path == RuntimeApiRoutes.ExecutionRuntimeSettings)
            {
                if (request.Method == HttpMethod.Put)
                    Assert.Equal(new RuntimeExecutionRuntimeSettingsUpdateRequest(1, false), await request.Content!.ReadFromJsonAsync<RuntimeExecutionRuntimeSettingsUpdateRequest>());
                return Reply(new RuntimeExecutionRuntimeSettings(2, false));
            }
            if (path == RuntimeApiRoutes.ExecutionRuntimeCatalog)
            {
                Assert.Equal("?skip=2&take=3&sourceId=dotnet", request.RequestUri.Query);
                return Reply(new RuntimeExecutionRuntimeCatalogPage("osx-arm64", 2, 3, 0, []));
            }
            if (path == RuntimeApiRoutes.ExecutionRuntimeUpdateStatus)
                return Reply(new RuntimeExecutionRuntimeUpdateStatus(false, null, null, null, []));
            if (path == RuntimeApiRoutes.ExecutionRuntimeInstallPreview)
                return Reply(new RuntimeExecutionRuntimeInstallPlan(false, "execution-runtime.missing", "Missing.", null, null, true));
            if (path == RuntimeApiRoutes.PackageRuntimePrerequisites)
            {
                var source = await request.Content!.ReadFromJsonAsync<RuntimePackageRuntimePrerequisiteRequest>();
                Assert.Equal("upload", Assert.Single(source!.Local!.Mutations).UploadId);
                return Reply(new RuntimePackageRuntimePrerequisitePlan(true, false, "execution-runtime.missing", "Install runtime first.", Hash, "osx-arm64", [], []));
            }
            if (path == RuntimeApiRoutes.ExecutionRuntimeUpdatePreview)
                return Reply(new RuntimeExecutionRuntimeUpdatePlan(true, null, "No changes.", null, null, [], [], usage));
            if (path == RuntimeApiRoutes.ExecutionRuntimeUninstallPreview)
                return Reply(new RuntimeExecutionRuntimeUninstallPlan(true, null, "Unused.", null, identity, usage, true));
            if (path == RuntimeApiRoutes.ExecutionRuntimeUpdateApply)
            {
                var apply = await request.Content!.ReadFromJsonAsync<RuntimeExecutionRuntimeUpdateApplyRequest>();
                Assert.Equal(confirmation, apply!.Confirmation);
                Assert.Equal(Hash, apply.ExpectedImpactFingerprint);
                Assert.Equal(RuntimeExecutionRuntimeApplyMode.RestartAffectedPackages, apply.Mode);
                return Reply(new RuntimeExecutionRuntimeUpdateResult(true, null, "Deferred.", RuntimeExecutionRuntimeUpdateState.Deferred, null));
            }
            Assert.Equal(confirmation, await request.Content!.ReadFromJsonAsync<RuntimeExecutionRuntimePlanConfirmation>());
            if (path == RuntimeApiRoutes.ExecutionRuntimeInstallApply) return Reply(operation);
            if (path == RuntimeApiRoutes.ExecutionRuntimeUninstallApply)
                return Reply(new RuntimeExecutionRuntimeUninstallResult(true, null, "Cleanup pending.", identity, true, true));
            Assert.Equal(RuntimeApiRoutes.ExecutionRuntimeUpdatePrepare, path);
            return Reply(new RuntimeExecutionRuntimeUpdateResult(true, null, "Prepared.", RuntimeExecutionRuntimeUpdateState.Prepared, null));
        });
        using var client = new RuntimeManagementClient(() => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler);
        await client.GetExecutionRuntimeSettingsAsync();
        await client.UpdateExecutionRuntimeSettingsAsync(new(1, false));
        await client.GetExecutionRuntimeUpdateStatusAsync();
        await client.GetExecutionRuntimeCatalogAsync(new(ConfiguredOfficialSourceId.DotNet, 2, 3));
        await client.PreviewExecutionRuntimeInstallAsync(new(Requirement));
        await client.ApplyExecutionRuntimeInstallAsync(confirmation);
        await client.PreviewPackageRuntimePrerequisitesAsync(new(Local: new([new(PackageStoreMutationKind.Install, UploadId: "upload")])));
        await client.PreviewExecutionRuntimeUpdateAsync(new());
        await client.PrepareExecutionRuntimeUpdateAsync(confirmation);
        await client.ApplyExecutionRuntimeUpdateAsync(new(confirmation, Hash, RuntimeExecutionRuntimeApplyMode.RestartAffectedPackages));
        await client.PreviewExecutionRuntimeUninstallAsync(new(identity));
        await client.ApplyExecutionRuntimeUninstallAsync(confirmation);

        Assert.Equal(new (string, string)[]
        {
            ("GET", "/api/v1/execution-runtimes/settings"),
            ("PUT", "/api/v1/execution-runtimes/settings"),
            ("GET", "/api/v1/execution-runtimes/updates/status"),
            ("GET", "/api/v1/execution-runtimes/catalog"),
            ("POST", "/api/v1/execution-runtimes/install/preview"),
            ("POST", "/api/v1/execution-runtimes/install/apply"),
            ("POST", "/api/v1/packages/runtime-prerequisites"),
            ("POST", "/api/v1/execution-runtimes/updates/preview"),
            ("POST", "/api/v1/execution-runtimes/updates/prepare"),
            ("POST", "/api/v1/execution-runtimes/updates/apply"),
            ("POST", "/api/v1/execution-runtimes/uninstall/preview"),
            ("POST", "/api/v1/execution-runtimes/uninstall/apply"),
        }, seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficialRuntimeFeatureIsRequiredBeforeBasicOrManagementCalls(bool basicCall)
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            Assert.Equal("/api/handshake", request.RequestUri!.AbsolutePath);
            calls++;
            return Task.FromResult(Reply(Handshake(false)));
        });
        using var client = new RuntimeManagementClient(() => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler);
        if (basicCall) await Assert.ThrowsAsync<RuntimeProtocolException>(() => client.GetSystemStatusAsync());
        else await Assert.ThrowsAsync<RuntimeProtocolException>(() => client.GetExecutionRuntimeSettingsAsync());
        Assert.True(calls > 0);
        Assert.Equal(1, RuntimeProtocol.CurrentRevision);
    }

    private static RuntimeHandshakeResponse Handshake(bool updates) => new(
        RuntimeProtocol.Identity, 1, 1, 1, Guid.NewGuid(),
        updates
            ? [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1]
            : [RuntimeProtocolFeatures.VersionedApiV1, "execution-runtime-management.v1", "execution-runtime-updates.v1"],
        new("Sunder.Runtime.Host", "Development", "Development"));

    private static HttpResponseMessage Reply<T>(T response) => new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
