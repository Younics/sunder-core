using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Sunder.Cli;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;

namespace Sunder.Cli.Tests;

public sealed class RegistryPublishCredentialTests
{
    private const string PublishToken = "sunder_pub_v1_abcdefghijklmnopqrstuvwxyz0123456789";

    [Fact]
    public async Task Environment_source_reads_once_and_clears_process_source()
    {
        string? stored = PublishToken;
        var reader = new RegistryPublishCredentialReader(
            TextReader.Null,
            () => false,
            _ => stored,
            (_, value) => stored = value);

        using var credential = await reader.ReadAsync(
            RegistryCredentialSource.Environment,
            CancellationToken.None);

        Assert.Equal(PublishToken, credential.Value);
        Assert.Null(stored);
        Assert.Equal("[REDACTED]", credential.ToString());
    }

    [Fact]
    public async Task Standard_input_accepts_one_bounded_publish_token_with_final_newline()
    {
        var reader = new RegistryPublishCredentialReader(
            new StringReader(PublishToken + "\r\n"),
            () => true);

        using var credential = await reader.ReadAsync(
            RegistryCredentialSource.StandardInput,
            CancellationToken.None);

        Assert.Equal(PublishToken, credential.Value);
    }

    [Fact]
    public async Task Standard_input_requires_redirection()
    {
        var reader = new RegistryPublishCredentialReader(
            new StringReader(PublishToken),
            () => false);

        await Assert.ThrowsAsync<CliUsageException>(async () =>
            await reader.ReadAsync(RegistryCredentialSource.StandardInput, CancellationToken.None));
    }

    [Theory]
    [InlineData("sunder_cli_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("plain-secret")]
    [InlineData("sunder_pub_v1_contains whitespace")]
    public void Automation_source_rejects_non_publish_credentials(string value)
        => Assert.Throws<CliAuthenticationException>(() => new RegistryPublishCredential(value));

    [Fact]
    public void Output_redacts_bare_human_and_publish_credentials()
    {
        Assert.DoesNotContain("sunder_pub_v1_", CliText.Sanitize(PublishToken), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sunder_cli_",
            CliText.Sanitize("sunder_cli_abcdefghijklmnopqrstuvwxyz0123456789"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_package_publish_sends_scoped_credential_only_to_exact_registry_origin()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-cli-publish-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, "demo.sunderpkg");
        await WritePackageArchiveAsync(archivePath);
        try
        {
            HttpMethod? method = null;
            Uri? uri = null;
            AuthenticationHeaderValue? authorization = null;
            string? expectedResourceId = null;
            string? setLatestHeader = null;
            string? body = null;
            using var client = new RegistryClient(
                new Uri("https://registry.test/"),
                new AsyncDelegateHandler(async request =>
                {
                    method = request.Method;
                    uri = request.RequestUri;
                    authorization = request.Headers.Authorization;
                    expectedResourceId = request.Headers.GetValues(RegistryPublishRequestHeaders.ExpectedResourceId).Single();
                    setLatestHeader = request.Headers.GetValues(RegistryPublishRequestHeaders.SetLatest).Single();
                    body = await request.Content!.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = JsonContent.Create(new RegistryPublishPackageResponse(
                            true,
                            "demo.package",
                            "1.0.0",
                            "Published.",
                            [],
                            [])),
                    };
                }));
            using var credential = new RegistryPublishCredential(PublishToken);

            var result = await client.PublishPackageAsync(
                archivePath,
                setLatest: false,
                credential,
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(HttpMethod.Post, method);
            Assert.Equal("https://registry.test/api/v1/packages/publish", uri?.AbsoluteUri);
            Assert.Equal("Bearer", authorization?.Scheme);
            Assert.Equal(PublishToken, authorization?.Parameter);
            Assert.Equal("demo.package", expectedResourceId);
            Assert.Equal("false", setLatestHeader);
            Assert.Contains("name=package", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("name=setLatest", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("false", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Direct_stack_publish_sends_expected_resource_preflight()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-cli-publish-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, "demo.sunderstack");
        await WriteStackArchiveAsync(archivePath);
        try
        {
            string? expectedResourceId = null;
            bool hasSetLatestHeader = true;
            using var client = new RegistryClient(
                new Uri("https://registry.test/"),
                new AsyncDelegateHandler(request =>
                {
                    expectedResourceId = request.Headers.GetValues(RegistryPublishRequestHeaders.ExpectedResourceId).Single();
                    hasSetLatestHeader = request.Headers.Contains(RegistryPublishRequestHeaders.SetLatest);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = JsonContent.Create(new RegistryPublishStackResponse(
                            true,
                            "demo-stack",
                            "Published.",
                            [],
                            [])),
                    });
                }));
            using var credential = new RegistryPublishCredential(PublishToken);

            var result = await client.PublishStackAsync(archivePath, credential, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("demo-stack", expectedResourceId);
            Assert.False(hasSetLatestHeader);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CliExitCodes.Authentication, RegistryV1ErrorCodes.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, CliExitCodes.Forbidden, RegistryV1ErrorCodes.Forbidden, true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CliExitCodes.Failure, RegistryV1ErrorCodes.RequestTooLarge, true)]
    [InlineData(HttpStatusCode.TooManyRequests, CliExitCodes.Unavailable, RegistryV1ErrorCodes.RateLimited, true)]
    [InlineData(HttpStatusCode.Unauthorized, CliExitCodes.Authentication, null, false)]
    [InlineData(HttpStatusCode.Forbidden, CliExitCodes.Forbidden, null, false)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CliExitCodes.Failure, null, false)]
    [InlineData(HttpStatusCode.TooManyRequests, CliExitCodes.Unavailable, null, false)]
    public async Task Direct_automation_publish_preserves_problem_and_non_contract_http_categories(
        HttpStatusCode statusCode,
        int expectedExitCode,
        string? problemCode,
        bool problemDetails)
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-cli-publish-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, "demo.sunderpkg");
        await WritePackageArchiveAsync(archivePath);
        try
        {
            using var client = new RegistryClient(
                new Uri("https://registry.test/"),
                new AsyncDelegateHandler(request =>
                {
                    var response = new HttpResponseMessage(statusCode)
                    {
                        RequestMessage = request,
                        Content = problemDetails
                            ? JsonContent.Create(
                                new RegistryProblemDetails(
                                    "about:blank",
                                    "Registry rejected publication",
                                    (int)statusCode,
                                    "The publication request was rejected.",
                                    "/api/v1/packages/publish",
                                    problemCode!,
                                    "trace-42",
                                    "registry-correlation-42"),
                                mediaType: new MediaTypeHeaderValue("application/problem+json"))
                            : JsonContent.Create(new { success = false, message = "Non-contract response." }),
                    };
                    if (!problemDetails)
                    {
                        response.Headers.TryAddWithoutValidation("X-Correlation-ID", "registry-correlation-42");
                    }
                    return Task.FromResult(response);
                }));
            using var credential = new RegistryPublishCredential(PublishToken);

            var exception = await Assert.ThrowsAsync<CliHttpException>(() => client.PublishPackageAsync(
                archivePath,
                setLatest: true,
                credential,
                CancellationToken.None));
            var error = CliErrorMapper.Describe(exception);

            Assert.Equal(statusCode, exception.StatusCode);
            Assert.Equal(problemCode ?? "registry.request.failed", error.Code);
            Assert.Equal("registry-correlation-42", error.CorrelationId);
            Assert.Equal(expectedExitCode, error.ExitCode);
            Assert.DoesNotContain("registry-correlation-42", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Direct_automation_publish_rejects_success_payload_with_missing_collections()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-cli-publish-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, "demo.sunderpkg");
        await WritePackageArchiveAsync(archivePath);
        try
        {
            using var client = new RegistryClient(
                new Uri("https://registry.test/"),
                new AsyncDelegateHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = JsonContent.Create(new
                    {
                        success = true,
                        packageId = "demo.package",
                        version = "1.0.0",
                        message = "Published.",
                    }),
                })));
            using var credential = new RegistryPublishCredential(PublishToken);

            await Assert.ThrowsAsync<InvalidDataException>(() => client.PublishPackageAsync(
                archivePath,
                setLatest: true,
                credential,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WritePackageArchiveAsync(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry(SunderPackageFormat.ManifestPath);
        await using var stream = manifest.Open();
        await stream.WriteAsync("""{"id":"demo.package"}"""u8.ToArray());
    }

    private static async Task WriteStackArchiveAsync(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry(SunderStackFormat.ManifestPath);
        await using var stream = manifest.Open();
        await stream.WriteAsync("""{"stackId":"demo-stack"}"""u8.ToArray());
    }

    private sealed class AsyncDelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => send(request);
    }
}
