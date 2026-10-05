using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeProtocolHttpIntegrationTests
{
    [Fact]
    public void ProtocolRevision1_IsExactAndRejectsOtherRevisions()
    {
        var otherRevision = CreateHandshake(
            protocolRevision: 2,
            minimumSupportedRevision: 2,
            maximumSupportedRevision: 2,
            [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.AtomicPackageSnapshotV1]);
        var revision1 = CreateHandshake(
            RuntimeProtocol.CurrentRevision,
            RuntimeProtocol.MinimumSupportedRevision,
            RuntimeProtocol.MaximumSupportedRevision,
            [RuntimeProtocolFeatures.VersionedApiV1]);
        var overlappingRange = CreateHandshake(
            protocolRevision: 2,
            minimumSupportedRevision: 1,
            maximumSupportedRevision: 2,
            [RuntimeProtocolFeatures.VersionedApiV1]);

        Assert.Equal(1, RuntimeProtocol.CurrentRevision);
        Assert.NotNull(RuntimeProtocolCompatibility.GetIncompatibility(otherRevision));
        Assert.NotNull(RuntimeProtocolCompatibility.GetIncompatibility(overlappingRange));
        Assert.Null(RuntimeProtocolCompatibility.GetIncompatibility(revision1));
    }

    [Fact]
    public void AtomicSnapshotFeature_IsRequiredOnlyBySnapshotOperations()
    {
        var handshake = CreateHandshake(
            RuntimeProtocol.CurrentRevision,
            RuntimeProtocol.MinimumSupportedRevision,
            RuntimeProtocol.MaximumSupportedRevision,
            [RuntimeProtocolFeatures.VersionedApiV1]);

        Assert.True(RuntimeProtocolCompatibility.IsCompatible(handshake));
        var incompatibility = RuntimeProtocolCompatibility.GetIncompatibility(
            handshake,
            RuntimeProtocolFeatures.AtomicPackageSnapshotV1);

        Assert.Contains(RuntimeProtocolFeatures.AtomicPackageSnapshotV1, incompatibility, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapHttpEndpoints_RemainReachableWhileBlockedAndExposeFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-bootstrap-http-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(root);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator(RuntimeHttpServer.Token));
        var app = builder.Build();
        app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
        app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
        app.MapRuntimeHandshakeEndpoint();
        app.MapGroup("/api/v1")
            .MapSystemEndpoints(DateTimeOffset.UtcNow)
            .MapPackageSessionEndpoints();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PackageLifecycleOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = app.Services.GetRequiredService<RuntimeSessionOwner>();
        var bootstrap = RuntimeHostBootstrapRunner.StartAsync(
            app,
            sessions,
            async cancellationToken =>
            {
                entered.TrySetResult();
                return await release.Task.WaitAsync(cancellationToken);
            });
        var failure = PackageLifecycleOperationResult.Failed(
            "blocked bootstrap failed",
            warnings: ["bootstrap warning"],
            errors: ["blocked bootstrap failed"]);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses ?? [])) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);

            var handshake = await client.GetFromJsonAsync<RuntimeHandshakeResponse>("api/handshake");
            var startingSystem = await client.GetFromJsonAsync<SystemStatusResponse>("api/v1/system");
            var startingSnapshot = await client.GetFromJsonAsync<RuntimePackageSnapshot>("api/v1/packages/snapshot");

            Assert.NotNull(handshake);
            Assert.NotNull(startingSystem);
            Assert.NotNull(startingSnapshot);
            Assert.Equal(RuntimeBootstrapState.Starting, startingSystem.State);
            Assert.Equal(RuntimeBootstrapState.Starting, startingSnapshot.BootstrapState);
            Assert.False(startingSystem.IsReady);
            Assert.Equal(handshake.RuntimeInstanceId, startingSnapshot.RuntimeInstanceId);
            Assert.False(bootstrap.IsCompleted);

            release.TrySetResult(failure);
            await bootstrap.WaitAsync(TimeSpan.FromSeconds(5));

            var failedSystem = await client.GetFromJsonAsync<SystemStatusResponse>("api/v1/system");
            var failedSnapshot = await client.GetFromJsonAsync<RuntimePackageSnapshot>("api/v1/packages/snapshot");
            Assert.NotNull(failedSystem);
            Assert.NotNull(failedSnapshot);
            Assert.Equal(RuntimeBootstrapState.Failed, failedSystem.State);
            Assert.Equal("runtime.v1.package-validation", failedSystem.StartupFailure?.Code);
            Assert.Equal(RuntimeBootstrapState.Failed, failedSnapshot.BootstrapState);
            Assert.False(failedSystem.IsReady);
            Assert.Contains("bootstrap warning", failedSnapshot.Warnings);
            Assert.Contains("blocked bootstrap failed", failedSnapshot.Errors);
        }
        finally
        {
            release.TrySetResult(failure);
            await bootstrap;
            await app.StopAsync();
            await app.DisposeAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task BootstrapCancellation_FromApplicationStopping_IsNotReportedAsFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-bootstrap-stop-tests", Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRuntimeHostServices(
            new RuntimePackagePaths(root),
            new RuntimeBearerTokenValidator(RuntimeHttpServer.Token));
        var app = builder.Build();
        var sessions = app.Services.GetRequiredService<RuntimeSessionOwner>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bootstrap = RuntimeHostBootstrapRunner.StartAsync(
            app,
            sessions,
            async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable bootstrap continuation.");
            });

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            app.Lifetime.StopApplication();
            await bootstrap.WaitAsync(TimeSpan.FromSeconds(5));

            var snapshot = sessions.GetSnapshot();
            Assert.Equal(RuntimeBootstrapState.ShuttingDown, snapshot.BootstrapState);
            Assert.DoesNotContain(snapshot.Errors, error => error.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            app.Lifetime.StopApplication();
            await app.DisposeAsync();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task BootstrapRunner_InvokesConnectionPublicationOnlyAfterListenerBind()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRuntimeHostServices(
            new RuntimePackagePaths(Path.Combine(Path.GetTempPath(), "sunder-runtime-bind-tests", Guid.NewGuid().ToString("N"))),
            new RuntimeBearerTokenValidator(RuntimeHttpServer.Token));
        var app = builder.Build();
        var sessions = app.Services.GetRequiredService<RuntimeSessionOwner>();
        var published = false;
        try
        {
            await RuntimeHostBootstrapRunner.StartAsync(
                app,
                sessions,
                _ =>
                {
                    Assert.True(published);
                    return Task.FromResult(new PackageLifecycleOperationResult(true, "ready", [], [], [], []));
                },
                () =>
                {
                    var addresses = app.Services.GetRequiredService<IServer>()
                        .Features.Get<IServerAddressesFeature>()?.Addresses;
                    Assert.NotEmpty(addresses ?? []);
                    published = true;
                });

            Assert.True(published);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Handshake_IsAuthenticatedUnversionedAndStableForRuntimeInstance()
    {
        await using var server = await RuntimeHttpServer.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };

        using var missing = await anonymous.GetAsync("api/handshake");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("Bearer", Assert.Single(missing.Headers.WwwAuthenticate).Scheme);
        var missingProblem = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("runtime.v1.authentication", missingProblem.GetProperty("code").GetString());

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        using var wrong = await anonymous.GetAsync("api/handshake");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);
        var first = await anonymous.GetFromJsonAsync<RuntimeHandshakeResponse>("api/handshake");
        var second = await anonymous.GetFromJsonAsync<RuntimeHandshakeResponse>("api/handshake");

        Assert.NotNull(first);
        Assert.Equal(RuntimeProtocol.Identity, first.ProtocolIdentity);
        Assert.Equal(RuntimeProtocol.CurrentRevision, first.ProtocolRevision);
        Assert.Equal(first.RuntimeInstanceId, second?.RuntimeInstanceId);
        Assert.Contains(RuntimeProtocolFeatures.AtomicPackageSnapshotV1, first.SupportedFeatures);
        Assert.Contains(RuntimeProtocolFeatures.PackageStageStatusV1, first.SupportedFeatures);
        Assert.Contains(RuntimeProtocolFeatures.DevPackageOwnerLeasesV1, first.SupportedFeatures);
        Assert.Contains(RuntimeProtocolFeatures.DevPackageImportsV1, first.SupportedFeatures);
        Assert.Contains(RuntimeProtocolFeatures.SchemaFirstRpcV1, first.SupportedFeatures);
        Assert.Contains(RuntimeProtocolFeatures.RpcPermissionsV1, first.SupportedFeatures);
        Assert.Contains(RuntimeProtocolFeatures.RuntimeResourcePolicyV1, first.SupportedFeatures);
        Assert.Contains(RuntimeProtocolFeatures.PackageRuntimeRecoveryV1, first.SupportedFeatures);
        Assert.Equal(
            RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Standard),
            first.ResourceProfile?.EffectiveProfileHash);
        Assert.Equal(
            RuntimeResourcePolicies.Standard.MaximumRpcValueBytes,
            first.ResourceProfile?.MaximumRpcValueBytes);
        Assert.Equal(
            RuntimeResourcePolicies.Standard.MaximumContentReferenceBytes,
            first.ResourceProfile?.MaximumContentReferenceBytes);
        Assert.False(string.IsNullOrWhiteSpace(first.Product.ProductVersion));
    }

    [Fact]
    public async Task Malformed_json_is_coded_problem_details_with_server_correlation()
    {
        await using var server = await RuntimeHttpServer.StartAsync(app =>
            app.MapPost(
                "/api/v1/parse",
                static (PackageLifecycleStageRequest _) => Results.NoContent()));
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/parse")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("runtime.v1.validation", problem.GetProperty("code").GetString());
        Assert.Equal(
            response.Headers.GetValues(RuntimeProblemDetailsMiddleware.CorrelationHeader).Single(),
            problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task PayloadTooLargeBadHttpRequest_PreservesCanonicalProblemDetails()
    {
        const string correlationId = "payload-too-large-correlation";
        const string detail = "Request body too large.";
        var middleware = new RuntimeProblemDetailsMiddleware(
            _ => throw new BadHttpRequestException(detail, StatusCodes.Status413PayloadTooLarge),
            NullLogger<RuntimeProblemDetailsMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers[RuntimeProblemDetailsMiddleware.CorrelationHeader] = correlationId;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Equal(
            $"https://sunder.dev/problems/{RuntimeErrorCodes.UploadLimit}",
            problem.RootElement.GetProperty("type").GetString());
        Assert.Equal("Upload limit exceeded", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(detail, problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(RuntimeErrorCodes.UploadLimit, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(correlationId, problem.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(correlationId, context.Response.Headers[RuntimeProblemDetailsMiddleware.CorrelationHeader]);
    }

    [Fact]
    public void DefaultRequestBodyLimit_MatchesMaximumPackageFileSize()
    {
        Assert.Equal(
            PackageStorageValidation.MaximumFileBytes,
            RuntimeRequestSizeLimits.DefaultMaxRequestBodyBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedRequestBody_ReturnsPayloadTooLargeForFixedAndChunkedBodies(bool chunked)
    {
        await using var server = await RuntimeHttpServer.StartAsync(
            app => app.MapPost("/api/v1/body-limit", async (HttpRequest request, CancellationToken cancellationToken) =>
            {
                await request.Body.CopyToAsync(Stream.Null, cancellationToken);
                return Results.NoContent();
            }),
            maxRequestBodyBytes: 3);
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            RuntimeHttpServer.Token);
        using HttpContent content = chunked
            ? new UnknownLengthContent([1, 2, 3, 4])
            : new ByteArrayContent([1, 2, 3, 4]);

        using var response = await client.PostAsync("api/v1/body-limit", content);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            $"https://sunder.dev/problems/{RuntimeErrorCodes.UploadLimit}",
            problem.GetProperty("type").GetString());
        Assert.Equal("Upload limit exceeded", problem.GetProperty("title").GetString());
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.GetProperty("status").GetInt32());
        Assert.Equal(RuntimeErrorCodes.UploadLimit, problem.GetProperty("code").GetString());
        Assert.Equal(
            response.Headers.GetValues(RuntimeProblemDetailsMiddleware.CorrelationHeader).Single(),
            problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task RpcManagementEndpoints_ReturnCatalogPermissionsAndRejectUnknownGrant()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-rpc-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var server = await RuntimeHttpServer.StartAsync(
                app => app.MapGroup("/api/v1").MapRuntimeRpcEndpoints(),
                services =>
                {
                    services.AddSingleton(new RuntimePackagePaths(root));
                    services.AddSingleton<RuntimeEventStreamService>();
                    services.AddSingleton<RuntimeRpcCatalog>();
                    services.AddSingleton<RuntimeRpcPermissionStore>();
                    services.AddSingleton(provider => new RuntimeSessionOwner(
                        NullLogger<RuntimeSessionOwner>.Instance,
                        provider.GetRequiredService<RuntimeEventStreamService>(),
                        rpcCatalog: provider.GetRequiredService<RuntimeRpcCatalog>()));
                    services.AddSingleton(provider => provider.GetRequiredService<RuntimeSessionOwner>().State);
                    services.AddSingleton<RuntimeRpcPermissionService>();
                });
            using var client = new HttpClient { BaseAddress = server.BaseUri };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);

            var catalog = await client.GetFromJsonAsync<RuntimeRpcCatalogSnapshot>("api/v1/rpc/catalog");
            var permissions = await client.GetFromJsonAsync<RuntimeRpcPermissionSnapshot>("api/v1/rpc/permissions");
            using var grant = await client.PostAsJsonAsync(
                "api/v1/rpc/permissions/grant",
                new RuntimeRpcPermissionUpdateRequest(new RuntimeRpcPermissionKey(
                    "missing.package",
                    "1.0.0",
                    new string('a', 64),
                    SunderPackageFormat.RuntimeHostRole,
                    "example.rpc",
                    "1.0.0",
                    new string('b', 64),
                    "invoke")));

            Assert.NotNull(catalog);
            Assert.Empty(catalog.Providers);
            Assert.NotNull(permissions);
            Assert.Empty(permissions.Permissions);
            Assert.Equal(HttpStatusCode.NotFound, grant.StatusCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AppInvariantViolationEndpoint_ReturnsTypedFailureForStaleSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-rpc-app-report-http-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var server = await RuntimeHttpServer.StartAsync(
                app => app.MapGroup("/api/v1").MapRuntimeRpcEndpoints(),
                services => services.AddRuntimeHostServices(
                    new RuntimePackagePaths(root),
                    new RuntimeBearerTokenValidator(RuntimeHttpServer.Token)));
            using var client = new HttpClient { BaseAddress = server.BaseUri };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                RuntimeHttpServer.Token);

            using var response = await client.PostAsJsonAsync(
                "api/v1/rpc/app/invariant-violation",
                new RuntimeRpcAppInvariantViolationRequest(
                    "stale-session",
                    "rpc1_exact_provider",
                    "sanitized failure"));
            var result = await response.Content.ReadFromJsonAsync<RuntimeRpcAppInvariantViolationResponse>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(result);
            Assert.False(result.Accepted);
            Assert.Equal(RuntimeRpcErrorKind.Unavailable, result.Error?.Kind);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Unknown_runtime_route_returns_coded_problem_details_instead_of_empty_404()
    {
        await using var server = await RuntimeHttpServer.StartAsync(app =>
            app.MapFallback("/api/{**path}", ThrowRuntimeRouteNotFound));
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);

        using var response = await client.GetAsync("api/v1/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("runtime.v1.not-found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Domain_operation_rejection_is_typed_http_200_not_problem_details()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-domain-rejection-tests", Guid.NewGuid().ToString("N"));
        await using var server = await RuntimeHttpServer.StartAsync(
            app => app.MapGroup("/api/v1").MapInstalledPackageEndpoints(),
            services => services.AddRuntimeHostServices(
                new RuntimePackagePaths(root),
                new RuntimeBearerTokenValidator(RuntimeHttpServer.Token)));
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);

        using var response = await client.PostAsJsonAsync(
            "api/v1/packages/store/stage",
            new PackageStoreStageRequest([]));
        var result = await response.Content.ReadFromJsonAsync<PackageStoreStageResult>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("mutation", Assert.Single(result.Errors), StringComparison.OrdinalIgnoreCase);
        TryDeleteDirectory(root);
    }

    [Fact]
    public async Task ExecutionRuntimeManagement_UsesTypedResultsAndConsumesOpaqueUploadsOnce()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sunder-execution-runtime-http-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            await using var server = await RuntimeHttpServer.StartAsync(
                app => app.MapGroup("/api/v1")
                    .MapContentTransferEndpoints()
                    .MapExecutionRuntimeEndpoints(),
                services => services.AddRuntimeHostServices(
                    new RuntimePackagePaths(root),
                    new RuntimeBearerTokenValidator(RuntimeHttpServer.Token)));
            using var client = new HttpClient { BaseAddress = server.BaseUri };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                RuntimeHttpServer.Token);

            var installed = await client.GetFromJsonAsync<IReadOnlyList<InstalledExecutionRuntimeSummary>>(
                "api/v1/execution-runtimes");
            Assert.Empty(installed ?? []);

            using var invalidEnsure = await client.PostAsJsonAsync(
                "api/v1/execution-runtimes/ensure",
                new RuntimeExecutionRuntimeEnsureRequest(
                    "https://registry.example.test/",
                    new RuntimeExecutionRuntimeRequirement(
                        SunderExecutionRuntimeFormat.DotNetRuntimeId,
                        ">=8.0.0",
                        SunderExecutionRuntimeFormat.DotNetModuleAbi)));
            var invalidEnsureProblem = await invalidEnsure.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.BadRequest, invalidEnsure.StatusCode);
            Assert.Equal(
                Sunder.Registry.Contracts.ExecutionRuntimeErrorCodes.RequirementInvalid,
                invalidEnsureProblem.GetProperty("code").GetString());

            using var uploadContent = new ByteArrayContent("not a runtime archive"u8.ToArray());
            uploadContent.Headers.ContentType = new MediaTypeHeaderValue(
                "application/vnd.sunder.execution-runtime");
            using var uploadResponse = await client.PostAsync(
                "api/v1/uploads/execution-runtimes",
                uploadContent);
            var upload = await uploadResponse.Content.ReadFromJsonAsync<ContentUploadDescriptor>();
            Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
            Assert.NotNull(upload);
            Assert.DoesNotContain(root, JsonSerializer.Serialize(upload), StringComparison.OrdinalIgnoreCase);

            using var importResponse = await client.PostAsJsonAsync(
                "api/v1/execution-runtimes/import",
                new RuntimeExecutionRuntimeImportRequest(upload.UploadId));
            var import = await importResponse.Content.ReadFromJsonAsync<RuntimeExecutionRuntimeOperationResult>();
            Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
            Assert.NotNull(import);
            Assert.False(import.Success);
            Assert.Equal(Sunder.Registry.Contracts.ExecutionRuntimeErrorCodes.ArtifactInvalid, import.ErrorCode);

            using var repeatedImport = await client.PostAsJsonAsync(
                "api/v1/execution-runtimes/import",
                new RuntimeExecutionRuntimeImportRequest(upload.UploadId));
            var repeatedProblem = await repeatedImport.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.NotFound, repeatedImport.StatusCode);
            Assert.Equal("runtime.v1.not-found", repeatedProblem.GetProperty("code").GetString());

            using var verifyResponse = await client.PostAsync(
                "api/v1/execution-runtimes/sunder.runtime.dotnet/versions/8.0.1/revisions/1/linux-x64/abis/sunder.dotnet-worker-module.v1/verify",
                content: null);
            var verify = await verifyResponse.Content.ReadFromJsonAsync<RuntimeExecutionRuntimeOperationResult>();
            Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
            Assert.False(verify?.Success);
            Assert.Equal(Sunder.Registry.Contracts.ExecutionRuntimeErrorCodes.Missing, verify?.ErrorCode);

            var malformedIdentity = new ExecutionRuntimeIdentity(
                SunderExecutionRuntimeFormat.DotNetRuntimeId,
                "8.0.2",
                1,
                "linux-x64",
                SunderExecutionRuntimeFormat.DotNetModuleAbi);
            var malformedReceiptPath = new ExecutionRuntimeStoreLayout(new RuntimePackagePaths(root))
                .GetReceiptPath(malformedIdentity);
            Directory.CreateDirectory(Path.GetDirectoryName(malformedReceiptPath)!);
            await File.WriteAllTextAsync(malformedReceiptPath, "{");
            var malformedRoute =
                "api/v1/execution-runtimes/sunder.runtime.dotnet/versions/8.0.2/revisions/1/linux-x64/abis/sunder.dotnet-worker-module.v1";

            using var malformedVerifyResponse = await client.PostAsync(malformedRoute + "/verify", content: null);
            var malformedVerify = await malformedVerifyResponse.Content
                .ReadFromJsonAsync<RuntimeExecutionRuntimeOperationResult>();
            Assert.Equal(HttpStatusCode.OK, malformedVerifyResponse.StatusCode);
            Assert.False(malformedVerify?.Success);
            Assert.Equal(
                Sunder.Registry.Contracts.ExecutionRuntimeErrorCodes.IntegrityFailed,
                malformedVerify?.ErrorCode);

            var malformedImpact = await client.GetFromJsonAsync<RuntimeExecutionRuntimeImpact>(
                malformedRoute + "/impact");
            Assert.NotNull(malformedImpact);
            Assert.Equal(0, malformedImpact.PackageReferenceCount);
            using var malformedRepairResponse = await client.PostAsJsonAsync(
                malformedRoute + "/repair",
                new RuntimeExecutionRuntimeActionRequest(malformedImpact.Fingerprint));
            var malformedRepair = await malformedRepairResponse.Content
                .ReadFromJsonAsync<RuntimeExecutionRuntimeOperationResult>();
            Assert.Equal(HttpStatusCode.OK, malformedRepairResponse.StatusCode);
            Assert.False(malformedRepair?.Success);
            Assert.Equal(
                Sunder.Registry.Contracts.ExecutionRuntimeErrorCodes.IntegrityFailed,
                malformedRepair?.ErrorCode);
            Assert.Equal(malformedImpact, malformedRepair?.Impact);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Reset_conflict_uses_coded_problem_details_instead_of_anonymous_error_shape()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-reset-conflict-tests", Guid.NewGuid().ToString("N"));
        await using var server = await RuntimeHttpServer.StartAsync(
            app => app.MapGroup("/api/v1").MapSystemEndpoints(DateTimeOffset.UtcNow),
            services => services.AddRuntimeHostServices(
                new RuntimePackagePaths(root),
                new RuntimeBearerTokenValidator(RuntimeHttpServer.Token)));
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);

        using var response = await client.PostAsJsonAsync(
            "api/v1/system/reset/drain",
            new RuntimeResetConfirmRequest("invalid") { RequireBoundStateRoot = true });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"Expected conflict, received {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("runtime.v1.conflict", problem.GetProperty("code").GetString());
        TryDeleteDirectory(root);
    }

    [Fact]
    public async Task DevPackageOwnerEndpoint_IsIdempotentAndRejectsWrongOwnerToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-dev-owner-http-tests", Guid.NewGuid().ToString("N"));
        await using var server = await RuntimeHttpServer.StartAsync(
            app => app.MapGroup("/api/v1").MapDevPackageOwnerEndpoints(),
            services => services.AddRuntimeHostServices(
                new RuntimePackagePaths(root),
                new RuntimeBearerTokenValidator(RuntimeHttpServer.Token)));
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);
        server.Services.GetRequiredService<RuntimeSessionOwner>().MarkReady();
        var protocol = await client.GetFromJsonAsync<RuntimeHandshakeResponse>("api/handshake");
        Assert.NotNull(protocol);
        var request = new DevPackageOwnerMutationRequest(
            protocol.RuntimeInstanceId,
            "owner-token-aaaaaaaaaaaaaaaaaaaa",
            "mutation-1",
            1,
            []);

        using var firstResponse = await client.PutAsJsonAsync("api/v1/dev-package-owners/owner-a", request);
        using var retryResponse = await client.PutAsJsonAsync("api/v1/dev-package-owners/owner-a", request);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();
        var retryBody = await retryResponse.Content.ReadAsStringAsync();

        Assert.True(firstResponse.StatusCode == HttpStatusCode.OK, firstBody);
        Assert.True(retryResponse.StatusCode == HttpStatusCode.OK, retryBody);
        var first = JsonSerializer.Deserialize<DevPackageOwnerLeaseResponse>(firstBody, JsonSerializerOptions.Web);
        var retry = JsonSerializer.Deserialize<DevPackageOwnerLeaseResponse>(retryBody, JsonSerializerOptions.Web);
        Assert.Equal(first?.Revision, retry?.Revision);
        Assert.Equal(first?.SessionGeneration, retry?.SessionGeneration);

        using var wrongToken = await client.PostAsJsonAsync(
            "api/v1/dev-package-owners/owner-a/heartbeat",
            new DevPackageOwnerHeartbeatRequest(
                protocol.RuntimeInstanceId,
                "wrong-owner-token-xxxxxxxxxxxxxxxx"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        TryDeleteDirectory(root);
    }

    [Fact]
    public async Task DevelopmentImportsEndpointRequiresRuntimeAuthenticationAndReturnsUnpublishedEvidence()
    {
        using var fixture = new DevelopmentImportAuthorityTests.Fixture();
        await using var server = await RuntimeHttpServer.StartAsync(app => app.MapGroup("/api/v1").MapDevPackageOwnerEndpoints(), services =>
        {
            services.AddRuntimeHostServices(fixture.Policy.Paths, new RuntimeBearerTokenValidator(RuntimeHttpServer.Token));
            services.AddSingleton(fixture.Preparation);
        });
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };
        using var denied = await anonymous.PostAsJsonAsync("api/v1/dev-packages/prepare-imports", new RuntimePrepareDevPackageImportsRequest(fixture.Folder, ActivePolicyTestContext.Origin));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); Assert.Equal(0, fixture.Requests);
        using var client = new RuntimeManagementClient(() => new RuntimeConnectionInfo(server.BaseUri, RuntimeHttpServer.Token), new HttpClientHandler());
        var result = await client.PrepareDevPackageImportsAsync(new(fixture.Folder, ActivePolicyTestContext.Origin));
        Assert.Equal("example.local", result.PackageId); Assert.Equal(1, result.PackageArtifacts); Assert.Equal(1, fixture.Requests);
        Assert.Empty(await server.Services.GetRequiredService<InstalledPackageStore>().ListAsync());
        Assert.NotNull(await fixture.Store.ResolveAsync(fixture.Source(), default));
    }

    private static IResult ThrowRuntimeRouteNotFound()
        => throw new RuntimeNotFoundException("The requested Runtime API endpoint was not found.");

    [Fact]
    public async Task PackageDataOperation_RetirementCancelsStorageAndAllowsSessionDrain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingPackageStateStore(entered);
        var state = CreateState(TimeSpan.FromSeconds(2));
        await state.PublishSessionAsync(CreatePackageSession(store));
        var data = new PackageDataAccessService(state);
        var read = data.GetStateAsync("test.package", "key", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var publication = state.PublishSessionAsync(CreateEmptySession());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(2, (await publication.WaitAsync(TimeSpan.FromSeconds(2))).Generation);
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task AppCallScopeOpenEndpoint_ReturnsTypedRpcFailureForRetiredSession()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sunder-runtime-app-rpc-http-tests",
            Guid.NewGuid().ToString("N"));
        await using var server = await RuntimeHttpServer.StartAsync(
            app => app.MapGroup("/api/v1").MapRuntimeRpcEndpoints(),
            services => services.AddRuntimeHostServices(
                new RuntimePackagePaths(root),
                new RuntimeBearerTokenValidator(RuntimeHttpServer.Token)));
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            RuntimeHttpServer.Token);

        using var response = await client.PostAsJsonAsync(
            "api/v1/rpc/app/call-scopes/open",
            new RuntimeRpcAppCallScopeOpenRequest("retired-session", null));
        var error = await response.Content.ReadFromJsonAsync<RuntimeRpcErrorDescriptor>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(error);
        Assert.Equal(RuntimeRpcErrorKind.Unavailable, error.Kind);
        Assert.Equal("rpc.app-session.retired", error.Code);
        Assert.Equal(RuntimeRpcDispatchState.NotDispatched, error.DispatchState);
        Assert.Equal(RuntimeRpcRetrySafety.Safe, error.RetrySafety);
        TryDeleteDirectory(root);
    }

    private static RuntimeHandshakeResponse CreateHandshake(
        int protocolRevision,
        int minimumSupportedRevision,
        int maximumSupportedRevision,
        IReadOnlyList<string> features)
        => new(
            RuntimeProtocol.Identity,
            protocolRevision,
            minimumSupportedRevision,
            maximumSupportedRevision,
            Guid.NewGuid(),
            features,
            new RuntimeProductVersionDiagnostics("Sunder.Runtime.Host", "1.1.0", "1.1.0-test"));

    private static PackageSessionState CreateState(TimeSpan drainTimeout)
        => new(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            static () => { },
            static _ => { },
            drainTimeout);

    private static ActivePackageSession CreatePackageSession(IPackageKeyValueStore? stateStore = null)
    {
        const string packageId = "test.package";
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var loadedPackage = new ActiveLoadedPackage(
            new ActivePackageDescriptor(packageId, packageId, "1.0.0", PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready, []),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, root),
            null,
            stateStore ?? new JsonPackageKeyValueStore(Path.Combine(root, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(root, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            new LocalPackageFileStore(Path.Combine(root, "files")),
            EmptyTestPackageSettings.Instance,
            new TestRuntimePackageWorker());
        return new ActivePackageSession(
            root,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase) { [packageId] = loadedPackage },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = new(
                    packageId,
                    packageId,
                    "1.0.0",
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    [],
                    null,
                    null,
                    null,
                    0),
            });
    }

    private static ActivePackageSession CreateEmptySession()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ActivePackageSession(
            root,
            new Dictionary<string, ActiveLoadedPackage>(),
            new Dictionary<string, SessionPackageDescriptor>());
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class BlockingPackageStateStore(TaskCompletionSource entered) : IPackageKeyValueStore
    {
        public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RuntimeHttpServer(WebApplication app, Uri baseUri) : IAsyncDisposable
    {
        public const string Token = "integration-runtime-token";

        public Uri BaseUri { get; } = baseUri;

        public IServiceProvider Services => app.Services;

        public static async Task<RuntimeHttpServer> StartAsync(
            Action<WebApplication>? map = null,
            Action<IServiceCollection>? configureServices = null,
            long? maxRequestBodyBytes = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.WebHost.ConfigureKestrel(options =>
                options.Limits.MaxRequestBodySize = maxRequestBodyBytes
                    ?? RuntimeRequestSizeLimits.DefaultMaxRequestBodyBytes);
            builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
            builder.Services.AddSingleton(new RuntimeBearerTokenValidator(Token));
            builder.Services.AddSingleton<RuntimeProtocolDescriptor>();
            configureServices?.Invoke(builder.Services);
            var app = builder.Build();
            app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
            app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
            app.MapRuntimeHandshakeEndpoint();
            map?.Invoke(app);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = Assert.Single(addresses ?? []);
            return new RuntimeHttpServer(app, new Uri(address));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
