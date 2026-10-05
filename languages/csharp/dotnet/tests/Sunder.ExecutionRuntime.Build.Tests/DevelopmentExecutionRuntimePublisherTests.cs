using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.ExecutionRuntime.Build.Tests;

public sealed class DevelopmentExecutionRuntimePublisherTests
{
    [Fact]
    public async Task PublishRelease_WhenExactReleaseIsSupported_PerformsLookupOnly()
    {
        var fixture = CreatePublicationFixture();
        var handler = new QueuedHttpHandler(
            _ => JsonResponse(HttpStatusCode.OK, StageResponse(
                fixture.Release,
                ExecutionRuntimeLifecycleStates.Supported,
                publishedRids: SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers,
                missingRids: [],
                readyToPromote: true)));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5288/") };

        var result = await DevelopmentExecutionRuntimeOperations.PublishReleaseAsync(
            client,
            fixture.Release,
            fixture.Expectations,
            fixture.Artifacts,
            CancellationToken.None);

        Assert.True(result.Success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup, request.Path);
        var lookup = JsonSerializer.Deserialize<RegistryLookupExecutionRuntimePublicationRequest>(
            request.Body!,
            JsonOptions);
        Assert.Equivalent(
            new RegistryLookupExecutionRuntimePublicationRequest(
                fixture.Release,
                fixture.Expectations),
            lookup);
    }

    [Fact]
    public async Task PublishRelease_WhenCreateRaces_RecoversExactStageAndPublishesOnlyMissingRids()
    {
        var fixture = CreatePublicationFixture();
        var rids = SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers.Order(StringComparer.Ordinal).ToArray();
        var responses = new List<Func<CapturedRequest, HttpResponseMessage>>
        {
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict),
            _ => JsonResponse(HttpStatusCode.OK, StageResponse(
                fixture.Release,
                ExecutionRuntimeLifecycleStates.Staged,
                publishedRids: [],
                missingRids: rids,
                readyToPromote: false)),
        };
        for (var index = 0; index < rids.Length; index++)
        {
            var published = rids[..(index + 1)];
            var missing = rids[(index + 1)..];
            responses.Add(_ => JsonResponse(HttpStatusCode.OK, StageResponse(
                fixture.Release,
                ExecutionRuntimeLifecycleStates.Staged,
                published,
                missing,
                readyToPromote: missing.Length == 0)));
        }
        responses.Add(_ => JsonResponse(HttpStatusCode.OK, StageResponse(
            fixture.Release,
            ExecutionRuntimeLifecycleStates.Staged,
            rids,
            missingRids: [],
            readyToPromote: true)));
        responses.Add(_ => JsonResponse(
            HttpStatusCode.OK,
            new RegistryExecutionRuntimeLifecycleOperationResponse(
                true,
                null,
                RuntimePolicyRevision: 7,
                Errors: [])));
        var handler = new QueuedHttpHandler(responses.ToArray());
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5288/") };

        var result = await DevelopmentExecutionRuntimeOperations.PublishReleaseAsync(
            client,
            fixture.Release,
            fixture.Expectations,
            fixture.Artifacts,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(7, result.RuntimePolicyRevision);
        Assert.Equal(11, handler.Requests.Count);
        Assert.Equal(
            [
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
                RegistryApiRoutes.DevelopmentExecutionRuntimeStages,
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
            ],
            handler.Requests.Take(3).Select(static request => request.Path));
        var stagedRelease = JsonSerializer.Deserialize<RegistryStageExecutionRuntimeReleaseRequest>(
            handler.Requests[1].Body!,
            JsonOptions);
        Assert.Equivalent(fixture.Release, stagedRelease);
        Assert.Equal(fixture.Release.ExpectedReleaseSigningKeyId, stagedRelease!.ExpectedReleaseSigningKeyId);
        Assert.Equal(
            fixture.Release.ExpectedNativeAttestations,
            stagedRelease.ExpectedNativeAttestations);

        var uploadRequests = handler.Requests.Skip(3).Take(rids.Length).ToArray();
        Assert.Equal(
            rids.Select(rid => RegistryApiRoutes.DevelopmentExecutionRuntimeStageArtifactLocal(StageId, rid)),
            uploadRequests.Select(static request => request.Path));
        foreach (var request in uploadRequests)
        {
            var publication = JsonSerializer.Deserialize<RegistryPublishLocalExecutionRuntimeArtifactRequest>(
                request.Body!,
                JsonOptions);
            Assert.NotNull(publication);
            var rid = request.Path.Split('/')[^2];
            var artifact = fixture.Artifacts.Single(candidate => candidate.Rid == rid);
            Assert.Equal(artifact.Path, publication.RuntimePath);
            Assert.Equal(artifact.IdempotencyKey, publication.IdempotencyKey);
            Assert.Equal(artifact.ArtifactSha256, publication.ExpectedArtifactSha256);
            Assert.Equal(artifact.ArtifactSize, publication.ExpectedArtifactSize);
        }
        Assert.Equal(
            RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
            handler.Requests[^2].Path);
        Assert.Equal(
            RegistryApiRoutes.DevelopmentExecutionRuntimeStagePromotion(StageId),
            handler.Requests[^1].Path);
    }

    [Fact]
    public async Task PublishRelease_WhenArtifactUploadRacesWithPromotion_ReconcilesSupportedRelease()
    {
        var fixture = CreatePublicationFixture();
        var rids = SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers.Order(StringComparer.Ordinal).ToArray();
        var handler = new QueuedHttpHandler(
            _ => JsonResponse(HttpStatusCode.OK, StageResponse(
                fixture.Release,
                ExecutionRuntimeLifecycleStates.Staged,
                publishedRids: [],
                missingRids: rids,
                readyToPromote: false)),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict),
            _ => JsonResponse(HttpStatusCode.OK, StageResponse(
                fixture.Release,
                ExecutionRuntimeLifecycleStates.Supported,
                publishedRids: rids,
                missingRids: [],
                readyToPromote: true)));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5288/") };

        var result = await DevelopmentExecutionRuntimeOperations.PublishReleaseAsync(
            client,
            fixture.Release,
            fixture.Expectations,
            fixture.Artifacts,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            [
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageArtifactLocal(StageId, rids[0]),
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
            ],
            handler.Requests.Select(static request => request.Path));
    }

    [Fact]
    public async Task PublishRelease_WhenPromotionRaces_ReconcilesSupportedRelease()
    {
        var fixture = CreatePublicationFixture();
        var rids = SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers.Order(StringComparer.Ordinal).ToArray();
        var completeStage = StageResponse(
            fixture.Release,
            ExecutionRuntimeLifecycleStates.Staged,
            publishedRids: rids,
            missingRids: [],
            readyToPromote: true);
        var handler = new QueuedHttpHandler(
            _ => JsonResponse(HttpStatusCode.OK, completeStage),
            _ => JsonResponse(HttpStatusCode.OK, completeStage),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict),
            _ => JsonResponse(HttpStatusCode.OK, StageResponse(
                fixture.Release,
                ExecutionRuntimeLifecycleStates.Supported,
                publishedRids: rids,
                missingRids: [],
                readyToPromote: true)));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5288/") };

        var result = await DevelopmentExecutionRuntimeOperations.PublishReleaseAsync(
            client,
            fixture.Release,
            fixture.Expectations,
            fixture.Artifacts,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            [
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
                RegistryApiRoutes.DevelopmentExecutionRuntimeStagePromotion(StageId),
                RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup,
            ],
            handler.Requests.Select(static request => request.Path));
    }

    [Fact]
    public async Task PublishRelease_RejectsPartialLocalReleaseBeforeNetworkMutation()
    {
        var fixture = CreatePublicationFixture();
        var handler = new QueuedHttpHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5288/") };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevelopmentExecutionRuntimeOperations.PublishReleaseAsync(
                client,
                fixture.Release,
                fixture.Expectations,
                fixture.Artifacts.Skip(1).ToArray(),
                CancellationToken.None));

        Assert.Contains("one exact local artifact", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PublishRelease_BoundsRegistryErrorDiagnostics()
    {
        var fixture = CreatePublicationFixture();
        var handler = new QueuedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new ByteArrayContent(new byte[(64 * 1024) + 1]),
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5288/") };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevelopmentExecutionRuntimeOperations.PublishReleaseAsync(
                client,
                fixture.Release,
                fixture.Expectations,
                fixture.Artifacts,
                CancellationToken.None));

        Assert.Contains("exceeded 65536 bytes", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    private static PublicationFixture CreatePublicationFixture()
    {
        const string releaseKeyId = "sunder.runtime.development.release.0123456789abcdef01234567";
        const string attestationKeyId = "sunder.runtime.development.native-attestation.89abcdef0123456789abcdef";
        var rids = SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers.Order(StringComparer.Ordinal).ToArray();
        var release = new RegistryStageExecutionRuntimeReleaseRequest(
            SunderExecutionRuntimeFormat.DotNetRuntimeId,
            "10.0.1",
            2,
            SunderExecutionRuntimeFormat.DotNetModuleAbi,
            SunderExecutionRuntimeFormat.WorkerProtocol,
            3,
            releaseKeyId,
            rids.Select(rid => new RegistryExecutionRuntimeNativeAttestationExpectation(
                rid,
                attestationKeyId)).ToArray(),
            SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers);
        var expectations = rids.Select((rid, index) =>
            new RegistryExecutionRuntimePublicationArtifactExpectation(
                rid,
                $"development-v1:{new string((char)('a' + index), 64)}",
                new string((char)('a' + index), 64),
                index + 100,
                new string((char)('1' + index), 64),
                new string((char)('7' - index), 64),
                releaseKeyId,
                new string((char)('f' - index), 64),
                attestationKeyId)).ToArray();
        var artifacts = expectations.Select(expectation =>
            new DevelopmentExecutionRuntimeOperations.DevelopmentExecutionRuntimeLocalArtifact(
                expectation.Rid,
                Path.Combine(Path.GetTempPath(), expectation.Rid + ".sunderruntime"),
                expectation.IdempotencyKey,
                expectation.ArtifactSha256,
                expectation.ArtifactSize)).ToArray();
        return new PublicationFixture(release, expectations, artifacts);
    }

    private static RegistryStagedExecutionRuntimePublicationResponse StageResponse(
        RegistryStageExecutionRuntimeReleaseRequest release,
        string lifecycleState,
        IReadOnlyList<string> publishedRids,
        IReadOnlyList<string> missingRids,
        bool readyToPromote)
        => new(
            StageId,
            new RegistryExecutionRuntimeReleaseSummary(
                release.RuntimeId,
                release.Version,
                release.PackagingRevision,
                release.ModuleAbi,
                release.WorkerProtocol,
                release.MinimumHostTrustGeneration,
                new RegistryExecutionRuntimeLifecycle(
                    lifecycleState,
                    DateTimeOffset.UnixEpoch,
                    lifecycleState == ExecutionRuntimeLifecycleStates.Supported
                        ? DateTimeOffset.UnixEpoch
                        : null,
                    null,
                    null,
                    null,
                    null,
                    null),
                SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers,
                DateTimeOffset.UnixEpoch),
            release.ExpectedReleaseSigningKeyId,
            release.ExpectedNativeAttestations,
            SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers,
            publishedRids,
            missingRids,
            readyToPromote,
            []);

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body)
        => new(status) { Content = JsonContent.Create(body, options: JsonOptions) };

    private const string StageId = "00112233445566778899aabbccddeeff";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record PublicationFixture(
        RegistryStageExecutionRuntimeReleaseRequest Release,
        IReadOnlyList<RegistryExecutionRuntimePublicationArtifactExpectation> Expectations,
        IReadOnlyList<DevelopmentExecutionRuntimeOperations.DevelopmentExecutionRuntimeLocalArtifact> Artifacts);

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);

    private sealed class QueuedHttpHandler(params Func<CapturedRequest, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<CapturedRequest, HttpResponseMessage>> _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(captured);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("The test publisher made an unexpected HTTP request.");
            }
            return _responses.Dequeue()(captured);
        }
    }
}
