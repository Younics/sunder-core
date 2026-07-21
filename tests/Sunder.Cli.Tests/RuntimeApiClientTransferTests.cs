using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Cli;
using Sunder.Host.Client;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Cli.Tests;

public sealed class RuntimeApiClientTransferTests
{
    [Fact]
    public async Task Local_package_upload_and_mutation_are_owned_by_runtime_client()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Authorization, byte[] Body)>();
        var handler = new DelegateHandler(async request =>
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync();
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return request.RequestUri.AbsolutePath switch
            {
                "/api/handshake" => Json(CreateHandshake()),
                "/api/v1/packages/installed" => Json(Array.Empty<InstalledPackageDescriptor>()),
                "/api/v1/uploads/packages" => Json(new ContentUploadDescriptor("upload-1", "hash", body.Length, "demo.sunderpkg", "application/vnd.sunder.package")),
                "/api/v1/packages/store/stage" => Json(new PackageStoreStageResult(
                    "stage-1", new PackageOperationResult(true, "staged", false, false, [], []), [], [])),
                "/api/v1/packages/store/stage/stage-1/commit" => Json(new PackageOperationResult(true, "installed", true, false, [], [])),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var path = Path.Combine(Path.GetTempPath(), $"sunder-cli-{Guid.NewGuid():N}.pkg");
        await File.WriteAllTextAsync(path, "archive");
        try
        {
            using var client = new RuntimeManagementClient(
                () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "runtime-secret"), handler);
            var result = await client.ApplyLocalPackageAsync(path, "demo", false, false);
            Assert.True(result.Success);
            Assert.Equal(["/api/handshake", "/api/v1/packages/installed", "/api/v1/uploads/packages", "/api/v1/packages/store/stage", "/api/v1/packages/store/stage/stage-1/commit"], requests.Select(item => item.Path));
            Assert.All(requests, request => Assert.Equal("Bearer runtime-secret", request.Authorization));
            Assert.Equal("archive", Encoding.UTF8.GetString(requests[2].Body));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Local_package_commit_transport_loss_is_reconciled_from_stage_status()
    {
        var requests = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/api/handshake" => Json(CreateHandshake()),
                "/api/v1/packages/installed" => Json(Array.Empty<InstalledPackageDescriptor>()),
                "/api/v1/uploads/packages" => Json(new ContentUploadDescriptor("upload-1", "hash", 7, "demo.sunderpkg", "application/vnd.sunder.package")),
                "/api/v1/packages/store/stage" => Json(new PackageStoreStageResult(
                    "stage-1", new PackageOperationResult(true, "staged", false, false, [], []), [], [])),
                "/api/v1/packages/store/stage/stage-1/commit" => throw new HttpRequestException("response lost after commit"),
                "/api/v1/packages/stages/stage-1" => Json(new RuntimePackageStageStatus(
                    "stage-1",
                    RuntimePackageStageKind.PackageStore,
                    RuntimePackageStageState.Committed,
                    DateTimeOffset.UtcNow,
                    new RuntimePackageStamp(Guid.NewGuid(), 4),
                    RuntimeSessionApplied: false,
                    ReconciliationPending: true,
                    "Store committed; reconciliation pending.")),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        });
        var path = Path.Combine(Path.GetTempPath(), $"sunder-cli-{Guid.NewGuid():N}.pkg");
        await File.WriteAllTextAsync(path, "archive");

        try
        {
            using var client = new RuntimeManagementClient(
                () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "runtime-secret"),
                handler);

            var result = await client.ApplyLocalPackageAsync(path, "demo", false, false);

            Assert.True(result.Success);
            Assert.True(result.StoreCommitted);
            Assert.True(result.RuntimeSessionReconciliationPending);
            Assert.False(result.RuntimeSessionApplied);
            Assert.Contains("/api/v1/packages/stages/stage-1", requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Stack_download_verifies_and_writes_explicit_output()
    {
        var bytes = "stack archive"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        }));
        using var client = new RegistryClient(new Uri("https://registry.test/"), handler);
        var output = Path.Combine(Path.GetTempPath(), $"sunder-stack-{Guid.NewGuid():N}.sunderstack");
        try
        {
            await client.DownloadStackAsync(new RegistryStackArtifact(hash, bytes.Length, "/artifact"), "stack-1", output, force: false, default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Stack_download_does_not_replace_existing_output_without_force()
    {
        var requested = false;
        var handler = new DelegateHandler(_ =>
        {
            requested = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new RegistryClient(new Uri("https://registry.test/"), handler);
        var output = Path.Combine(Path.GetTempPath(), $"sunder-stack-{Guid.NewGuid():N}.sunderstack");
        await File.WriteAllTextAsync(output, "original");
        try
        {
            var error = await Assert.ThrowsAsync<CliConflictException>(() => client.DownloadStackAsync(
                new RegistryStackArtifact("", null, "/artifact"), "stack-1", output, force: false, default));

            Assert.Contains("--force", error.Message, StringComparison.Ordinal);
            Assert.Equal("original", await File.ReadAllTextAsync(output));
            Assert.False(requested);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Stack_download_replaces_existing_output_only_with_force()
    {
        var bytes = "replacement"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        }));
        using var client = new RegistryClient(new Uri("https://registry.test/"), handler);
        var output = Path.Combine(Path.GetTempPath(), $"sunder-stack-{Guid.NewGuid():N}.sunderstack");
        await File.WriteAllTextAsync(output, "original");
        try
        {
            await client.DownloadStackAsync(
                new RegistryStackArtifact(hash, bytes.Length, "/artifact"), "stack-1", output, force: true, default);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Runtime_client_rejects_oversized_json_response()
    {
        var handler = new DelegateHandler(request => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/handshake"
                ? Json(CreateHandshake())
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"service\":\"oversized\"}"),
                }));
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "runtime-secret"),
            handler,
            new RuntimeClientPolicyOptions { MaxJsonResponseBytes = 8 });

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetSystemStatusAsync());
    }

    [Fact]
    public async Task Runtime_client_never_deserializes_problem_details_as_operation_result_and_preserves_correlation()
    {
        var handler = new DelegateHandler(request => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/handshake"
                ? Json(CreateHandshake())
                : new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent(
                        """
                        {
                          "type":"https://sunder.dev/problems/runtime.v1.package-validation",
                          "title":"Package validation failed",
                          "status":422,
                          "detail":"The staged package is invalid.",
                          "code":"runtime.v1.package-validation",
                          "correlationId":"server-correlation-42",
                          "stageId":"looks-like-a-success-payload"
                        }
                        """,
                        Encoding.UTF8,
                        "application/problem+json"),
                }));
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "runtime-secret"),
            handler);

        var exception = await Assert.ThrowsAsync<RuntimeClientException>(() =>
            client.StagePackageStoreChangesAsync(new PackageStoreStageRequest([])));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Equal("runtime.v1.package-validation", exception.ErrorCode);
        Assert.Equal("server-correlation-42", exception.CorrelationId);
        Assert.Contains("staged package is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_client_returns_typed_domain_rejection_from_successful_http_response()
    {
        var rejection = PackageStoreStageResult.Failed("The package is already installed.");
        var handler = new DelegateHandler(request => Task.FromResult(
            request.RequestUri?.AbsolutePath == "/api/handshake"
                ? Json(CreateHandshake())
                : Json(rejection)));
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "runtime-secret"),
            handler);

        var result = await client.StagePackageStoreChangesAsync(new PackageStoreStageRequest([]));

        Assert.False(result.Success);
        Assert.Contains("already installed", Assert.Single(result.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_client_rejects_incompatible_handshake_before_versioned_request()
    {
        var paths = new List<string>();
        var incompatible = CreateHandshake() with
        {
            ProtocolRevision = RuntimeProtocol.CurrentRevision + 1,
            MinimumSupportedRevision = RuntimeProtocol.CurrentRevision + 1,
            MaximumSupportedRevision = RuntimeProtocol.CurrentRevision + 1,
        };
        var handler = new DelegateHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath == "/api/handshake"
                ? Json(incompatible)
                : throw new InvalidOperationException("Versioned call must not be sent."));
        });
        using var client = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "runtime-secret"),
            handler);

        await Assert.ThrowsAsync<RuntimeProtocolException>(() => client.GetSystemStatusAsync());

        Assert.Equal(["/api/handshake"], paths);
    }

    [Fact]
    public async Task Runtime_client_prefers_host_connection_and_reloads_rotated_credential()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-cli-connection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtimeUrl = new Uri("http://runtime.test/");
            var hostPath = Path.Combine(root, "host.json");
            var runtimePath = Path.Combine(root, "runtime.json");
            RuntimeConnectionInfoStore.Save(new RuntimeConnectionInfo(runtimeUrl, "direct-token"), runtimePath);
            RuntimeConnectionInfoStore.Save(new RuntimeConnectionInfo(runtimeUrl, "host-token-1"), hostPath);
            var authorizations = new List<string?>();
            var handler = new DelegateHandler(request =>
            {
                authorizations.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(request.RequestUri!.AbsolutePath switch
                {
                    "/api/handshake" => Json(CreateHandshake()),
                    "/api/v1/system" => Json(new SystemStatusResponse(
                        "Sunder.Runtime.Host",
                        "1.0.0",
                        true,
                        DateTimeOffset.UtcNow)),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                });
            });
            using var management = new RuntimeManagementClient(
                () => HostConnectionInfoStore.LoadPreferredFor(runtimeUrl, hostPath, runtimePath),
                handler);

            await management.GetSystemStatusAsync();
            RuntimeConnectionInfoStore.Save(new RuntimeConnectionInfo(runtimeUrl, "host-token-2"), hostPath);
            await management.GetSystemStatusAsync();
            File.Delete(hostPath);
            await management.GetSystemStatusAsync();

            Assert.Equal(
                [
                    "Bearer host-token-1", "Bearer host-token-1",
                    "Bearer host-token-2", "Bearer host-token-2",
                    "Bearer direct-token", "Bearer direct-token",
                ],
                authorizations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Registry_content_reader_rejects_chunked_content_past_limit()
    {
        using var content = new StreamContent(new MemoryStream("123456789"u8.ToArray()));

        await Assert.ThrowsAsync<InvalidDataException>(() => CliHttpContentReader.ReadJsonAsync<object>(
            content,
            maxBytes: 8,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            CancellationToken.None));
    }

    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };

    private static RuntimeHandshakeResponse CreateHandshake()
        => new(
            RuntimeProtocol.Identity,
            RuntimeProtocol.CurrentRevision,
            RuntimeProtocol.MinimumSupportedRevision,
            RuntimeProtocol.MaximumSupportedRevision,
            Guid.NewGuid(),
            [
                RuntimeProtocolFeatures.VersionedApiV1,
                RuntimeProtocolFeatures.AtomicPackageSnapshotV1,
                RuntimeProtocolFeatures.PackageStageStatusV1,
            ],
            new RuntimeProductVersionDiagnostics("Sunder.Runtime.Host", "Development", "Development"));

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await send(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
