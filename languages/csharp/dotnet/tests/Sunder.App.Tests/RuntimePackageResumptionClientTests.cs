using System.Net;
using System.Net.Http.Json;
using Sunder.App.Services;
using Sunder.App.Tests.TestSupport;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

/// <summary>App queue + real shared HTTP client/serialization/authentication; Host domain semantics have their own tests.</summary>
public sealed class RuntimePackageResumptionClientTests
{
    [Fact]
    public async Task RetainedQueuedRuntimeAndPackageStageRoundTripSameProofThroughSharedClient()
    {
        await using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var upload = new ContentUploadDescriptor("upload", RuntimeManagementTestClient.Hash, 81, "demo.sunderpkg", "application/vnd.sunder.package");
        var handle = new RuntimePackageUploadRetentionHandle(upload.UploadId, "opaque-lease", upload.ContentHash, upload.Length);
        var available = PackageUploadRetentionTestClient.Available(handle);
        var runtimeReady = false;
        var previews = new List<RuntimePackageRuntimePrerequisiteRequest>();
        var releases = 0;
        var staged = false;
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            switch (request.RequestUri!.AbsolutePath)
            {
                case RuntimeApiRoutes.PackageUploadRetention:
                    Assert.Equal(new(upload.UploadId, upload.ContentHash, upload.Length), await request.Content!.ReadFromJsonAsync<RuntimePackageUploadRetentionRequest>());
                    return Json(available);
                case RuntimeApiRoutes.PackageUploadRetentionRenew:
                case RuntimeApiRoutes.PackageUploadRetentionStatus:
                    Assert.Equal(handle, await request.Content!.ReadFromJsonAsync<RuntimePackageUploadRetentionHandle>());
                    return Json(available);
                case RuntimeApiRoutes.PackageUploadRetentionRelease:
                    Assert.Equal(handle, await request.Content!.ReadFromJsonAsync<RuntimePackageUploadRetentionHandle>());
                    releases++;
                    return Json(new RuntimePackageUploadRetentionResult(true, null, "Consumed/released", null));
                case RuntimeApiRoutes.PackageRuntimePrerequisites:
                    var source = (await request.Content!.ReadFromJsonAsync<RuntimePackageRuntimePrerequisiteRequest>())!;
                    Assert.Equal(handle, Assert.Single(source.Local!.Mutations).UploadRetention);
                    Assert.Null(source.Registry);
                    Assert.Null(source.Adoption);
                    Assert.False(staged);
                    previews.Add(source);
                    return Json(RuntimeManagementTestClient.Prerequisites(runtimeReady));
                case RuntimeApiRoutes.ExecutionRuntimeInstallApply:
                    Assert.False(staged);
                    var package = Assert.Single(queue.ListProcesses(), item => item.Metadata["sunder.installationPhase"] == "package");
                    Assert.Equal(BackgroundProcessState.Queued, package.State);
                    Assert.Null(package.StartedAtUtc);
                    var confirmation = (await request.Content!.ReadFromJsonAsync<RuntimeExecutionRuntimePlanConfirmation>())!;
                    Assert.Equal("install", confirmation.PlanId);
                    Assert.Equal(new string('b', 64), confirmation.ExpectedFingerprint);
                    runtimeReady = true;
                    return Json(new RuntimeExecutionRuntimeOperationResult(true, null, "Ready", "install", null, false));
                case "/api/v1/packages/store/stage":
                    Assert.True(runtimeReady);
                    var stage = (await request.Content!.ReadFromJsonAsync<PackageStoreStageRequest>())!;
                    Assert.Equal(handle, Assert.Single(stage.Mutations).UploadRetention);
                    Assert.Equal(new(RuntimeManagementTestClient.Hash, RuntimeManagementTestClient.Identity.Rid), stage.RuntimePrerequisiteExpectation);
                    staged = true;
                    return Json(new PackageStoreStageResult("stage", new(true, "Staged", false, false, [], []), []));
                default: throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }
        });
        using var client = Client(handler);
        await using var retention = await PackageUploadRetentionSession.AcquireAsync(client, upload, CancellationToken.None);
        Assert.True((await client.RenewPackageUploadRetentionAsync(retention.Handle)).Success);
        var request = new PackageStoreStageRequest([new(PackageStoreMutationKind.Install, UploadId: upload.UploadId) { UploadRetention = retention.Handle }]);
        var source = new RuntimePackageRuntimePrerequisiteRequest(Local: request);
        var reviewed = await client.PreviewPackageRuntimePrerequisitesAsync(source, retention.Token);
        RegistryPackageInstallService.ValidateRuntimePreview(reviewed);
        var approval = new PackageInstallationApproval(reviewed);
        Assert.True(approval.NeedsRuntimeInstallation);
        Assert.Empty(queue.ListProcesses());

        async Task<RuntimePackageRuntimePrerequisitePlan> Refresh(CancellationToken token)
        {
            await retention.CheckAvailableAsync(token);
            return await client.PreviewPackageRuntimePrerequisitesAsync(source with { ExpectedSourceFingerprint = reviewed.SourceFingerprint }, token);
        }
        var installation = PackageInstallationCohort.Enqueue(queue, "Install Demo",
            new PackageOperationMetadata("demo", PackageOperationKind.InstallLocal, "Demo").ToMetadata(),
            context => approval.InstallRuntimesAsync(client, Refresh, context), async context =>
            {
                var current = await Refresh(context.CancellationToken);
                approval.Validate(current, requireReady: true);
                await retention.PrepareForConsumptionAsync(context.CancellationToken);
                var stage = request with { RuntimePrerequisiteExpectation = new(current.SourceFingerprint, current.Rid) };
                Assert.True((await client.StagePackageStoreChangesAsync(stage, context.CancellationToken)).Success);
            }, retention.Token, retention.DisposeAsync);
        var completed = await installation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed.Success, completed.Failure?.Message);
        Assert.True(staged);
        Assert.Equal(1, releases);
        Assert.Equal(4, previews.Count);
        Assert.All(previews, preview => Assert.Equal(handle, preview.Local!.Mutations[0].UploadRetention));
        Assert.Null(previews[0].ExpectedSourceFingerprint);
        Assert.All(previews.Skip(1), preview => Assert.Equal(RuntimeManagementTestClient.Hash, preview.ExpectedSourceFingerprint));
        Assert.All(queue.ListProcesses(), item => Assert.Equal(BackgroundProcessState.Completed, item.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoptionAdapterUsesTypedSourceAndCarriesBothExpectationsOverHttp(bool sourceRaced)
    {
        var fingerprint = new string('f', 64);
        RuntimePackageRuntimePrerequisiteRequest? source = null;
        var adoptionCalls = new List<RuntimeRegistrySourceAdoptionRequest>();
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == RuntimeApiRoutes.ExecutionRuntimes)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Json<IReadOnlyList<InstalledExecutionRuntimeSummary>>([]);
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            if (request.RequestUri!.AbsolutePath == RuntimeApiRoutes.PackageRuntimePrerequisites)
            {
                source = await request.Content!.ReadFromJsonAsync<RuntimePackageRuntimePrerequisiteRequest>();
                Assert.Null(source!.Registry);
                Assert.Null(source.Local);
                Assert.Equal("demo", source.Adoption!.PackageId);
                Assert.Equal("latest", source.Adoption.Tag);
                Assert.Equal(RuntimeManagementTestClient.Origin, source.Adoption.RegistryOrigin);
                Assert.False(source.Adoption.Confirm);
                return Json(RuntimeManagementTestClient.Prerequisites(true));
            }
            Assert.Equal("/api/v1/registry/packages/adopt-source", request.RequestUri.AbsolutePath);
            var adoption = (await request.Content!.ReadFromJsonAsync<RuntimeRegistrySourceAdoptionRequest>())!;
            adoptionCalls.Add(adoption);
            if (adoption.DryRun)
                return Json(new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "Preview", false, false, [], [], [], [])
                { PlanExpectations = [new(RuntimeManagementTestClient.Origin, false, fingerprint)] });
            Assert.NotNull(source);
            Assert.Equal(new(RuntimeManagementTestClient.Hash, RuntimeManagementTestClient.Identity.Rid), adoption.RuntimePrerequisiteExpectation);
            Assert.True(adoption.Confirm);
            Assert.Equal(fingerprint, adoption.ExpectedResolutionFingerprint);
            return Json(sourceRaced
                ? new RuntimeRegistryPackageChangeResult(false, RuntimeRegistryErrorCode.StalePlan, "The installed source changed; review again.", false, false, [], ["Source changed"], [], [])
                : new RuntimeRegistryPackageChangeResult(true, RuntimeRegistryErrorCode.None, "Adopted", true, false, [], [], [], []));
        });
        using var client = Client(handler);
        using var service = new RegistryPackageInstallService((_, _) => Task.FromResult(true));
        var result = await service.AdoptPackageSourceAsync("demo", new(RuntimeManagementTestClient.Origin), client);
        Assert.Equal(!sourceRaced, result.Success);
        Assert.Equal(3, adoptionCalls.Count);
        Assert.All(adoptionCalls.Take(2), preview => { Assert.True(preview.DryRun); Assert.False(preview.Confirm); });
        Assert.False(adoptionCalls[^1].DryRun);
        Assert.NotNull(adoptionCalls[^1].RuntimePrerequisiteExpectation);
    }

    [Theory]
    [InlineData("retain")]
    [InlineData("adoption-preview")]
    [InlineData("adoption-apply")]
    [InlineData("retained-stage")]
    public async Task OldHostCannotSilentlyIgnoreResumptionProof(string operation)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("No mutation may reach an older Host."), resumption: false);
        using var client = Client(handler);
        await Assert.ThrowsAsync<RuntimeProtocolException>(async () =>
        {
            switch (operation)
            {
                case "retain": await client.RetainPackageUploadAsync(new("upload", RuntimeManagementTestClient.Hash, 81)); break;
                case "adoption-preview": await client.PreviewPackageRuntimePrerequisitesAsync(new() { Adoption = new("demo", RuntimeManagementTestClient.Origin) }); break;
                case "adoption-apply": await client.AdoptRegistryPackageSourceAsync(new("demo", RuntimeManagementTestClient.Origin)
                    { RuntimePrerequisiteExpectation = new(RuntimeManagementTestClient.Hash, RuntimeManagementTestClient.Identity.Rid) }); break;
                default: await client.StagePackageStoreChangesAsync(new([new(PackageStoreMutationKind.Install, UploadId: "upload")
                    { UploadRetention = new("upload", "lease", RuntimeManagementTestClient.Hash, 81) }])); break;
            }
        });
        Assert.Equal(["/api/handshake"], handler.Paths);
    }

    private static RuntimeApiClient Client(Handler handler)
        => new(() => new RuntimeConnectionInfo(new("http://127.0.0.1:5275/"), "test-token"), handler);

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, bool resumption = true) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath != "/api/handshake") return respond(request);
            var features = new List<string> { RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.AtomicPackageSnapshotV1,
                RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1 };
            if (resumption) features.Add(RuntimeProtocolFeatures.PackageInstallResumptionV1);
            return Task.FromResult(Json(new RuntimeHandshakeResponse(RuntimeProtocol.Identity, RuntimeProtocol.CurrentRevision,
                RuntimeProtocol.MinimumSupportedRevision, RuntimeProtocol.MaximumSupportedRevision, Guid.NewGuid(), features,
                new("Sunder.Runtime.Host", "Development", "Development"))));
        }
    }
}
