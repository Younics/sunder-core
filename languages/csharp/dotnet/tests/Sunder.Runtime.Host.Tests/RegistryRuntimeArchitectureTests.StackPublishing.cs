using System.Net;
using System.Net.Http.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RegistryRuntimeArchitectureTests
{
    [Theory]
    [InlineData("new")]
    [InlineData("existing")]
    [InlineData("reservation-denied")]
    [InlineData("reservation-conflict")]
    [InlineData("policy-denied")]
    [InlineData("policy-unauthorized")]
    [InlineData("publish-denied")]
    [InlineData("lost-response")]
    public async Task StackPublishingReservesExactArchiveIdentityBeforeFreshPolicyAndNeverReplays(string scenario)
    {
        var root = CreateTempDirectory();
        try
        {
            const string id = "test.stack";
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var archive = Path.Combine(root, "input.sunderstack");
            await SunderStackArchiveWriter.WriteAsync(new SunderStackManifest
            {
                SchemaVersion = SunderStackFormat.CurrentSchemaVersion, MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
                StackId = id, Name = "Test Stack", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
                Packages = [new() { PackageId = "example.package", InstallTag = "latest", Required = true }], Fragments = [],
            }, archive);
            var paths = new RuntimePackagePaths(root);
            var store = new RegistryCredentialStore(paths);
            await store.SetAsync(origin, new("credential", "user-1", DateTimeOffset.UtcNow.AddHours(1), null, null, null, null, false));
            using var transfers = new RuntimeContentTransferStore(paths);
            await using var stream = File.OpenRead(archive);
            var upload = await transfers.CreateUploadAsync(RuntimeUploadKind.Stack, stream, stream.Length, null,
                "not-the-stack-identity.sunderstack", "application/octet-stream", 1, CancellationToken.None);
            var calls = new List<string>();
            var revision = 42;
            var posts = 0;
            var handler = new DelegateHandler(request =>
            {
                Assert.Equal("credential", request.Headers.Authorization?.Parameter);
                var path = request.RequestUri!.AbsolutePath;
                calls.Add(request.Method + " " + path);
                if (path == RegistryApiRoutes.StackPolicy(id))
                    return scenario == "existing" ? Json(new RegistryStackPolicySnapshot(id, "user-1", new(id, 1, new string('a', 64)),
                        "Listed", null, new(revision, new string('b', 64)))) : new(HttpStatusCode.NotFound);
                if (path == RegistryApiRoutes.CurrentUserStackReservation(id))
                {
                    Assert.Equal(HttpMethod.Put, request.Method);
                    if (scenario == "reservation-denied") return Denied("The identity is protected.");
                    if (scenario == "reservation-conflict") return new(HttpStatusCode.Conflict)
                    { Content = JsonContent.Create(new RegistryIdentityReservationOperationResponse(false, null, "conflict", ["The identity already has an active owner lease."])) };
                    revision++;
                    return Json(new RegistryIdentityReservationOperationResponse(true,
                        new("Stack", id, Guid.NewGuid(), 1, "user-1", "Reserved", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), null, null), null, []));
                }
                if (path == RegistryApiRoutes.PublicationAuthorityPolicy)
                {
                    Assert.Equal(scenario == "existing" ? 1 : 2, calls.Count - 1);
                    if (scenario == "policy-denied") return Denied("Current Registry credentials are required.");
                    if (scenario == "policy-unauthorized") return new(HttpStatusCode.Unauthorized);
                    return Json(new RegistryPolicyIdentity(revision, new string('b', 64)));
                }
                Assert.Equal(RegistryApiRoutes.StackPublish, path);
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(revision.ToString(), Assert.Single(request.Headers.GetValues(RegistryPolicyMutationHeaders.PublicationAuthorityRevision)));
                Assert.Matches("^[0-9a-f]{32}$", Assert.Single(request.Headers.GetValues(RegistryPolicyMutationHeaders.IdempotencyKey)));
                posts++;
                if (scenario == "publish-denied") return Denied("The current Stack owner or authorized maintainer is required.");
                if (scenario == "lost-response") throw new HttpRequestException("Simulated response loss after dispatch.");
                return Json(new RegistryPublishStackResponse(true, id, "Published.", [], []));
            });
            var operations = new RegistryAuthenticatedOperations(new FakeHttpClientFactory(handler), store, transfers, () => 1);
            Task<RegistryPublishStackResponse> Publish() => operations.PublishStackAsync(new RuntimeRegistryPublishRequest(origin.AbsoluteUri, upload.UploadId), CancellationToken.None);
            if (scenario == "lost-response") await Assert.ThrowsAsync<HttpRequestException>(Publish);
            else
            {
                var result = await Publish();
                Assert.Equal(scenario is "new" or "existing", result.Success);
                if (scenario.EndsWith("denied", StringComparison.Ordinal)) Assert.True(result.Forbidden);
                if (scenario == "reservation-conflict") Assert.Contains("active owner lease", Assert.Single(result.Errors), StringComparison.Ordinal);
                if (scenario == "policy-denied") Assert.Contains("publication policy", Assert.Single(result.Errors), StringComparison.Ordinal);
                if (scenario == "publish-denied") Assert.Contains("authorized maintainer", Assert.Single(result.Errors), StringComparison.Ordinal);
            }
            Assert.Equal(scenario is "new" or "existing" or "publish-denied" or "lost-response" ? 1 : 0, posts);
            Assert.Equal(scenario == "policy-unauthorized", await store.GetAsync(origin) is null);
            Assert.Empty(Directory.GetFileSystemEntries(paths.TransferRootPath));
        }
        finally { Directory.Delete(root, recursive: true); }

        static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        static HttpResponseMessage Denied(string detail) => new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { title = "Forbidden", detail }) };
    }
}
