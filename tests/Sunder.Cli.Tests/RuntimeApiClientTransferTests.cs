using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Cli;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

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
            Assert.Equal(["/api/v1/packages/installed", "/api/v1/uploads/packages", "/api/v1/packages/store/stage", "/api/v1/packages/store/stage/stage-1/commit"], requests.Select(item => item.Path));
            Assert.All(requests, request => Assert.Equal("Bearer runtime-secret", request.Authorization));
            Assert.Equal("archive", Encoding.UTF8.GetString(requests[1].Body));
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
            await client.DownloadStackAsync(new RegistryStackArtifact(hash, bytes.Length, "/artifact"), "stack-1", output, default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
