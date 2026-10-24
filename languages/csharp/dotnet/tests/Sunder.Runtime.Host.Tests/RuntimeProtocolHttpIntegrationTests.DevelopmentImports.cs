using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeProtocolHttpIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevOwnerEndpoint_RejectsAnInvalidCompatibilityGraphWithoutLosingThePreviousLease(bool incompatibleAuthority)
    {
        // App-only inert fixtures exercise the actual prepare/graph/HTTP boundary;
        // no package Worker or external Registry/provider is started.
        using var fixture = new DevelopmentImportAuthorityTests.Fixture { Offline = true };
        await fixture.Preparation.PrepareAsync(await fixture.LocalInputs(), default);
        var folders = new List<DevPackageOwnerFolder> { new(fixture.Folder, false) };
        if (incompatibleAuthority)
        {
            var authority = fixture.Temp.GetPath("local-authority");
            var manifestPath = Path.Combine(authority, SunderPackageFormat.ManifestPath);
            var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!;
            manifest["version"] = "2.0.0";
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            var projectionPath = Path.Combine(authority, SunderPackageFormat.RpcModelProjectionPath);
            var projection = JsonNode.Parse(await File.ReadAllBytesAsync(projectionPath))!.AsObject();
            projection["packageVersion"] = "2.0.0";
            await File.WriteAllBytesAsync(projectionPath, SunderRpcModelProjectionJson.Write(projection));
            CanonicalPackageTestBuilder.WriteContentIndex(authority);
            var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(authority);
            Assert.True(validation.Success, string.Join(" | ", validation.Errors));
            folders.Add(new(authority, false));
        }
        var logs = new RecordingLogSink();
        await using var server = await StartDevelopmentImportsServerAsync(fixture, logs);
        using var client = AuthenticatedDevelopmentImportsClient(server);
        var owner = server.Services.GetRequiredService<RuntimeSessionOwner>();
        owner.MarkReady();
        var instance = server.Services.GetRequiredService<RuntimeProtocolDescriptor>().RuntimeInstanceId;
        const string token = "owner-token-aaaaaaaaaaaaaaaaaaaa";
        var initial = new DevPackageOwnerMutationRequest(instance, token, "initial", 1, []);
        using var accepted = await client.PutAsJsonAsync("api/v1/dev-package-owners/graph-owner", initial);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var prior = await accepted.Content.ReadFromJsonAsync<DevPackageOwnerLeaseResponse>();
        Assert.NotNull(prior);

        using var response = await client.PutAsJsonAsync("api/v1/dev-package-owners/graph-owner",
            new DevPackageOwnerMutationRequest(instance, token, "rejected", 2, folders));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"Expected 422; received {(int)response.StatusCode}: {body}\n"
            + string.Join("\n", logs.Entries.Where(entry => entry.Exception is not null).Select(entry => entry.Exception)));
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(RuntimeErrorCodes.PackageValidation, problem.RootElement.GetProperty("code").GetString());
        Assert.Contains("compatibility graph", problem.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(problem.RootElement.GetProperty("correlationId").GetString(),
            Assert.Single(response.Headers.GetValues(RuntimeProblemDetailsMiddleware.CorrelationHeader)));
        Assert.DoesNotContain(fixture.Folder, body, StringComparison.Ordinal);
        Assert.Empty(owner.State.GetActivePackages());
        Assert.Equal(prior.SessionGeneration, owner.Generation);
        using var heartbeat = await client.PostAsJsonAsync("api/v1/dev-package-owners/graph-owner/heartbeat",
            new DevPackageOwnerHeartbeatRequest(instance, token));
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        var retained = await heartbeat.Content.ReadFromJsonAsync<DevPackageOwnerLeaseResponse>();
        Assert.NotNull(retained);
        Assert.Equal(prior.Revision, retained.Revision);
        Assert.Equal(prior.MutationId, retained.MutationId);
        Assert.Empty(retained.Packages);
        Assert.Equal(0, fixture.Requests);
        Assert.Contains(logs.Entries, entry => entry.Exception is InvalidDataException
            && entry.Message.Contains("compatibility graph", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("missing-candidate")]
    [InlineData("corrupt-candidate")]
    [InlineData("missing-catalog")]
    [InlineData("malformed-catalog")]
    public async Task DevelopmentImportsEndpoint_InvalidLocalEvidenceReturns400WithoutNetworkOrReceiptMutation(string failure)
    {
        using var fixture = new DevelopmentImportAuthorityTests.Fixture(platform: true) { Offline = true };
        var request = await fixture.LocalInputs();
        await using var server = await StartDevelopmentImportsServerAsync(fixture);
        using var client = AuthenticatedDevelopmentImportsClient(server);
        using var prepared = await client.PostAsJsonAsync("api/v1/dev-packages/prepare-imports", request);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var result = await prepared.Content.ReadFromJsonAsync<RuntimeDevPackageImportsPrepared>();
        Assert.NotNull(result);
        var receiptPath = fixture.Store.PathFor(result.ManifestSha256);
        var receipt = await File.ReadAllBytesAsync(receiptPath);
        var modified = File.GetLastWriteTimeUtc(receiptPath);
        var manifest = await File.ReadAllBytesAsync(fixture.ManifestPath);
        var sensitiveDiagnostic = fixture.Temp.GetPath("private-evidence-do-not-expose");
        switch (failure)
        {
            case "missing-candidate": request = request with { CandidateArchives = [] }; break;
            case "missing-catalog": request = request with { PlatformCatalogPath = null }; break;
            case "corrupt-candidate": await File.WriteAllTextAsync(request.CandidateArchives[0], sensitiveDiagnostic); break;
            default: await File.WriteAllTextAsync(request.PlatformCatalogPath!, "{\"" + sensitiveDiagnostic); break;
        }

        using var response = await client.PostAsJsonAsync("api/v1/dev-packages/prepare-imports", request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(RuntimeErrorCodes.Validation, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("contract-authority.release-conflict", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("exact local candidate archives", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(problem.RootElement.GetProperty("correlationId").GetString(),
            Assert.Single(response.Headers.GetValues(RuntimeProblemDetailsMiddleware.CorrelationHeader)));
        Assert.DoesNotContain(sensitiveDiagnostic, body, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Folder, body, StringComparison.Ordinal);
        Assert.DoesNotContain(request.PlatformCatalogPath ?? "private-evidence-do-not-expose", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-evidence-do-not-expose", body, StringComparison.Ordinal);
        Assert.Equal(receipt, await File.ReadAllBytesAsync(receiptPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(receiptPath));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.True((await fixture.Store.ResolveAsync(fixture.Source(), default)).IsLocal);
        Assert.Empty(await server.Services.GetRequiredService<InstalledPackageStore>().ListAsync());
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentImportsEndpoint_DoesNotReclassifyCancellationOrIoFailure(bool cancel)
    {
        using var fixture = new DevelopmentImportAuthorityTests.Fixture();
        var prepared = await fixture.Prepare();
        var receiptPath = fixture.Store.PathFor(prepared.ManifestSha256);
        var receipt = await File.ReadAllBytesAsync(receiptPath);
        var modified = File.GetLastWriteTimeUtc(receiptPath);
        var diagnostic = fixture.Temp.GetPath("private-transport-diagnostic");
        fixture.SendOverride = (_, _) =>
        {
            if (cancel) throw new OperationCanceledException(diagnostic);
            throw new IOException(diagnostic);
        };
        await using var server = await StartDevelopmentImportsServerAsync(fixture);
        using var client = AuthenticatedDevelopmentImportsClient(server);
        using var response = await client.PostAsJsonAsync("api/v1/dev-packages/prepare-imports",
            new RuntimePrepareDevPackageImportsRequest(fixture.Folder, ActivePolicyTestContext.Origin));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(cancel ? 499 : 500, (int)response.StatusCode);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(cancel ? RuntimeErrorCodes.Cancellation : RuntimeErrorCodes.Internal, problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("private-transport-diagnostic", body, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostic, body, StringComparison.Ordinal);
        Assert.Equal(receipt, await File.ReadAllBytesAsync(receiptPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(receiptPath));
    }

    private static Task<RuntimeHttpServer> StartDevelopmentImportsServerAsync(
        DevelopmentImportAuthorityTests.Fixture fixture,
        RecordingLogSink? logs = null)
        => RuntimeHttpServer.StartAsync(app => app.MapGroup("/api/v1").MapDevPackageOwnerEndpoints(), services =>
        {
            services.AddRuntimeHostServices(fixture.Policy.Paths, new RuntimeBearerTokenValidator(RuntimeHttpServer.Token));
            services.AddSingleton(fixture.Preparation);
            if (logs is not null)
            {
                services.AddSingleton(logs.CreateLogger<RuntimeProblemDetailsMiddleware>());
                services.AddSingleton(logs.CreateLogger<PackageSessionLoadService>());
            }
        });

    private static HttpClient AuthenticatedDevelopmentImportsClient(RuntimeHttpServer server)
    {
        var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);
        return client;
    }
}
