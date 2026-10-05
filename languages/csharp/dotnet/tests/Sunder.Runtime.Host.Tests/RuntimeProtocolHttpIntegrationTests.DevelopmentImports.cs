using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeProtocolHttpIntegrationTests
{
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

    private static Task<RuntimeHttpServer> StartDevelopmentImportsServerAsync(DevelopmentImportAuthorityTests.Fixture fixture)
        => RuntimeHttpServer.StartAsync(app => app.MapGroup("/api/v1").MapDevPackageOwnerEndpoints(), services =>
        {
            services.AddRuntimeHostServices(fixture.Policy.Paths, new RuntimeBearerTokenValidator(RuntimeHttpServer.Token));
            services.AddSingleton(fixture.Preparation);
        });

    private static HttpClient AuthenticatedDevelopmentImportsClient(RuntimeHttpServer server)
    {
        var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);
        return client;
    }
}
