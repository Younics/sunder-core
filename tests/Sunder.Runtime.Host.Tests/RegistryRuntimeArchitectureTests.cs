using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RegistryRuntimeArchitectureTests
{
    [Fact]
    public async Task CredentialStore_EncryptsCredentialsAndIsolatesNormalizedOrigins()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new RuntimePackagePaths(root);
            var store = new RegistryCredentialStore(paths);
            var first = RegistryOrigin.Normalize("https://REGISTRY.example/api");
            var second = RegistryOrigin.Normalize("https://other.example/api/");
            const string secret = "credential-value-that-must-not-be-plaintext";

            await store.SetAsync(first, new RegistryCredential(secret, "user-1", DateTimeOffset.UtcNow.AddHours(1), "owner", "Owner", null, null, false));

            var persisted = await File.ReadAllTextAsync(paths.RegistryCredentialFilePath);
            Assert.DoesNotContain(secret, persisted, StringComparison.Ordinal);
            Assert.Equal(secret, (await store.GetAsync(first))?.AccessToken);
            Assert.Null(await store.GetAsync(second));
            Assert.Contains(Path.Combine("credentials", "registry"), paths.RegistryCredentialFilePath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrowserAuthorization_ValidatesStateAndPkceWithoutDisclosingCredential()
    {
        var root = CreateTempDirectory();
        try
        {
            const string secret = "runtime-only-registry-credential";
            var handler = new DelegateHandler(request =>
            {
                if (request.RequestUri?.AbsolutePath.EndsWith("/api/v1/cli-auth/token", StringComparison.Ordinal) == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new RegistryCliTokenResponse(true, secret, "user-1", DateTimeOffset.UtcNow.AddHours(1), [])),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryCurrentUserResponse("user-1", "Owner", null, "owner")),
                };
            });
            var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(handler),
                new RegistryCredentialStore(new RuntimePackagePaths(root)),
                NullLogger<RegistryAuthCoordinator>.Instance);

            var start = coordinator.Start(new RuntimeRegistryAuthStartRequest("https://registry.example/", DisplayName: "Tests"));
            var launch = new Uri(start.LaunchUrl);
            var query = ParseQuery(launch.Query);
            Assert.True(query.TryGetValue("code_challenge", out var challenge));
            Assert.True(challenge.Length >= 43);
            Assert.DoesNotContain("code_verifier", launch.Query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, start.ToString(), StringComparison.Ordinal);

            var callback = new Uri(query["redirect_uri"]);
            using var callbackClient = new HttpClient();
            using var callbackResponse = await callbackClient.GetAsync(
                new UriBuilder(callback) { Query = $"code=test-code&state={Uri.EscapeDataString(query["state"])}" }.Uri);
            callbackResponse.EnsureSuccessStatusCode();

            RuntimeRegistryAuthSessionStatus? status = null;
            for (var attempt = 0; attempt < 50 && status?.State != RuntimeRegistryAuthSessionState.Succeeded; attempt++)
            {
                status = coordinator.GetSession(start.SessionId);
                await Task.Delay(20);
            }

            Assert.Equal(RuntimeRegistryAuthSessionState.Succeeded, status?.State);
            Assert.Equal("owner", status?.User?.Username);
            Assert.DoesNotContain(secret, status?.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://registry.example/")]
    [InlineData("https://user:secret@registry.example/")]
    [InlineData("https://registry.example/?share=credential")]
    public void RegistryOrigin_RejectsUntrustedOrCredentialSharingOrigins(string value)
        => Assert.Throws<ArgumentException>(() => RegistryOrigin.Normalize(value));

    [Fact]
    public void PublicRegistryRuntimeContracts_DoNotExposeCredentialProperties()
    {
        var names = typeof(RuntimeRegistryAuthStartResponse).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(RuntimeRegistryAuthStartResponse).Namespace && type.Name.Contains("Registry", StringComparison.Ordinal))
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                                               || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                                               || name.Contains("Verifier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthenticatedOperation_DropsRejectedCredentialAndSucceedsAfterSignInRetry()
    {
        var root = CreateTempDirectory();
        try
        {
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await store.SetAsync(origin, new RegistryCredential("rejected", "user-1", DateTimeOffset.UtcNow.AddHours(1), null, null, null, null, false));
            var calls = 0;
            var handler = new DelegateHandler(request =>
            {
                calls++;
                return calls == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new RegistryPackageStarResponse(true, "Starred.", null, [])),
                    };
            });
            var operations = new RegistryAuthenticatedOperations(new FakeHttpClientFactory(handler), store);

            var rejected = await operations.SetPackageStarAsync(new RuntimeRegistryStarRequest(origin.AbsoluteUri, "agent", true), CancellationToken.None);
            Assert.True(rejected.Forbidden);
            Assert.Null(await store.GetAsync(origin));

            await store.SetAsync(origin, new RegistryCredential("replacement", "user-1", DateTimeOffset.UtcNow.AddHours(1), null, null, null, null, false));
            var retried = await operations.SetPackageStarAsync(new RuntimeRegistryStarRequest(origin.AbsoluteUri, "agent", true), CancellationToken.None);

            Assert.True(retried.Success);
            Assert.Equal(2, calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeUpload_CancellationRemovesPartialArtifact()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new RuntimePackagePaths(root);
            var store = new RuntimeContentTransferStore(paths);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateUploadAsync(
                RuntimeUploadKind.Package,
                new CancellingStream(),
                null,
                null,
                "cancelled.sunderpkg",
                "application/vnd.sunder.package",
                generation: 1,
                CancellationToken.None));

            Assert.Empty(Directory.Exists(paths.TransferRootPath)
                ? Directory.EnumerateFiles(paths.TransferRootPath)
                : []);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppAndCliSources_DoNotContainRegistryCredentialHandling()
    {
        var root = LocateRepositoryRoot();
        var forbidden = new[]
        {
            "SunderAuthStore",
            "RegistryAuthToken",
            "SUNDER_REGISTRY_TOKEN",
            "CliBrowserAuthFlow",
            "RegistryBrowserAuthFlow",
        };
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Host", "Sunder.App"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "Host", "Sunder.Cli"), "*.cs", SearchOption.AllDirectories));

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var value in forbidden)
            {
                Assert.DoesNotContain(value, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AppAndCliPackageChanges_AreThinClientsOfTheSameRuntimeOrchestration()
    {
        var root = LocateRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "Host", "Sunder.App", "Services", "RegistryPackageInstallService.cs"));
        var cliDirectory = Path.Combine(root, "src", "Host", "Sunder.Cli");
        var cli = string.Join('\n', Directory.EnumerateFiles(cliDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        Assert.Contains("InstallRegistryPackageAsync", app, StringComparison.Ordinal);
        Assert.Contains("InstallRegistryPackageAsync", cli, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadArtifactAsync", app, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadArtifactAsync", cli, StringComparison.Ordinal);
        Assert.DoesNotContain("StagePackageStoreChangesAsync", app, StringComparison.Ordinal);
        Assert.DoesNotContain("StagePackageStoreChangesAsync", cli, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]), StringComparer.OrdinalIgnoreCase);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-registry-runtime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Core repository root.");
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class CancellingStream : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) throw new OperationCanceledException();
            _read = true;
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
    }
}
