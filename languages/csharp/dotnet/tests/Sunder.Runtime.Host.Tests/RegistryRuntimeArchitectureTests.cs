using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed partial class RegistryRuntimeArchitectureTests
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
    public async Task CredentialStore_CasRevisionIsOriginScopedAndRejectsStaleSameTokenSnapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            var first = RegistryOrigin.Normalize("https://first.example/");
            var second = RegistryOrigin.Normalize("https://second.example/");
            var initial = new RegistryCredential(
                "shared-token-value",
                "user-1",
                DateTimeOffset.UtcNow.AddHours(1),
                "initial",
                "Initial",
                null,
                null,
                false);
            await store.SetAsync(first, initial);
            var firstSnapshot = Assert.IsType<RegistryCredentialSnapshot>(await store.GetSnapshotAsync(first));

            await store.SetAsync(second, initial with { Username = "second" });
            Assert.True(await store.TryUpdateAsync(
                first,
                firstSnapshot,
                initial with { Username = "first-refreshed" }));

            var staleSnapshot = Assert.IsType<RegistryCredentialSnapshot>(await store.GetSnapshotAsync(first));
            await store.SetAsync(first, initial with { Username = "new-login" });
            Assert.False(await store.TryUpdateAsync(
                first,
                staleSnapshot,
                initial with { Username = "stale-refresh" }));
            Assert.Equal("new-login", (await store.GetAsync(first))?.Username);
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
            await using var coordinator = new RegistryAuthCoordinator(
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

    [Fact]
    public async Task RegistryLogout_InvalidatesPendingBrowserSessionsForOnlyTheNormalizedOrigin()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await using var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))),
                store,
                NullLogger<RegistryAuthCoordinator>.Instance);
            var matching = coordinator.Start(new RuntimeRegistryAuthStartRequest("https://REGISTRY.example/api"));
            var other = coordinator.Start(new RuntimeRegistryAuthStartRequest("https://other.example/"));

            var result = await coordinator.LogoutAsync("https://registry.example/api/", CancellationToken.None);

            var matchingStatus = coordinator.GetSession(matching.SessionId);
            Assert.False(result.IsSignedIn);
            Assert.Equal(RuntimeRegistryAuthSessionState.Failed, matchingStatus?.State);
            Assert.Equal(RuntimeRegistryErrorCode.Cancelled, matchingStatus?.ErrorCode);
            Assert.Contains("logout", matchingStatus?.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(RuntimeRegistryAuthSessionState.Pending, coordinator.GetSession(other.SessionId)?.State);
            Assert.Null(await store.GetAsync(RegistryOrigin.Normalize(matching.RegistryOrigin)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryLogout_WaitsForInvalidatedExchangeAndRevokesIssuedCredential()
    {
        var root = CreateTempDirectory();
        try
        {
            const string issuedCredential = "credential-issued-after-logout";
            var exchangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseExchange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? revokedCredential = null;
            var handler = new AsyncDelegateHandler(async (request, _) =>
            {
                if (request.Method == HttpMethod.Post
                    && request.RequestUri?.AbsolutePath.EndsWith("/api/v1/cli-auth/token", StringComparison.Ordinal) == true)
                {
                    exchangeStarted.TrySetResult();
                    await releaseExchange.Task;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new RegistryCliTokenResponse(
                            true,
                            issuedCredential,
                            "user-1",
                            DateTimeOffset.UtcNow.AddHours(1),
                            [])),
                    };
                }
                if (request.Method == HttpMethod.Delete)
                {
                    revokedCredential = request.Headers.Authorization?.Parameter;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryCurrentUserResponse("user-1", "Owner", null, "owner")),
                };
            });
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await using var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(handler),
                store,
                NullLogger<RegistryAuthCoordinator>.Instance);
            var start = coordinator.Start(new RuntimeRegistryAuthStartRequest("https://registry.example/"));
            var launchQuery = ParseQuery(new Uri(start.LaunchUrl).Query);
            var callback = new Uri(launchQuery["redirect_uri"]);
            using var callbackClient = new HttpClient();
            var callbackTask = callbackClient.GetAsync(new UriBuilder(callback)
            {
                Query = $"code=test-code&state={Uri.EscapeDataString(launchQuery["state"])}",
            }.Uri);
            await exchangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var logoutTask = coordinator.LogoutAsync(start.RegistryOrigin, CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => coordinator.GetSession(start.SessionId)?.ErrorCode == RuntimeRegistryErrorCode.Cancelled);
                Assert.False(logoutTask.IsCompleted);
                Assert.Throws<InvalidOperationException>(() =>
                    coordinator.Start(new RuntimeRegistryAuthStartRequest(start.RegistryOrigin)));
            }
            finally
            {
                releaseExchange.TrySetResult();
            }
            var result = await logoutTask.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                using var ignored = await callbackTask;
            }
            catch (HttpRequestException)
            {
                // Logout closes the loopback callback without reporting browser success.
            }

            Assert.False(result.IsSignedIn);
            Assert.Equal(issuedCredential, revokedCredential);
            Assert.Equal(RuntimeRegistryAuthSessionState.Failed, coordinator.GetSession(start.SessionId)?.State);
            Assert.Null(await store.GetAsync(RegistryOrigin.Normalize(start.RegistryOrigin)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrowserAuthorization_SessionsAreCappedExpireAndAreRemovedAfterRetention()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))),
                new RegistryCredentialStore(new RuntimePackagePaths(root)),
                NullLogger<RegistryAuthCoordinator>.Instance,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(1),
                maxSessions: 2);

            var first = coordinator.Start(new RuntimeRegistryAuthStartRequest("https://registry.example/"));
            var second = coordinator.Start(new RuntimeRegistryAuthStartRequest("https://registry.example/"));
            Assert.Throws<InvalidOperationException>(() => coordinator.Start(new RuntimeRegistryAuthStartRequest("https://registry.example/")));

            var expiredAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(6);
            coordinator.SweepExpired(expiredAt);
            Assert.Equal(RuntimeRegistryAuthSessionState.Expired, coordinator.GetSession(first.SessionId)?.State);
            Assert.Equal(RuntimeRegistryAuthSessionState.Expired, coordinator.GetSession(second.SessionId)?.State);

            await WaitUntilAsync(() =>
            {
                coordinator.SweepExpired(expiredAt + TimeSpan.FromMinutes(2));
                return coordinator.SessionCount == 0;
            });
            Assert.NotNull(coordinator.Start(new RuntimeRegistryAuthStartRequest("https://registry.example/")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrowserAuthorization_OversizedRawRequestFailsWithoutLeakingSession()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))),
                new RegistryCredentialStore(new RuntimePackagePaths(root)),
                NullLogger<RegistryAuthCoordinator>.Instance,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(1),
                maxSessions: 1);
            var start = coordinator.Start(new RuntimeRegistryAuthStartRequest("https://registry.example/"));
            var callback = new Uri(ParseQuery(new Uri(start.LaunchUrl).Query)["redirect_uri"]);
            using var client = new TcpClient();
            await client.ConnectAsync(callback.Host, callback.Port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /{new string('x', new RuntimeAuthPolicyOptions().MaxCallbackRequestLineBytes)} HTTP/1.1\r\n\r\n"));

            await WaitUntilAsync(() => coordinator.GetSession(start.SessionId)?.State == RuntimeRegistryAuthSessionState.Failed);
            await WaitUntilAsync(() =>
            {
                coordinator.SweepExpired(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));
                return coordinator.SessionCount == 0;
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BrowserAuthorization_HostShutdownClosesListenersAndAwaitsSessions()
    {
        var root = CreateTempDirectory();
        try
        {
            await using var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))),
                new RegistryCredentialStore(new RuntimePackagePaths(root)),
                NullLogger<RegistryAuthCoordinator>.Instance);
            await coordinator.StartAsync(CancellationToken.None);
            var start = coordinator.Start(new RuntimeRegistryAuthStartRequest("https://registry.example/"));
            var callback = new Uri(ParseQuery(new Uri(start.LaunchUrl).Query)["redirect_uri"]);

            await coordinator.StopAsync(CancellationToken.None);

            Assert.Equal(0, coordinator.SessionCount);
            using var listener = new TcpListener(IPAddress.Loopback, callback.Port);
            listener.Start();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedManagementFetchesPolicyAndDoesNotDispatchWithoutIt(bool missingPolicy)
    {
        var root = CreateTempDirectory();
        try
        {
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await store.SetAsync(origin, new RegistryCredential("credential", "user-1", DateTimeOffset.UtcNow.AddHours(1), null, null, null, null, false));
            var calls = 0;
            var handler = new DelegateHandler(request =>
            {
                calls++; Assert.Equal("credential", request.Headers.Authorization!.Parameter);
                if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new RegistryPackageDistTagsResponse("example.package", []) { PackagePolicy = missingPolicy ? null : new(42, new string('a', 64)) }) };
                Assert.Equal("42", Assert.Single(request.Headers.GetValues(RegistryPolicyMutationHeaders.PackagePolicyRevision)));
                Assert.Matches("^[0-9a-f]{32}$", Assert.Single(request.Headers.GetValues(RegistryPolicyMutationHeaders.IdempotencyKey)));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new RegistryPackageManagementOperationResponse(true, "Updated.", [])) };
            });
            var operations = new RegistryAuthenticatedOperations(new FakeHttpClientFactory(handler), store);
            Task<RegistryPackageManagementOperationResponse> Execute() => operations.SetYankAsync(new RuntimeRegistryYankRequest(origin.AbsoluteUri, "example.package", "1.0.0", true), CancellationToken.None);
            if (missingPolicy) await Assert.ThrowsAsync<InvalidDataException>(Execute);
            else Assert.True((await Execute()).Success);
            Assert.Equal(missingPolicy ? 1 : 2, calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AuthenticatedOperation_PreservesValidCredentialWhenAuthorizationIsForbidden()
    {
        var root = CreateTempDirectory();
        try
        {
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await store.SetAsync(origin, new RegistryCredential(
                "valid-but-forbidden",
                "user-1",
                DateTimeOffset.UtcNow.AddHours(1),
                null,
                null,
                null,
                null,
                false));
            var operations = new RegistryAuthenticatedOperations(
                new FakeHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))),
                store);

            var result = await operations.SetPackageStarAsync(
                new RuntimeRegistryStarRequest(origin.AbsoluteUri, "other-owner.package", true),
                CancellationToken.None);

            Assert.True(result.Forbidden);
            Assert.NotNull(await store.GetAsync(origin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryLogout_RevokesCurrentServerTokenBeforeDeletingLocalCredential()
    {
        var root = CreateTempDirectory();
        try
        {
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await store.SetAsync(origin, new RegistryCredential(
                "interactive-token",
                "user-1",
                DateTimeOffset.UtcNow.AddHours(1),
                null,
                null,
                null,
                null,
                false));
            HttpRequestMessage? observed = null;
            var handler = new DelegateHandler(request =>
            {
                observed = request;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            await using var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(handler),
                store,
                NullLogger<RegistryAuthCoordinator>.Instance);

            var result = await coordinator.LogoutAsync(origin.AbsoluteUri, CancellationToken.None);

            Assert.False(result.IsSignedIn);
            Assert.Equal(HttpMethod.Delete, observed?.Method);
            Assert.Equal("/api/v1/cli-auth/token", observed?.RequestUri?.AbsolutePath);
            Assert.Equal("interactive-token", observed?.Headers.Authorization?.Parameter);
            Assert.Null(await store.GetAsync(origin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryLogout_WhenServerRevocationFails_StillDeletesLocalCredential()
    {
        var root = CreateTempDirectory();
        try
        {
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await store.SetAsync(origin, new RegistryCredential(
                "interactive-token",
                "user-1",
                DateTimeOffset.UtcNow.AddHours(1),
                null,
                null,
                null,
                null,
                false));
            await using var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(new DelegateHandler(_ =>
                    new HttpResponseMessage(HttpStatusCode.InternalServerError))),
                store,
                NullLogger<RegistryAuthCoordinator>.Instance);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                coordinator.LogoutAsync(origin.AbsoluteUri, CancellationToken.None));

            Assert.Null(await store.GetAsync(origin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryStatusRefresh_CannotRestoreCredentialAfterConcurrentLogout()
    {
        var root = CreateTempDirectory();
        try
        {
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await store.SetAsync(origin, new RegistryCredential(
                "interactive-token",
                "user-1",
                DateTimeOffset.UtcNow.AddHours(1),
                null,
                null,
                null,
                null,
                false));
            var profileStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new AsyncDelegateHandler(async (request, _) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    profileStarted.TrySetResult();
                    await releaseProfile.Task;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new RegistryCurrentUserResponse(
                            "user-1",
                            "Owner",
                            null,
                            "owner")),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            await using var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(handler),
                store,
                NullLogger<RegistryAuthCoordinator>.Instance);
            var statusTask = coordinator.GetStatusAsync(origin.AbsoluteUri, CancellationToken.None);
            await profileStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var logoutTask = coordinator.LogoutAsync(origin.AbsoluteUri, CancellationToken.None);
            try
            {
                Assert.False((await logoutTask.WaitAsync(TimeSpan.FromSeconds(5))).IsSignedIn);
                Assert.Null(await store.GetAsync(origin));
            }
            finally
            {
                releaseProfile.TrySetResult();
            }

            Assert.False((await statusTask.WaitAsync(TimeSpan.FromSeconds(5))).IsSignedIn);
            Assert.Null(await store.GetAsync(origin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryLogin_WinsAgainstPendingOldStatusRefresh()
    {
        var root = CreateTempDirectory();
        try
        {
            const string oldToken = "old-interactive-token";
            const string newToken = "new-interactive-token";
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await store.SetAsync(origin, new RegistryCredential(
                oldToken,
                "old-user",
                DateTimeOffset.UtcNow.AddHours(1),
                "old-owner",
                "Old Owner",
                null,
                null,
                false));
            var oldProfileStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOldProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new AsyncDelegateHandler(async (request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new RegistryCliTokenResponse(
                            true,
                            newToken,
                            "new-user",
                            DateTimeOffset.UtcNow.AddHours(1),
                            [])),
                    };
                }

                if (request.Headers.Authorization?.Parameter == oldToken)
                {
                    oldProfileStarted.TrySetResult();
                    await releaseOldProfile.Task;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new RegistryCurrentUserResponse(
                            "old-user",
                            "Old Owner",
                            null,
                            "old-owner")),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryCurrentUserResponse(
                        "new-user",
                        "New Owner",
                        null,
                        "new-owner")),
                };
            });
            await using var coordinator = new RegistryAuthCoordinator(
                new FakeHttpClientFactory(handler),
                store,
                NullLogger<RegistryAuthCoordinator>.Instance);
            var statusTask = coordinator.GetStatusAsync(origin.AbsoluteUri, CancellationToken.None);
            await oldProfileStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var login = coordinator.Start(new RuntimeRegistryAuthStartRequest(origin.AbsoluteUri));
            var launchQuery = ParseQuery(new Uri(login.LaunchUrl).Query);
            var callback = new Uri(launchQuery["redirect_uri"]);
            using var callbackClient = new HttpClient();
            var callbackTask = callbackClient.GetAsync(new UriBuilder(callback)
            {
                Query = $"code=test-code&state={Uri.EscapeDataString(launchQuery["state"])}",
            }.Uri);

            try
            {
                await WaitUntilAsync(() =>
                    coordinator.GetSession(login.SessionId)?.State == RuntimeRegistryAuthSessionState.Succeeded);
                Assert.Equal(newToken, (await store.GetAsync(origin))?.AccessToken);
            }
            finally
            {
                releaseOldProfile.TrySetResult();
            }

            using var callbackResponse = await callbackTask.WaitAsync(TimeSpan.FromSeconds(5));
            callbackResponse.EnsureSuccessStatusCode();
            var status = await statusTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(status.IsSignedIn);
            Assert.Equal("new-owner", status.User?.Username);
            Assert.Equal(newToken, (await store.GetAsync(origin))?.AccessToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AuthenticatedOperation_OldUnauthorizedResponseCannotDeleteNewLoginCredential()
    {
        var root = CreateTempDirectory();
        try
        {
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await store.SetAsync(origin, new RegistryCredential(
                "old-token",
                "old-user",
                DateTimeOffset.UtcNow.AddHours(1),
                null,
                null,
                null,
                null,
                false));
            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operations = new RegistryAuthenticatedOperations(
                new FakeHttpClientFactory(new AsyncDelegateHandler(async (_, _) =>
                {
                    requestStarted.TrySetResult();
                    await releaseResponse.Task;
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                })),
                store);
            var operation = operations.SetPackageStarAsync(
                new RuntimeRegistryStarRequest(origin.AbsoluteUri, "agent", true),
                CancellationToken.None);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await store.SetAsync(origin, new RegistryCredential(
                    "new-token",
                    "new-user",
                    DateTimeOffset.UtcNow.AddHours(1),
                    "new-owner",
                    "New Owner",
                    null,
                    null,
                    false));
            }
            finally
            {
                releaseResponse.TrySetResult();
            }

            var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Forbidden);
            Assert.Equal("new-token", (await store.GetAsync(origin))?.AccessToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AuthenticatedOperation_DoesNotDeserializeTypedFailureResponse()
    {
        var root = CreateTempDirectory();
        try
        {
            var origin = RegistryOrigin.Normalize("https://registry.example/");
            var store = new RegistryCredentialStore(new RuntimePackagePaths(root));
            await store.SetAsync(origin, new RegistryCredential("credential", "user-1", DateTimeOffset.UtcNow.AddHours(1), null, null, null, null, false));
            var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = JsonContent.Create(new RegistryPackageStarResponse(true, "Starred.", null, [])),
            });
            var operations = new RegistryAuthenticatedOperations(new FakeHttpClientFactory(handler), store);

            var result = await operations.SetPackageStarAsync(
                new RuntimeRegistryStarRequest(origin.AbsoluteUri, "agent", true),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(["Registry request failed with HTTP 500."], result.Errors);
            Assert.NotNull(await store.GetAsync(origin));
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
    public async Task RegistryHttpClient_RejectsOversizedJsonBeforeDeserialization()
    {
        var body = new TrackingReadStream("123456789"u8.ToArray());
        var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });
        var client = new RegistryHttpClient(
            new FakeHttpClientFactory(handler),
            new RuntimeTransportPolicyOptions { MaxRegistryJsonBytes = 4 },
            TimeProvider.System);
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://registry.example/test"),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadJsonAsync<object>(response, CancellationToken.None));
        Assert.Equal(5, body.BytesRead);
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task RegistryHttpClient_AcceptsJsonAtExactLimit()
    {
        var body = new TrackingReadStream("\"ok\""u8.ToArray());
        var client = new RegistryHttpClient(
            new FakeHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
            })),
            new RuntimeTransportPolicyOptions { MaxRegistryJsonBytes = 4 },
            TimeProvider.System);
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://registry.example/test"),
            CancellationToken.None);

        var result = await client.ReadJsonAsync<string>(response, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(4, body.BytesRead);
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task RegistryHttpClient_MapsProblemDetailAndPropagatesCallerCancellation()
    {
        var problemHandler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new RegistryProblemDetails(
                "about:blank", "Conflict", 409, "Package version already exists.", "/test",
                RegistryV1ErrorCodes.Conflict, "trace", "correlation")),
        });
        var problemClient = new RegistryHttpClient(
            new FakeHttpClientFactory(problemHandler),
            new RuntimeTransportPolicyOptions(),
            TimeProvider.System);
        using var response = await problemClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://registry.example/test"),
            CancellationToken.None);
        Assert.Equal("Package version already exists.", await problemClient.ReadErrorAsync(response, CancellationToken.None));

        var cancellationClient = new RegistryHttpClient(
            new FakeHttpClientFactory(new AsyncDelegateHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })),
            new RuntimeTransportPolicyOptions(),
            TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var pending = cancellationClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://registry.example/test"),
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task RegistryHttpClient_BodyTimeoutAndCallerCancellationDisposeStreams()
    {
        var timedOutBody = new CancellationBlockingResponseStream();
        var timeoutClient = new RegistryHttpClient(
            new FakeHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(timedOutBody),
            })),
            new RuntimeTransportPolicyOptions { RegistryRequestTimeout = TimeSpan.FromMilliseconds(50) },
            TimeProvider.System);
        using (var response = await timeoutClient.SendAsync(
                   new HttpRequestMessage(HttpMethod.Get, "https://registry.example/test"),
                   CancellationToken.None))
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                timeoutClient.ReadJsonAsync<object>(response, CancellationToken.None));
        }
        Assert.True(timedOutBody.IsDisposed);

        var cancelledBody = new CancellationBlockingResponseStream();
        var cancellationClient = new RegistryHttpClient(
            new FakeHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(cancelledBody),
            })),
            new RuntimeTransportPolicyOptions(),
            TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        using var cancelledResponse = await cancellationClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://registry.example/test"),
            cancellation.Token);
        var read = cancellationClient.ReadJsonAsync<object>(cancelledResponse, cancellation.Token);
        await cancelledBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(cancelledBody.IsDisposed);
    }

    [Fact]
    public async Task RegistryHttpClient_RejectsResponseFromChangedOrigin()
    {
        var client = new RegistryHttpClient(
            new FakeHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.example/test"),
                Content = JsonContent.Create(new { value = true }),
            })),
            new RuntimeTransportPolicyOptions(),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://registry.example/test"),
            CancellationToken.None));

        Assert.Contains("untrusted origin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistryPackagePlan_WhenRegistryIsUnreachable_ReturnsFailedPlan(bool timeout)
    {
        var root = CreateTempDirectory();
        try
        {
            HttpMessageHandler handler = timeout
                ? new AsyncDelegateHandler(async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                })
                : new AsyncDelegateHandler((_, _) => Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Connection refused.")));
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(
                new RuntimePackagePaths(root),
                new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddSingleton(new RuntimeTransportPolicyOptions
            {
                RegistryRequestTimeout = TimeSpan.FromMilliseconds(20),
            });
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var orchestrator = provider.GetRequiredService<RegistryPackageChangeOrchestrator>();
            var request = new RuntimeRegistryPackageBatchRequest(
                "http://localhost:5288/",
                [new RuntimeRegistryPackageChangeRequest("agent", null, [], "latest")]);
            var plan = await orchestrator.ResolveAsync(request, CancellationToken.None);

            Assert.False(plan.Success);
            Assert.Empty(plan.Items);
            Assert.Equal(["Registry is not reachable at http://localhost:5288/."], plan.Errors);

            var execution = await orchestrator.ExecuteAsync(request, CancellationToken.None);
            Assert.False(execution.Success);
            Assert.Equal(RuntimeRegistryErrorCode.RegistryUnavailable, execution.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(ExecutionRuntimeErrorCodes.Revoked)]
    [InlineData(ExecutionRuntimeErrorCodes.Retired)]
    [InlineData(ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied)]
    public async Task RegistryPackagePlan_StartupUpdateCheckHardDenialCommitsEvidenceAndFaultsActivePackage(
        string conflictCode)
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string requestOrigin = "https://REGISTRY.example";
            const string packageId = "test.runtime.plan.denied";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var installedStore = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await installedStore.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(installed, RegistryPackageActionKinds.Keep);
            var targetBinding = Assert.Single(item.RuntimeBindings);
            var binding = Assert.Single(installed.ExecutionRuntimeBindings);
            var receipt = CreateRuntimeReceipt(binding, now);
            var authoritativeLifecycle = conflictCode switch
            {
                ExecutionRuntimeErrorCodes.Revoked => RevokedRuntimeLifecycle(now),
                ExecutionRuntimeErrorCodes.Retired => RetiredRuntimeLifecycle(now),
                _ => SupportedRuntimeLifecycle(now),
            };
            var planRequests = 0;
            var releaseRequests = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    releaseRequests++;
                    return RuntimeReleaseResponse(
                        receipt,
                        authoritativeLifecycle,
                        runtimePolicyRevision: 2,
                        now);
                }

                planRequests++;
                return RuntimeConflictPlanResponse(
                    new RegistryExecutionRuntimeResolutionConflict(
                        conflictCode,
                        conflictCode switch
                        {
                            ExecutionRuntimeErrorCodes.Revoked =>
                                "The currently bound execution runtime is revoked.",
                            ExecutionRuntimeErrorCodes.Retired =>
                                "The currently bound execution runtime is retired and activation-blocked.",
                            _ => "The currently bound execution runtime is no longer trusted.",
                        },
                        targetBinding.Requirement,
                        targetBinding.Rid,
                        packageId,
                        targetBinding.Role,
                        targetBinding.Runtime),
                    now,
                    ["Registry plan warning."]);
            });
            var applicationLifetime = new TestApplicationLifetime();
            var services = new ServiceCollection();
            services.AddSingleton<IHostApplicationLifetime>(applicationLifetime);
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver(enforceLifecyclePolicy: true);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();

            await WriteRuntimeReceiptAsync(
                provider.GetRequiredService<ExecutionRuntimeStore>(),
                provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
                receipt);
            var lifecyclePolicies = provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>();
            await lifecyclePolicies.ObserveAsync([
                new ExecutionRuntimeLifecyclePolicyObservation(
                    receipt,
                    origin,
                    SupportedRuntimeLifecycle(now),
                    RuntimePolicyRevision: 1,
                    MinimumHostTrustGeneration: 1,
                    PolicyObservedAtUtc: now.AddMinutes(-1)),
            ]);
            var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            await lifecycle.InitializeAsync();
            var loaded = await lifecycle.LoadInstalledPackagesAsync();
            Assert.True(loaded.Success, loaded.Message);
            var packageSessions = provider.GetRequiredService<PackageSessionState>();
            var active = Assert.Single(packageSessions.ActiveSession.LoadedPackageMap).Value;
            var oldGeneration = active.Worker.GenerationCompletion;
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;

            var plan = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ResolveAsync(
                RuntimeActionRequest(requestOrigin, packageId),
                CancellationToken.None);

            Assert.False(plan.Success);
            var conflict = Assert.Single(plan.RuntimeConflicts);
            Assert.Equal(conflictCode, conflict.Code);
            Assert.Equal("blocked-runtime-resolution", plan.ResolutionFingerprint);
            Assert.Equal(2, plan.RuntimePolicyRevision);
            Assert.Empty(plan.Errors);
            Assert.Equal("Registry plan warning.", plan.Warnings[0]);
            Assert.Contains(plan.Warnings, warning =>
                warning.Contains("Stopped and faulted", StringComparison.Ordinal));
            Assert.Equal(1, planRequests);
            Assert.Equal(1, releaseRequests);
            Assert.False(applicationLifetime.StopRequested);
            Assert.Equal(catalogGeneration + 1, coordinator.CatalogGeneration);
            var persistedBinding = Assert.Single(
                Assert.Single(await installedStore.ListAsync()).ExecutionRuntimeBindings);
            Assert.Equal(2, persistedBinding.RuntimePolicyRevision);
            Assert.Equal(authoritativeLifecycle.State, persistedBinding.RuntimeLifecycleState);
            Assert.Equal(now, persistedBinding.PolicyObservedAtUtc);
            Assert.Equal(now, persistedBinding.ResolvedAtUtc);
            await oldGeneration.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(oldGeneration.IsCompletedSuccessfully);
            Assert.Empty(packageSessions.ActiveSession.LoadedPackageMap);
            var faulted = Assert.Single(packageSessions.ActiveSession.GetSessionPackages());
            Assert.Equal(PackageReadinessState.Failed, faulted.Readiness);
            var denied = Assert.Single(lifecyclePolicies.CaptureSnapshot().Entries);
            Assert.Equal(2, denied.RuntimePolicyRevision);
            Assert.Equal(authoritativeLifecycle.State, denied.Lifecycle.State);
            Assert.Equal(
                conflictCode == ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied
                    ? ExecutionRuntimeTrustDispositions.Denied
                    : ExecutionRuntimeTrustDispositions.Trusted,
                denied.TrustDisposition);
            Assert.Empty(Directory.EnumerateDirectories(paths.TransactionRootPath));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryPackagePlan_OrdinarySuccessfulPlanningDoesNotMutateRuntimeState()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.plan.read.only";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var installedStore = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await installedStore.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(installed, RegistryPackageActionKinds.Keep);
            var binding = Assert.Single(installed.ExecutionRuntimeBindings);
            var receipt = CreateRuntimeReceipt(binding, now);
            var planRequests = 0;
            var releaseRequests = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    releaseRequests++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                planRequests++;
                return PlanResponse(
                    item,
                    now: now,
                    runtimePolicyRevision: 2,
                    lifecycle: DeprecatedRuntimeLifecycle(now),
                    exactBinding: binding,
                    warnings: ["Registry plan warning."]);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver(enforceLifecyclePolicy: true);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();

            await WriteRuntimeReceiptAsync(
                provider.GetRequiredService<ExecutionRuntimeStore>(),
                provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
                receipt);
            var lifecyclePolicies = provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>();
            await lifecyclePolicies.ObserveAsync([
                new ExecutionRuntimeLifecyclePolicyObservation(
                    receipt,
                    origin,
                    SupportedRuntimeLifecycle(now),
                    RuntimePolicyRevision: 1,
                    MinimumHostTrustGeneration: 1,
                    PolicyObservedAtUtc: now.AddMinutes(-1)),
            ]);
            var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            await lifecycle.InitializeAsync();
            var loaded = await lifecycle.LoadInstalledPackagesAsync();
            Assert.True(loaded.Success, loaded.Message);
            var packageSessions = provider.GetRequiredService<PackageSessionState>();
            var activeBefore = Assert.Single(packageSessions.ActiveSession.LoadedPackageMap).Value;
            var processBefore = WorkerReleaseMetrics.GetProcess(activeBefore.Worker);
            var policySnapshot = lifecyclePolicies.CaptureSnapshot();
            var policyBefore = Assert.Single(policySnapshot.Entries);
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var catalogJson = await File.ReadAllTextAsync(paths.StateFilePath);

            var plan = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ResolveAsync(
                RuntimeActionRequest(origin, packageId),
                CancellationToken.None);

            Assert.True(plan.Success);
            Assert.Equal(["Registry plan warning."], plan.Warnings);
            Assert.Equal(1, planRequests);
            Assert.Equal(0, releaseRequests);
            Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
            Assert.Equal(catalogJson, await File.ReadAllTextAsync(paths.StateFilePath));
            Assert.Equal(binding, Assert.Single(
                Assert.Single(await installedStore.ListAsync()).ExecutionRuntimeBindings));
            var policyAfter = lifecyclePolicies.CaptureSnapshot();
            Assert.Equal(policySnapshot.Generation, policyAfter.Generation);
            Assert.Equal(policyBefore, Assert.Single(policyAfter.Entries));
            var activeAfter = Assert.Single(packageSessions.ActiveSession.LoadedPackageMap).Value;
            Assert.Same(activeBefore.Worker, activeAfter.Worker);
            Assert.False(processBefore.HasExited);
            Assert.Empty(Directory.EnumerateDirectories(paths.TransactionRootPath));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryPackagePlan_HardDenialEnforcementFailurePreservesPlanAndRequestsShutdown()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.plan.enforcement.failure";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var installedStore = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await installedStore.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(installed, RegistryPackageActionKinds.Keep);
            var targetBinding = Assert.Single(item.RuntimeBindings);
            var binding = Assert.Single(installed.ExecutionRuntimeBindings);
            var receipt = CreateRuntimeReceipt(binding, now);
            var releaseRequests = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    releaseRequests++;
                    return RuntimeReleaseResponse(
                        receipt,
                        RevokedRuntimeLifecycle(now),
                        runtimePolicyRevision: 2,
                        now);
                }

                return RuntimeConflictPlanResponse(
                    new RegistryExecutionRuntimeResolutionConflict(
                        ExecutionRuntimeErrorCodes.Revoked,
                        "The currently bound execution runtime is revoked.",
                        targetBinding.Requirement,
                        targetBinding.Rid,
                        packageId,
                        targetBinding.Role,
                        targetBinding.Runtime),
                    now,
                    ["Registry plan warning."]);
            });
            var applicationLifetime = new TestApplicationLifetime();
            var services = new ServiceCollection();
            services.AddSingleton<IHostApplicationLifetime>(applicationLifetime);
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver(enforceLifecyclePolicy: true);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();

            await WriteRuntimeReceiptAsync(
                provider.GetRequiredService<ExecutionRuntimeStore>(),
                provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
                receipt);
            var lifecyclePolicies = provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>();
            await lifecyclePolicies.ObserveAsync([
                new ExecutionRuntimeLifecyclePolicyObservation(
                    receipt,
                    origin,
                    SupportedRuntimeLifecycle(now),
                    RuntimePolicyRevision: 1,
                    MinimumHostTrustGeneration: 1,
                    PolicyObservedAtUtc: now.AddMinutes(-1)),
            ]);
            File.Delete(paths.ExecutionRuntimeLifecyclePolicyFilePath);
            Directory.CreateDirectory(paths.ExecutionRuntimeLifecyclePolicyFilePath);
            var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            await lifecycle.InitializeAsync();
            var loaded = await lifecycle.LoadInstalledPackagesAsync();
            Assert.True(loaded.Success, loaded.Message);
            var packageSessions = provider.GetRequiredService<PackageSessionState>();
            var active = Assert.Single(packageSessions.ActiveSession.LoadedPackageMap).Value;
            var process = WorkerReleaseMetrics.GetProcess(active.Worker);
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var catalogJson = await File.ReadAllTextAsync(paths.StateFilePath);

            var plan = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ResolveAsync(
                RuntimeActionRequest(origin, packageId),
                CancellationToken.None);

            Assert.False(plan.Success);
            Assert.Equal(ExecutionRuntimeErrorCodes.Revoked, Assert.Single(plan.RuntimeConflicts).Code);
            Assert.Equal("blocked-runtime-resolution", plan.ResolutionFingerprint);
            Assert.Empty(plan.Errors);
            Assert.Equal("Registry plan warning.", plan.Warnings[0]);
            Assert.Contains(plan.Warnings, warning =>
                warning.Contains("shutdown was requested", StringComparison.OrdinalIgnoreCase));
            Assert.True(applicationLifetime.StopRequested);
            Assert.Equal(1, releaseRequests);
            Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
            Assert.Equal(catalogJson, await File.ReadAllTextAsync(paths.StateFilePath));
            Assert.Equal(binding, Assert.Single(
                Assert.Single(await installedStore.ListAsync()).ExecutionRuntimeBindings));
            var trusted = Assert.Single(lifecyclePolicies.CaptureSnapshot().Entries);
            Assert.Equal(1, trusted.RuntimePolicyRevision);
            Assert.Equal(ExecutionRuntimeTrustDispositions.Trusted, trusted.TrustDisposition);
            Assert.False(process.HasExited);
            Assert.Single(packageSessions.ActiveSession.LoadedPackageMap);
            Assert.Empty(Directory.EnumerateDirectories(paths.TransactionRootPath));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryUpdateAll_UsesRecordedOriginsAndTagsAndSkipsUnmanagedPackages()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            var packages = new[]
            {
                CreateInstalledPackage("test.alpha", "https://alpha.example/", "beta", includePrerelease: true),
                CreateInstalledPackage("test.bravo", "https://bravo.example/", "stable"),
                CreateInstalledPackage("test.dependency", "https://alpha.example/", tag: null, isTransitive: true),
                CreateInstalledPackage("test.local", registryOrigin: null, tag: null),
                CreateInstalledPackage("test.pinned", "https://alpha.example/", tag: null, requestedVersion: "1.0.0"),
            };
            await store.WriteAsync(packages);
            var requests = new List<(string Origin, string PackageId, string? Tag, bool IncludePrerelease)>();
            var handler = new DelegateHandler(request =>
            {
                var body = request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!;
                Assert.Equal([SunderPackageFormat.CurrentManifestVersion], body.Compatibility.ManifestVersions);
                Assert.Equal(["worker"], body.Compatibility.RuntimeTargetKinds);
                Assert.Equal("worker-protocol.v1", body.Compatibility.WorkerProtocol);
                Assert.Contains("rpc.v1", body.Compatibility.WorkerCapabilities);
                Assert.Equal("sdk-baseline-1-1.v1", body.Compatibility.SdkBaseline);
                Assert.Equal(PackageTargetSelection.GetCurrentRuntimeIdentifier(), body.Compatibility.Rid);
                var package = Assert.Single(body.Packages);
                requests.Add((
                    request.RequestUri!.GetLeftPart(UriPartial.Authority) + "/",
                    package.PackageId,
                    package.Tag,
                    body.IncludePrerelease));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                        true,
                        [],
                        [],
                        [],
                        [],
                        [],
                        ResolutionFingerprint: new string('f', 64))),
                };
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().UpdateAsync(
                new RuntimeRegistryUpdateRequest(DryRun: true),
                CancellationToken.None);
            var includePrereleaseResult = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().UpdateAsync(
                new RuntimeRegistryUpdateRequest(IncludePrerelease: true),
                CancellationToken.None);
            var mismatchedOrigin = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().UpdateAsync(
                new RuntimeRegistryUpdateRequest("https://other.example/", "test.alpha"),
                CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.True(includePrereleaseResult.Success, includePrereleaseResult.Message);
            Assert.Equal(["test.dependency", "test.local", "test.pinned"], result.SkippedPackageIds);
            Assert.Contains(result.Warnings, warning => warning.Contains("unmanaged", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Warnings, warning => warning.Contains("pinned", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Warnings, warning => warning.Contains("resolver-managed dependency", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                [
                    ("https://alpha.example/", true, new string('f', 64)),
                    ("https://bravo.example/", false, new string('f', 64)),
                ],
                result.PlanExpectations.Select(static plan => (
                    plan.RegistryOrigin,
                    plan.IncludePrerelease,
                    plan.ExpectedResolutionFingerprint)).ToArray());
            Assert.Empty(result.RuntimeArtifacts);
            Assert.Equal(
                [
                    ("https://alpha.example/", "test.alpha", "beta", true),
                    ("https://bravo.example/", "test.bravo", "stable", false),
                    ("https://alpha.example/", "test.alpha", "beta", true),
                    ("https://bravo.example/", "test.bravo", "stable", true),
                ],
                requests);
            Assert.True(mismatchedOrigin.Success);
            Assert.Equal(["test.alpha"], mismatchedOrigin.SkippedPackageIds);
            Assert.Contains(mismatchedOrigin.Warnings, warning => warning.Contains("recorded Registry origin", StringComparison.Ordinal));

            InstalledPackageRecord CreateInstalledPackage(
                string packageId,
                string? registryOrigin,
                string? tag,
                string? requestedVersion = null,
                bool includePrerelease = false,
                bool isTransitive = false)
            {
                var installPath = paths.GetInstalledPackagePath(packageId, "1.0.0");
                CanonicalPackageTestBuilder.WriteExplodedPackage(
                    installPath,
                    packageId,
                    "1.0.0",
                    typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
                var record = CanonicalPackageTestBuilder.CreateInstalledRecord(installPath, packageId, "1.0.0");
                return registryOrigin is null
                    ? record with { Provenance = InstalledPackageProvenanceRecord.Unknown }
                    : record with
                    {
                        Provenance = new InstalledPackageProvenanceRecord(
                            InstalledPackageSourceKind.Registry,
                            isTransitive
                                ? InstalledPackageVersionPolicy.TransitiveDependency
                                : requestedVersion is null
                                ? InstalledPackageVersionPolicy.FollowTag
                                : InstalledPackageVersionPolicy.ExplicitVersion,
                            registryOrigin,
                            packageId,
                            RequestedTag: isTransitive ? null : tag,
                            RequestedVersion: requestedVersion,
                            SourceIdentity: new string('a', 64),
                            IncludePrerelease: isTransitive ? false : includePrerelease),
                    };
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryUpdateAll_ResolvesEveryOriginBeforePreparingArtifactsAndCommitsNothingWhenOnePlanFails()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            await store.WriteAsync([
                CreateManagedPackage("test.alpha", "https://alpha.example/"),
                CreateManagedPackage("test.bravo", "https://bravo.example/"),
            ]);
            var planOrigins = new List<string>();
            var artifactRequests = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    artifactRequests++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                var origin = request.RequestUri!.GetLeftPart(UriPartial.Authority) + "/";
                planOrigins.Add(origin);
                if (origin == "https://bravo.example/")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                            false,
                            [],
                            [],
                            ["Bravo plan failed."],
                            [new RegistryPackageInstallPlanConflict(
                                "test.bravo",
                                "1.0.0",
                                ">=2.0.0",
                                "test.alpha",
                                RegistryV1ErrorCodes.PackageRequirementUnsatisfied,
                                "Bravo package requirement conflict.")],
                            [])),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                        true,
                        [new RegistryPackageInstallPlanItem(
                            "test.alpha",
                            "1.0.0",
                            "2.0.0",
                            new string('b', 64),
                            RegistryPackageActionKinds.PackageUpdate,
                            null,
                            [],
                            [],
                            [])],
                        [],
                        [],
                        [],
                        [])),
                };
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().UpdateAsync(
                new RuntimeRegistryUpdateRequest(),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.Conflict, result.ErrorCode);
            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal("test.bravo", conflict.PackageId);
            Assert.Equal(RegistryV1ErrorCodes.PackageRequirementUnsatisfied, conflict.ErrorCode);
            Assert.Equal(["https://alpha.example/", "https://bravo.example/"], planOrigins);
            Assert.Equal(0, artifactRequests);
            Assert.All(await store.ListAsync(), package => Assert.Equal("1.0.0", package.Version));

            InstalledPackageRecord CreateManagedPackage(string packageId, string registryOrigin)
            {
                var installPath = paths.GetInstalledPackagePath(packageId, "1.0.0");
                CanonicalPackageTestBuilder.WriteExplodedPackage(
                    installPath,
                    packageId,
                    "1.0.0",
                    typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
                return CanonicalPackageTestBuilder.CreateInstalledRecord(installPath, packageId, "1.0.0") with
                {
                    Provenance = new InstalledPackageProvenanceRecord(
                        InstalledPackageSourceKind.Registry,
                        InstalledPackageVersionPolicy.FollowTag,
                        registryOrigin,
                        packageId,
                        RequestedTag: "latest",
                        SourceIdentity: new string('a', 64)),
                };
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryRuntimeRebindStage_WhenExpectedCatalogIsStale_RejectsWithoutStage()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.rebind.stale.catalog";
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await store.WriteAsync([installed]);
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            provider = services.BuildServiceProvider();
            var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            await lifecycle.InitializeAsync();
            var catalogGeneration = provider.GetRequiredService<PackageStoreCoordinator>().CatalogGeneration;
            var changed = installed with { IsEnabled = false };
            await store.WriteAsync([changed]);
            var desiredBinding = Assert.Single(installed.ExecutionRuntimeBindings) with
            {
                RuntimePackagingRevision = 2,
                RuntimeManifestSha256 = new string('c', 64),
                RuntimeArtifactSha256 = new string('d', 64),
            };

            var stage = await lifecycle.StageRegistryAsync(
                new PackageStoreStageRequest([]),
                new Dictionary<string, RegistryPackageUploadMetadata>(StringComparer.Ordinal),
                [installed],
                [new PackageExecutionRuntimeRebindMutation(packageId, installed, [desiredBinding])]);

            Assert.False(stage.Success);
            Assert.Null(stage.StageId);
            Assert.Contains("catalog changed after resolution", stage.OperationResult.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(catalogGeneration, provider.GetRequiredService<PackageStoreCoordinator>().CatalogGeneration);
            var persisted = Assert.Single(await store.ListAsync());
            Assert.False(persisted.IsEnabled);
            Assert.Equal(Assert.Single(installed.ExecutionRuntimeBindings), Assert.Single(persisted.ExecutionRuntimeBindings));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryExecution_RuntimeOnlyRebindSkipsPackageArtifactGetAndCommitsExactBinding()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.rebind";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await store.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(installed, RegistryPackageActionKinds.RuntimeOnlyRebind);
            var resolutionRequests = new List<RegistryResolvePackageChangesRequest>();
            var artifactGets = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    artifactGets++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                resolutionRequests.Add(request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!);
                return PlanResponse(item, now: now);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;

            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ExecuteAsync(
                RuntimeActionRequest(origin, packageId),
                CancellationToken.None);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.True(result.RuntimeSessionApplied);
            Assert.Equal(0, artifactGets);
            Assert.Equal(
                new string?[] { null, "test-resolution-fingerprint", "test-resolution-fingerprint" },
                resolutionRequests.Select(static request => request.ExpectedResolutionFingerprint).ToArray());
            Assert.Equal(catalogGeneration + 1, coordinator.CatalogGeneration);
            var persisted = Assert.Single(await store.ListAsync());
            Assert.Equal(installed.ContentIdentity, persisted.ContentIdentity);
            Assert.Equal(installed.InstallPath, persisted.InstallPath);
            var binding = Assert.Single(persisted.ExecutionRuntimeBindings);
            var plannedRuntime = Assert.Single(item.RuntimeBindings).Runtime;
            Assert.Equal(plannedRuntime.RuntimeId, binding.RuntimeId);
            Assert.Equal(plannedRuntime.Version, binding.RuntimeVersion);
            Assert.Equal(plannedRuntime.PackagingRevision, binding.RuntimePackagingRevision);
            Assert.Equal(plannedRuntime.Rid, binding.Rid);
            Assert.Equal(plannedRuntime.ModuleAbi, binding.ModuleAbi);
            Assert.Equal(new string('1', 64), binding.RuntimeManifestSha256);
            Assert.Equal(new string('2', 64), binding.RuntimeArtifactSha256);
            Assert.Equal(4096, binding.RuntimeArtifactSize);
            Assert.Equal(origin, binding.RegistryOrigin);
            Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.Registry, binding.AcquisitionSource);
            Assert.Equal("sunder.test.release.v1", binding.SigningKeyId);
            Assert.Equal(1, binding.TrustStoreGeneration);
            Assert.Equal(1, binding.RuntimePolicyRevision);
            Assert.Equal(ExecutionRuntimeLifecycleStates.Supported, binding.RuntimeLifecycleState);
            Assert.Equal(now, binding.PolicyObservedAtUtc);
            Assert.Equal(now, binding.ResolvedAtUtc);
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryExecution_RuntimeOnlyRebindCandidateFailurePreservesActiveStateAndCleansCandidate()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.rebind.candidate.failure";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await store.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(
                installed,
                RegistryPackageActionKinds.RuntimeOnlyRebind);
            var resolutionRequests = new List<RegistryResolvePackageChangesRequest>();
            var packageArtifactGets = 0;
            string? stageId = null;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    packageArtifactGets++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                resolutionRequests.Add(request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!);
                if (resolutionRequests.Count == 2)
                {
                    stageId = GetOnlyPendingStageId(provider!);
                }
                return PlanResponse(item, now: now);
            });
            var launchResolver = new CandidateFailingExecutionRuntimeLaunchResolver(root);
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver(launchResolver: launchResolver);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();

            var lifecyclePolicies = provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>();
            var oldBinding = Assert.Single(installed.ExecutionRuntimeBindings);
            var oldReceipt = CreateRuntimeReceipt(oldBinding, now);
            await lifecyclePolicies.ObserveAsync([
                new ExecutionRuntimeLifecyclePolicyObservation(
                    oldReceipt,
                    origin,
                    SupportedRuntimeLifecycle(now),
                    RuntimePolicyRevision: 1,
                    MinimumHostTrustGeneration: 1,
                    PolicyObservedAtUtc: now.AddMinutes(-1)),
            ]);
            var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            await lifecycle.InitializeAsync();
            var loaded = await lifecycle.LoadInstalledPackagesAsync();
            Assert.True(loaded.Success, loaded.Message);

            var packageSessions = provider.GetRequiredService<PackageSessionState>();
            var oldSession = packageSessions.ActiveSession;
            var oldActive = Assert.Single(oldSession.LoadedPackageMap).Value;
            var oldWorker = oldActive.Worker;
            var oldProcess = WorkerReleaseMetrics.GetProcess(oldWorker);
            var oldLaunch = Assert.Single(launchResolver.Launches);
            Assert.Equal(oldProcess.Id, oldLaunch.Lease.ProcessId);
            Assert.False(oldLaunch.Lease.Released);
            var oldProjectionPath = Assert.IsType<string>(oldSession.SessionFolder);
            var oldProjectionFiles = CaptureFileTree(oldProjectionPath);
            var installedFiles = CaptureFileTree(installed.InstallPath);
            var dataPath = Path.Combine(paths.PackageDataRootPath, packageId, "data", "preserved.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            await File.WriteAllTextAsync(dataPath, "preserve-me");
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var catalogBytes = await File.ReadAllBytesAsync(paths.StateFilePath);
            var policyBefore = lifecyclePolicies.CaptureSnapshot();
            var policyBytes = await File.ReadAllBytesAsync(paths.ExecutionRuntimeLifecyclePolicyFilePath);

            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ExecuteAsync(
                RuntimeActionRequest(origin, packageId),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.InternalError, result.ErrorCode);
            Assert.Equal("The package store stage could not be committed.", result.Message);
            Assert.Equal("The package store stage could not be committed.", Assert.Single(result.Errors));
            Assert.Equal(0, packageArtifactGets);
            Assert.Equal(
                new string?[] { null, "test-resolution-fingerprint" },
                resolutionRequests.Select(static request => request.ExpectedResolutionFingerprint).ToArray());
            Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
            Assert.Equal(catalogBytes, await File.ReadAllBytesAsync(paths.StateFilePath));
            var persisted = Assert.Single(await store.ListAsync());
            Assert.Equal(installed.ContentIdentity, persisted.ContentIdentity);
            Assert.Equal(installed.InstallPath, persisted.InstallPath);
            Assert.Equal(oldBinding, Assert.Single(persisted.ExecutionRuntimeBindings));
            Assert.Equal(installedFiles, CaptureFileTree(installed.InstallPath));
            Assert.Equal("preserve-me", await File.ReadAllTextAsync(dataPath));

            Assert.Same(oldSession, packageSessions.ActiveSession);
            var activeAfter = Assert.Single(packageSessions.ActiveSession.LoadedPackageMap).Value;
            Assert.Same(oldWorker, activeAfter.Worker);
            oldProcess.Refresh();
            Assert.False(oldProcess.HasExited);
            Assert.False(oldLaunch.Lease.Released);
            Assert.Equal(oldProcess.Id, oldLaunch.Lease.ProcessId);
            Assert.Equal(oldProjectionFiles, CaptureFileTree(oldProjectionPath));

            var candidateLaunch = Assert.Single(
                launchResolver.Launches,
                launch => launch.Identity != oldLaunch.Identity);
            Assert.NotEqual(oldLaunch.RuntimeRootPath, candidateLaunch.RuntimeRootPath);
            Assert.NotNull(candidateLaunch.Lease.ProcessId);
            Assert.True(candidateLaunch.Lease.Released);
            Assert.True(candidateLaunch.Lease.ProcessExitedAtRelease);
            Assert.Equal(oldLaunch.ProjectionPath, candidateLaunch.ProjectionPath);
            Assert.True(Directory.Exists(candidateLaunch.ProjectionPath));
            var failedStageId = Assert.IsType<string>(stageId);
            var owner = provider.GetRequiredService<RuntimeSessionOwner>();
            Assert.False(owner.TryGetStageGeneration(failedStageId, out _));
            Assert.Equal(RuntimePackageStageState.Failed, owner.GetStageStatus(failedStageId)?.State);
            Assert.Empty(Directory.EnumerateDirectories(paths.TransactionRootPath));

            var policyAfter = lifecyclePolicies.CaptureSnapshot();
            Assert.Equal(policyBefore.Generation, policyAfter.Generation);
            Assert.Equal(policyBefore.Entries, policyAfter.Entries);
            Assert.Equal(policyBytes, await File.ReadAllBytesAsync(paths.ExecutionRuntimeLifecyclePolicyFilePath));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, ExecutionRuntimeErrorCodes.Missing)]
    [InlineData(true, ExecutionRuntimeErrorCodes.PolicyRequired)]
    public async Task LocalPackageStage_RuntimeUnavailableReturnsExactRequirementWithoutRegistryOrMutation(
        bool writeCompatibleReceipt,
        string expectedErrorCode)
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        string? archivePath = null;
        try
        {
            var paths = new RuntimePackagePaths(root);
            RuntimeLocalState.EnsureInitialized(root);
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var packageId = writeCompatibleReceipt
                ? "test.local.runtime.policyrequired"
                : "test.local.runtime.missing";
            var sourceRoot = Path.Combine(root, "local-package-source");
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                sourceRoot,
                packageId,
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: [rid]);
            archivePath = await CanonicalPackageTestBuilder.CreateArchiveAsync(sourceRoot);
            var installedRecord = CanonicalPackageTestBuilder.CreateInstalledRecord(
                sourceRoot,
                packageId,
                "1.0.0");
            var networkCalls = 0;
            var handler = new DelegateHandler(_ =>
            {
                Interlocked.Increment(ref networkCalls);
                throw new InvalidOperationException("Local package staging must not invoke Registry HTTP.");
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();

            if (writeCompatibleReceipt)
            {
                await WriteRuntimeReceiptAsync(
                    provider.GetRequiredService<ExecutionRuntimeStore>(),
                    provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
                    CreateRuntimeReceipt(
                        Assert.Single(installedRecord.ExecutionRuntimeBindings),
                        DateTimeOffset.Parse("2026-08-28T10:00:00Z")));
            }
            var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            await lifecycle.InitializeAsync();
            var transfers = provider.GetRequiredService<RuntimeContentTransferStore>();
            await using var archive = File.OpenRead(archivePath);
            var upload = await transfers.CreateUploadAsync(
                RuntimeUploadKind.Package,
                archive,
                archive.Length,
                expectedHash: null,
                Path.GetFileName(archivePath),
                "application/vnd.sunder.package",
                provider.GetRequiredService<RuntimeSessionOwner>().Generation,
                CancellationToken.None);
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var catalogBytes = File.Exists(paths.StateFilePath)
                ? await File.ReadAllBytesAsync(paths.StateFilePath)
                : null;
            var catalogFiles = CaptureFileTree(paths.CatalogRootPath);
            var installedFiles = CaptureFileTree(paths.InstalledRootPath);
            var stagingFiles = CaptureFileTree(paths.StagingRootPath);
            var stagingDirectories = CaptureDirectoryTree(paths.StagingRootPath);
            var transactionFiles = CaptureFileTree(paths.TransactionRootPath);
            var transactionDirectories = CaptureDirectoryTree(paths.TransactionRootPath);
            var activeSession = provider.GetRequiredService<PackageSessionState>().ActiveSession;
            var sessionGeneration = provider.GetRequiredService<RuntimeSessionOwner>().Generation;

            var stage = await lifecycle.StageAsync(new PackageStoreStageRequest([
                new PackageStoreMutationRequest(
                    PackageStoreMutationKind.Install,
                    UploadId: upload.UploadId),
            ]));

            var requirement =
                $"{SunderPackageFormat.DotNetExecutionRuntimeId} >=10.0.0 <11.0.0 {SunderPackageFormat.DotNetWorkerModuleAbi}";
            var ensureCommand =
                $"sunder runtime ensure {SunderPackageFormat.DotNetExecutionRuntimeId} '>=10.0.0 <11.0.0' {SunderPackageFormat.DotNetWorkerModuleAbi}";
            var recovery = writeCompatibleReceipt
                ? $"Run \"{ensureCommand}\" to resolve trusted Registry policy, then retry."
                : $"Run \"{ensureCommand}\" or 'sunder runtime import --file <artifact.sunderruntime>', then retry.";
            var expectedMessage =
                $"{expectedErrorCode}: no installed execution runtime with authoritative local policy satisfies target 'runtime/{rid}' requirement '{requirement}'. {recovery}";
            Assert.False(stage.Success);
            Assert.Null(stage.StageId);
            Assert.Equal(expectedMessage, stage.OperationResult.Message);
            Assert.Equal(expectedMessage, Assert.Single(stage.Errors));
            Assert.Equal(0, Volatile.Read(ref networkCalls));
            Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
            if (catalogBytes is null)
            {
                Assert.False(File.Exists(paths.StateFilePath));
            }
            else
            {
                Assert.Equal(catalogBytes, await File.ReadAllBytesAsync(paths.StateFilePath));
            }
            Assert.Equal(catalogFiles, CaptureFileTree(paths.CatalogRootPath));
            Assert.Equal(installedFiles, CaptureFileTree(paths.InstalledRootPath));
            Assert.Empty(await provider.GetRequiredService<InstalledPackageStore>().ListAsync());
            Assert.Equal(stagingFiles, CaptureFileTree(paths.StagingRootPath));
            Assert.Equal(stagingDirectories, CaptureDirectoryTree(paths.StagingRootPath));
            Assert.Equal(transactionFiles, CaptureFileTree(paths.TransactionRootPath));
            Assert.Equal(transactionDirectories, CaptureDirectoryTree(paths.TransactionRootPath));
            Assert.Equal(sessionGeneration, provider.GetRequiredService<RuntimeSessionOwner>().Generation);
            Assert.Same(activeSession, provider.GetRequiredService<PackageSessionState>().ActiveSession);
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            if (archivePath is not null)
            {
                File.Delete(archivePath);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryExecution_KeepDoesNotMutatePackageStore()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.keep";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await store.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(installed, RegistryPackageActionKinds.Keep);
            var resolutionRequests = new List<RegistryResolvePackageChangesRequest>();
            var artifactGets = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    artifactGets++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                resolutionRequests.Add(request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!);
                return PlanResponse(
                    item,
                    now: now,
                    exactBinding: Assert.Single(installed.ExecutionRuntimeBindings));
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var lifecyclePolicies = provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>();
            await lifecyclePolicies.InitializeAsync();
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var catalogJson = await File.ReadAllTextAsync(paths.StateFilePath);

            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ExecuteAsync(
                RuntimeActionRequest(origin, packageId),
                CancellationToken.None);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.False(result.RuntimeSessionApplied);
            Assert.Equal(0, artifactGets);
            Assert.Equal(
                new string?[] { null, "test-resolution-fingerprint" },
                resolutionRequests.Select(static request => request.ExpectedResolutionFingerprint).ToArray());
            Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
            Assert.Equal(catalogJson, await File.ReadAllTextAsync(paths.StateFilePath));
            Assert.Empty(Directory.EnumerateDirectories(paths.TransactionRootPath));
            Assert.Single(lifecyclePolicies.CaptureSnapshot().Entries);
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryExecution_LifecycleOnlyKeepCommitsBindingAndPolicyCacheTogether()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.keep.lifecycle";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await store.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(installed, RegistryPackageActionKinds.Keep);
            var currentBinding = Assert.Single(installed.ExecutionRuntimeBindings);
            var lifecycle = DeprecatedRuntimeLifecycle(now);
            var resolutionRequests = new List<RegistryResolvePackageChangesRequest>();
            var artifactGets = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    artifactGets++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                resolutionRequests.Add(request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!);
                return PlanResponse(
                    item,
                    now: now,
                    runtimePolicyRevision: 2,
                    lifecycle: lifecycle,
                    exactBinding: currentBinding);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();
            var packageLifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            await packageLifecycle.InitializeAsync();
            var loaded = await packageLifecycle.LoadInstalledPackagesAsync();
            Assert.True(loaded.Success, loaded.Message);
            var activeBefore = Assert.Single(
                provider.GetRequiredService<PackageSessionState>().ActiveSession.LoadedPackageMap).Value;
            var workerBefore = activeBefore.Worker;
            var processBefore = WorkerReleaseMetrics.GetProcess(workerBefore);
            Assert.False(processBefore.HasExited);
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;

            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ExecuteAsync(
                RuntimeActionRequest(origin, packageId),
                CancellationToken.None);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.False(result.RuntimeSessionApplied);
            Assert.Equal(0, artifactGets);
            Assert.Equal(
                new string?[] { null, "test-resolution-fingerprint", "test-resolution-fingerprint" },
                resolutionRequests.Select(static request => request.ExpectedResolutionFingerprint).ToArray());
            Assert.Equal(catalogGeneration + 1, coordinator.CatalogGeneration);
            var persistedBinding = Assert.Single(Assert.Single(await store.ListAsync()).ExecutionRuntimeBindings);
            Assert.Equal(currentBinding.RuntimeId, persistedBinding.RuntimeId);
            Assert.Equal(currentBinding.RuntimeVersion, persistedBinding.RuntimeVersion);
            Assert.Equal(currentBinding.RuntimePackagingRevision, persistedBinding.RuntimePackagingRevision);
            Assert.Equal(currentBinding.RuntimeManifestSha256, persistedBinding.RuntimeManifestSha256);
            Assert.Equal(currentBinding.RuntimeArtifactSha256, persistedBinding.RuntimeArtifactSha256);
            Assert.Equal(2, persistedBinding.RuntimePolicyRevision);
            Assert.Equal(ExecutionRuntimeLifecycleStates.Deprecated, persistedBinding.RuntimeLifecycleState);
            Assert.Equal(now, persistedBinding.PolicyObservedAtUtc);
            Assert.Equal(now, persistedBinding.ResolvedAtUtc);
            var cached = Assert.Single(provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>()
                .CaptureSnapshot().Entries);
            Assert.Equal(2, cached.RuntimePolicyRevision);
            Assert.Equal(lifecycle, cached.Lifecycle);
            Assert.Equal(now, cached.PolicyObservedAtUtc);
            var activeAfter = Assert.Single(
                provider.GetRequiredService<PackageSessionState>().ActiveSession.LoadedPackageMap).Value;
            Assert.Same(workerBefore, activeAfter.Worker);
            Assert.False(processBefore.HasExited);
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryInstall_StalePreviewFailsBeforeRuntimePackageDownloadOrStaging()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.install.stale.preview";
            var previewFingerprint = new string('a', 64);
            var currentFingerprint = new string('b', 64);
            var paths = new RuntimePackagePaths(root);
            var artifact = await CreateRegistryArtifactAsync(
                root,
                "stale-preview",
                origin,
                packageId,
                currentVersion: null,
                version: "1.0.0");
            var resolutionRequests = new List<RegistryResolvePackageChangesRequest>();
            var artifactGets = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    artifactGets++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                resolutionRequests.Add(request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!);
                return PlanResponse(
                    artifact.PlanItem,
                    resolutionFingerprint: currentFingerprint);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var packageStagingBefore = CaptureFileTree(paths.StagingRootPath);
            var packageTransactionsBefore = CaptureFileTree(paths.TransactionRootPath);
            var runtimeStagingBefore = CaptureFileTree(paths.ExecutionRuntimeStagingRootPath);
            var runtimeTransactionsBefore = CaptureFileTree(paths.ExecutionRuntimeTransactionRootPath);

            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().InstallAsync(
                new RuntimeRegistryPackageRequest(
                    origin,
                    packageId,
                    Version: "1.0.0",
                    Tag: null,
                    ExpectedResolutionFingerprint: previewFingerprint),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.StalePlan, result.ErrorCode);
            Assert.Equal(
                "The Registry package plan changed after it was previewed. Review the current plan and retry; no package changes were applied.",
                result.Message);
            Assert.Equal(0, artifactGets);
            Assert.Equal(previewFingerprint, Assert.Single(resolutionRequests).ExpectedResolutionFingerprint);
            Assert.Empty(await new InstalledPackageStore(paths).ListAsync());
            Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
            Assert.Equal(packageStagingBefore, CaptureFileTree(paths.StagingRootPath));
            Assert.Equal(packageTransactionsBefore, CaptureFileTree(paths.TransactionRootPath));
            Assert.Equal(runtimeStagingBefore, CaptureFileTree(paths.ExecutionRuntimeStagingRootPath));
            Assert.Equal(runtimeTransactionsBefore, CaptureFileTree(paths.ExecutionRuntimeTransactionRootPath));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryExecution_StaleLifecycleKeepSecondConfirmationLeavesCatalogAndPolicyCacheUntouched()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.keep.stale";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await store.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(installed, RegistryPackageActionKinds.Keep);
            var currentBinding = Assert.Single(installed.ExecutionRuntimeBindings);
            var lifecycle = DeprecatedRuntimeLifecycle(now);
            var resolutionRequests = new List<RegistryResolvePackageChangesRequest>();
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                resolutionRequests.Add(request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!);
                return PlanResponse(
                    item,
                    resolutionFingerprint: resolutionRequests.Count == 3
                        ? "stale-resolution-fingerprint"
                        : "test-resolution-fingerprint",
                    now: now,
                    runtimePolicyRevision: 2,
                    lifecycle: lifecycle,
                    exactBinding: currentBinding);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var lifecyclePolicies = provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>();
            await lifecyclePolicies.InitializeAsync();
            var before = lifecyclePolicies.CaptureSnapshot();
            Assert.Empty(before.Entries);
            Assert.False(File.Exists(paths.ExecutionRuntimeLifecyclePolicyFilePath));
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var catalogJson = await File.ReadAllTextAsync(paths.StateFilePath);

            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ExecuteAsync(
                RuntimeActionRequest(origin, packageId),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.Conflict, result.ErrorCode);
            Assert.Contains("stale", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                new string?[] { null, "test-resolution-fingerprint", "test-resolution-fingerprint" },
                resolutionRequests.Select(static request => request.ExpectedResolutionFingerprint).ToArray());
            Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
            Assert.Equal(catalogJson, await File.ReadAllTextAsync(paths.StateFilePath));
            var after = lifecyclePolicies.CaptureSnapshot();
            Assert.Equal(before.Generation, after.Generation);
            Assert.Empty(after.Entries);
            Assert.False(File.Exists(paths.ExecutionRuntimeLifecyclePolicyFilePath));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryExecution_SecondStaleConfirmationCarriesFingerprintAndDiscardsStage()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.rebind.stale.confirmation";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await store.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(installed, RegistryPackageActionKinds.RuntimeOnlyRebind);
            var resolutionRequests = new List<RegistryResolvePackageChangesRequest>();
            var artifactGets = 0;
            string? stagedId = null;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    artifactGets++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                resolutionRequests.Add(request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!);
                if (resolutionRequests.Count == 3)
                {
                    stagedId = GetOnlyPendingStageId(provider!);
                    return PlanResponse(item, resolutionFingerprint: "stale-resolution-fingerprint", now: now);
                }
                return PlanResponse(item, now: now);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var catalogJson = await File.ReadAllTextAsync(paths.StateFilePath);

            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ExecuteAsync(
                RuntimeActionRequest(origin, packageId),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.Conflict, result.ErrorCode);
            Assert.Contains("stale", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, artifactGets);
            Assert.Equal(
                new string?[] { null, "test-resolution-fingerprint", "test-resolution-fingerprint" },
                resolutionRequests.Select(static request => request.ExpectedResolutionFingerprint).ToArray());
            var discardedStageId = Assert.IsType<string>(stagedId);
            var owner = provider.GetRequiredService<RuntimeSessionOwner>();
            Assert.False(owner.TryGetStageGeneration(discardedStageId, out _));
            Assert.Equal(RuntimePackageStageState.Discarded, owner.GetStageStatus(discardedStageId)?.State);
            Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
            Assert.Equal(catalogJson, await File.ReadAllTextAsync(paths.StateFilePath));
            Assert.Empty(Directory.EnumerateDirectories(paths.TransactionRootPath));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, true, true, false, false, ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied, true)]
    [InlineData(2, false, true, false, false, ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied, true)]
    [InlineData(3, false, true, false, false, ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied, true)]
    [InlineData(1, true, false, false, false, ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied, true)]
    [InlineData(1, true, true, true, false, ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied, true)]
    [InlineData(1, true, true, false, true, ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied, true)]
    [InlineData(1, true, true, false, false, ExecutionRuntimeErrorCodes.Revoked, true)]
    [InlineData(1, true, true, false, false, ExecutionRuntimeErrorCodes.Retired, true)]
    [InlineData(1, true, true, false, false, ExecutionRuntimeErrorCodes.PolicyRequired, false)]
    [InlineData(1, true, true, false, false, ExecutionRuntimeErrorCodes.HostTrustUpdateRequired, false)]
    public async Task RegistryUpdate_RuntimePolicyConflictEnforcesOnlyExactHardDenials(
        int deniedPlanRequest,
        bool dryRun,
        bool writeReceipt,
        bool breakPolicyPersistence,
        bool malformedEvidence,
        string conflictCode,
        bool expectedHardDenial)
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.trust.denied.active";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var installedStore = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            await installedStore.WriteAsync([installed]);
            var item = await CreateRuntimeActionItemAsync(
                installed,
                RegistryPackageActionKinds.RuntimeOnlyRebind);
            var targetBinding = Assert.Single(item.RuntimeBindings);
            var binding = Assert.Single(installed.ExecutionRuntimeBindings);
            var currentRuntime = new RegistryExecutionRuntimeIdentity(
                binding.RuntimeId,
                binding.RuntimeVersion,
                binding.RuntimePackagingRevision,
                binding.Rid,
                binding.ModuleAbi);
            var receipt = CreateRuntimeReceipt(binding, now);
            var authoritativeLifecycle = conflictCode switch
            {
                ExecutionRuntimeErrorCodes.Revoked => RevokedRuntimeLifecycle(now),
                ExecutionRuntimeErrorCodes.Retired => RetiredRuntimeLifecycle(now),
                _ => SupportedRuntimeLifecycle(now),
            };
            var planRequests = 0;
            var releaseRequests = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    releaseRequests++;
                    return RuntimeReleaseResponse(
                        receipt,
                        authoritativeLifecycle,
                        runtimePolicyRevision: 2,
                        now);
                }
                planRequests++;
                return planRequests == deniedPlanRequest
                    ? RuntimeConflictPlanResponse(
                        new RegistryExecutionRuntimeResolutionConflict(
                            conflictCode,
                            conflictCode switch
                            {
                                ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied =>
                                    "The currently bound execution runtime is no longer trusted.",
                                ExecutionRuntimeErrorCodes.Revoked =>
                                    "The currently bound execution runtime is revoked.",
                                ExecutionRuntimeErrorCodes.Retired =>
                                    "The currently bound execution runtime is retired.",
                                _ => "Registry policy requires a different execution runtime.",
                            },
                            targetBinding.Requirement,
                            targetBinding.Rid,
                            packageId,
                            targetBinding.Role,
                            malformedEvidence ? null : currentRuntime),
                        now)
                    : PlanResponse(item, now: now);
            });
            var applicationLifetime = new TestApplicationLifetime();
            var services = new ServiceCollection();
            services.AddSingleton<IHostApplicationLifetime>(applicationLifetime);
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver(enforceLifecyclePolicy: true);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();

            if (writeReceipt)
            {
                await WriteRuntimeReceiptAsync(
                    provider.GetRequiredService<ExecutionRuntimeStore>(),
                    provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
                    receipt);
            }
            var lifecyclePolicies = provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>();
            await lifecyclePolicies.ObserveAsync([
                new ExecutionRuntimeLifecyclePolicyObservation(
                    receipt,
                    origin,
                    SupportedRuntimeLifecycle(now),
                    RuntimePolicyRevision: 1,
                    MinimumHostTrustGeneration: 1,
                    PolicyObservedAtUtc: now.AddMinutes(-1)),
            ]);
            if (breakPolicyPersistence)
            {
                File.Delete(paths.ExecutionRuntimeLifecyclePolicyFilePath);
                Directory.CreateDirectory(paths.ExecutionRuntimeLifecyclePolicyFilePath);
            }
            var dataRoot = Path.Combine(paths.PackageDataRootPath, packageId, "data");
            Directory.CreateDirectory(dataRoot);
            var dataPath = Path.Combine(dataRoot, "preserved.txt");
            await File.WriteAllTextAsync(dataPath, "preserve-me");
            var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            await lifecycle.InitializeAsync();
            var loaded = await lifecycle.LoadInstalledPackagesAsync();
            Assert.True(loaded.Success, loaded.Message);
            var packageSessions = provider.GetRequiredService<PackageSessionState>();
            var active = Assert.Single(packageSessions.ActiveSession.LoadedPackageMap).Value;
            var oldGeneration = active.Worker.GenerationCompletion;
            var oldProcess = WorkerReleaseMetrics.GetProcess(active.Worker);
            Assert.False(oldProcess.HasExited);
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var catalogJson = await File.ReadAllTextAsync(paths.StateFilePath);

            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().UpdateAsync(
                new RuntimeRegistryUpdateRequest(
                    RegistryOrigin: origin,
                    PackageId: packageId,
                    DryRun: dryRun),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.Conflict, result.ErrorCode);
            Assert.Equal(conflictCode, Assert.Single(result.RuntimeConflicts).Code);
            Assert.Equal(deniedPlanRequest, planRequests);
            Assert.Equal(expectedHardDenial && writeReceipt && !malformedEvidence ? 1 : 0, releaseRequests);
            Assert.Equal("preserve-me", await File.ReadAllTextAsync(dataPath));
            Assert.Empty(Directory.EnumerateDirectories(paths.TransactionRootPath));
            var policyCommitted = expectedHardDenial
                                  && writeReceipt
                                  && !breakPolicyPersistence
                                  && !malformedEvidence;
            if (policyCommitted)
            {
                Assert.Equal(catalogGeneration + 1, coordinator.CatalogGeneration);
                Assert.NotEqual(catalogJson, await File.ReadAllTextAsync(paths.StateFilePath));
                var persisted = Assert.Single(await installedStore.ListAsync());
                var persistedBinding = Assert.Single(persisted.ExecutionRuntimeBindings);
                Assert.Equal(2, persistedBinding.RuntimePolicyRevision);
                Assert.Equal(authoritativeLifecycle.State, persistedBinding.RuntimeLifecycleState);
                Assert.Equal(now, persistedBinding.PolicyObservedAtUtc);
                Assert.Equal(now, persistedBinding.ResolvedAtUtc);
            }
            else
            {
                Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
                Assert.Equal(catalogJson, await File.ReadAllTextAsync(paths.StateFilePath));
                Assert.Equal(binding, Assert.Single(
                    Assert.Single(await installedStore.ListAsync()).ExecutionRuntimeBindings));
            }
            if (!expectedHardDenial)
            {
                Assert.False(applicationLifetime.StopRequested);
                Assert.DoesNotContain(result.Warnings, warning =>
                    warning.Contains("Stopped and faulted", StringComparison.Ordinal));
                Assert.False(oldProcess.HasExited);
                Assert.Single(packageSessions.ActiveSession.LoadedPackageMap);
                var trusted = Assert.Single(lifecyclePolicies.CaptureSnapshot().Entries);
                Assert.Equal(1, trusted.RuntimePolicyRevision);
                Assert.Equal(ExecutionRuntimeTrustDispositions.Trusted, trusted.TrustDisposition);
            }
            else if (writeReceipt && !breakPolicyPersistence && !malformedEvidence)
            {
                Assert.False(applicationLifetime.StopRequested);
                Assert.Contains(result.Warnings, warning =>
                    warning.Contains("Stopped and faulted", StringComparison.Ordinal));
                await oldGeneration.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(oldGeneration.IsCompletedSuccessfully);
                Assert.Empty(packageSessions.ActiveSession.LoadedPackageMap);
                var faulted = Assert.Single(packageSessions.ActiveSession.GetSessionPackages());
                Assert.Equal(PackageReadinessState.Failed, faulted.Readiness);
                var denied = Assert.Single(lifecyclePolicies.CaptureSnapshot().Entries);
                Assert.Equal(2, denied.RuntimePolicyRevision);
                Assert.Equal(
                    conflictCode == ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied
                        ? ExecutionRuntimeTrustDispositions.Denied
                        : ExecutionRuntimeTrustDispositions.Trusted,
                    denied.TrustDisposition);
                Assert.Equal(authoritativeLifecycle.State, denied.Lifecycle.State);
            }
            else
            {
                Assert.True(applicationLifetime.StopRequested);
                Assert.Contains("shutdown was requested", result.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(oldProcess.HasExited);
                Assert.Single(packageSessions.ActiveSession.LoadedPackageMap);
                var trusted = Assert.Single(lifecyclePolicies.CaptureSnapshot().Entries);
                Assert.Equal(1, trusted.RuntimePolicyRevision);
                Assert.Equal(ExecutionRuntimeTrustDispositions.Trusted, trusted.TrustDisposition);
            }
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryUpdate_KnownRevocationStopsActiveWorkerAndFaultsPackageWithoutDeletingData()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.runtime.revoked.active";
            const string sharedRuntimePackageId = "test.runtime.revoked.shared";
            const string unaffectedPackageId = "test.runtime.supported.active";
            var now = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
            var paths = new RuntimePackagePaths(root);
            var installedStore = new InstalledPackageStore(paths);
            var installed = WriteInstalledPackage(
                paths,
                packageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('a', 64)));
            var sharedRuntime = WriteInstalledPackage(
                paths,
                sharedRuntimePackageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    sharedRuntimePackageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('d', 64)));
            var unaffected = WriteInstalledPackage(
                paths,
                unaffectedPackageId,
                "1.0.0",
                new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    origin,
                    unaffectedPackageId,
                    RequestedTag: "latest",
                    SourceIdentity: new string('c', 64)));
            var binding = Assert.Single(installed.ExecutionRuntimeBindings);
            sharedRuntime = sharedRuntime with { ExecutionRuntimeBindings = [binding] };
            var unaffectedBinding = Assert.Single(unaffected.ExecutionRuntimeBindings) with
            {
                RuntimeVersion = "10.0.2",
                RuntimePackagingRevision = 2,
                RuntimeManifestSha256 = new string('c', 64),
                RuntimeArtifactSha256 = new string('d', 64),
                RuntimeArtifactSize = 104857601,
            };
            unaffected = unaffected with { ExecutionRuntimeBindings = [unaffectedBinding] };
            await SeedExistingActivePolicyAsync(paths, unaffected);
            await installedStore.WriteAsync([installed, sharedRuntime, unaffected]);
            var item = await CreateRuntimeActionItemAsync(installed, RegistryPackageActionKinds.Keep);
            var targetBinding = Assert.Single(item.RuntimeBindings);
            var receipt = CreateRuntimeReceipt(binding, now);
            var unaffectedReceipt = CreateRuntimeReceipt(unaffectedBinding, now);
            var revoked = RevokedRuntimeLifecycle(now);
            var planRequests = 0;
            var releaseRequests = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    releaseRequests++;
                    return RuntimeReleaseResponse(receipt, revoked, runtimePolicyRevision: 2, now);
                }
                planRequests++;
                return RuntimeConflictPlanResponse(
                    new RegistryExecutionRuntimeResolutionConflict(
                        ExecutionRuntimeErrorCodes.Revoked,
                        "The currently bound execution runtime is revoked.",
                        targetBinding.Requirement,
                        targetBinding.Rid,
                        packageId,
                        targetBinding.Role,
                        targetBinding.Runtime),
                    now);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver(enforceLifecyclePolicy: true);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();

            var runtimeStore = provider.GetRequiredService<ExecutionRuntimeStore>();
            await WriteRuntimeReceiptAsync(
                runtimeStore,
                provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
                receipt);
            await WriteRuntimeReceiptAsync(
                runtimeStore,
                provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
                unaffectedReceipt);
            var lifecyclePolicies = provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>();
            await lifecyclePolicies.ObserveAsync([
                new ExecutionRuntimeLifecyclePolicyObservation(
                    receipt,
                    origin,
                    SupportedRuntimeLifecycle(now),
                    RuntimePolicyRevision: 1,
                    MinimumHostTrustGeneration: 1,
                    PolicyObservedAtUtc: now.AddMinutes(-1)),
                new ExecutionRuntimeLifecyclePolicyObservation(
                    unaffectedReceipt,
                    origin,
                    SupportedRuntimeLifecycle(now),
                    RuntimePolicyRevision: 1,
                    MinimumHostTrustGeneration: 1,
                    PolicyObservedAtUtc: now.AddMinutes(-1)),
            ]);
            var dataRoot = Path.Combine(paths.PackageDataRootPath, packageId, "data");
            Directory.CreateDirectory(dataRoot);
            var dataPath = Path.Combine(dataRoot, "preserved.txt");
            await File.WriteAllTextAsync(dataPath, "preserve-me");

            var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            await lifecycle.InitializeAsync();
            var loaded = await lifecycle.LoadInstalledPackagesAsync();
            Assert.True(loaded.Success, loaded.Message);
            var packageSessions = provider.GetRequiredService<PackageSessionState>();
            Assert.True(packageSessions.ActiveSession.LoadedPackageMap.Count == 3,
                string.Join(" | ", packageSessions.GetSessionPackages().Select(value => value.PackageId + ": " + value.LastError)));
            var active = packageSessions.ActiveSession.LoadedPackageMap[packageId];
            var sharedRuntimeActive = packageSessions.ActiveSession.LoadedPackageMap[sharedRuntimePackageId];
            var unaffectedActive = packageSessions.ActiveSession.LoadedPackageMap[unaffectedPackageId];
            var oldGeneration = active.Worker.GenerationCompletion;
            var sharedRuntimeGeneration = sharedRuntimeActive.Worker.GenerationCompletion;
            var oldProcess = WorkerReleaseMetrics.GetProcess(active.Worker);
            var sharedRuntimeProcess = WorkerReleaseMetrics.GetProcess(sharedRuntimeActive.Worker);
            var unaffectedProcess = WorkerReleaseMetrics.GetProcess(unaffectedActive.Worker);
            Assert.False(oldProcess.HasExited);
            Assert.False(sharedRuntimeProcess.HasExited);
            Assert.False(unaffectedProcess.HasExited);
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;

            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().UpdateAsync(
                new RuntimeRegistryUpdateRequest(RegistryOrigin: origin, PackageId: packageId),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.Conflict, result.ErrorCode);
            Assert.Contains("revoked", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(result.Warnings, warning => warning.Contains("Stopped and faulted", StringComparison.Ordinal));
            Assert.Equal(1, planRequests);
            Assert.Equal(1, releaseRequests);
            await oldGeneration.WaitAsync(TimeSpan.FromSeconds(5));
            await sharedRuntimeGeneration.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(oldGeneration.IsCompletedSuccessfully);
            Assert.True(sharedRuntimeGeneration.IsCompletedSuccessfully);
            var remaining = Assert.Single(packageSessions.ActiveSession.LoadedPackageMap);
            Assert.Equal(unaffectedPackageId, remaining.Key);
            Assert.Same(unaffectedActive.Worker, remaining.Value.Worker);
            Assert.False(unaffectedProcess.HasExited);
            var faulted = Assert.Single(
                packageSessions.ActiveSession.GetSessionPackages(),
                package => package.PackageId == packageId);
            Assert.Equal(packageId, faulted.PackageId);
            Assert.Equal(PackageReadinessState.Failed, faulted.Readiness);
            Assert.Contains("lifecycle policy", faulted.LastError, StringComparison.OrdinalIgnoreCase);
            var sharedRuntimeFaulted = Assert.Single(
                packageSessions.ActiveSession.GetSessionPackages(),
                package => package.PackageId == sharedRuntimePackageId);
            Assert.Equal(PackageReadinessState.Failed, sharedRuntimeFaulted.Readiness);
            Assert.Contains("lifecycle policy", sharedRuntimeFaulted.LastError, StringComparison.OrdinalIgnoreCase);
            var stillReady = Assert.Single(
                packageSessions.ActiveSession.GetSessionPackages(),
                package => package.PackageId == unaffectedPackageId);
            Assert.Equal(PackageReadinessState.Ready, stillReady.Readiness);
            Assert.Equal("preserve-me", await File.ReadAllTextAsync(dataPath));
            Assert.Equal(catalogGeneration + 1, coordinator.CatalogGeneration);
            var persistedPackages = (await installedStore.ListAsync())
                .ToDictionary(static package => package.PackageId, StringComparer.OrdinalIgnoreCase);
            foreach (var affectedPackageId in new[] { packageId, sharedRuntimePackageId })
            {
                var persistedBinding = Assert.Single(persistedPackages[affectedPackageId].ExecutionRuntimeBindings);
                Assert.Equal(2, persistedBinding.RuntimePolicyRevision);
                Assert.Equal(ExecutionRuntimeLifecycleStates.Revoked, persistedBinding.RuntimeLifecycleState);
                Assert.Equal(now, persistedBinding.PolicyObservedAtUtc);
                Assert.Equal(now, persistedBinding.ResolvedAtUtc);
            }
            Assert.Equal(
                unaffectedBinding,
                Assert.Single(persistedPackages[unaffectedPackageId].ExecutionRuntimeBindings));
            Assert.Empty(Directory.EnumerateDirectories(paths.TransactionRootPath));
            var cached = Assert.Single(
                lifecyclePolicies.CaptureSnapshot().Entries,
                policy => policy.Identity == receipt.Identity);
            Assert.Equal(2, cached.RuntimePolicyRevision);
            Assert.Equal(ExecutionRuntimeLifecycleStates.Revoked, cached.Lifecycle.State);
            var unaffectedCached = Assert.Single(
                lifecyclePolicies.CaptureSnapshot().Entries,
                policy => policy.Identity == unaffectedReceipt.Identity);
            Assert.Equal(1, unaffectedCached.RuntimePolicyRevision);
            Assert.Equal(ExecutionRuntimeLifecycleStates.Supported, unaffectedCached.Lifecycle.State);
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryUpdate_IncludePrereleaseOverrideIsResolverOnlyAndPreservesStoredPolicy()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.prerelease";
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            await store.WriteAsync([
                WriteInstalledPackage(
                    paths,
                    packageId,
                    "1.0.0",
                    new InstalledPackageProvenanceRecord(
                        InstalledPackageSourceKind.Registry,
                        InstalledPackageVersionPolicy.FollowTag,
                        origin,
                        packageId,
                        RequestedTag: "latest",
                        SourceIdentity: new string('a', 64),
                        IncludePrerelease: false)),
            ]);
            var artifact = await CreateRegistryArtifactAsync(
                root,
                "prerelease-update",
                origin,
                packageId,
                currentVersion: "1.0.0",
                version: "2.0.0-beta.1");
            RegistryResolvePackageChangesRequest? captured = null;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return artifact.CreateResponse(request.RequestUri!);
                }
                captured = request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult();
                return PlanResponse(artifact.PlanItem, exactBinding: Assert.Single(store.ListAsync().GetAwaiter().GetResult().Single().ExecutionRuntimeBindings));
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().UpdateAsync(
                new RuntimeRegistryUpdateRequest(IncludePrerelease: true),
                CancellationToken.None);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.True(captured?.IncludePrerelease);
            Assert.False(captured?.Reinstall);
            var installed = Assert.Single(await store.ListAsync());
            Assert.Equal("2.0.0-beta.1", installed.Version);
            Assert.Equal(origin, installed.Provenance?.RegistryOrigin);
            Assert.Equal("latest", installed.Provenance?.RequestedTag);
            Assert.False(installed.Provenance?.IncludePrerelease);
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryInstall_PersistsExplicitPrereleasePolicy()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.install.policy";
            var paths = new RuntimePackagePaths(root);
             var artifact = await CreateRegistryArtifactAsync(
                 root,
                 "explicit-install",
                 origin,
                 packageId,
                 currentVersion: null,
                 version: "1.0.0-beta.1",
                 runtimeIdentifiers: SunderPackageFormat.SupportedRuntimeIdentifiers);
            var handler = new DelegateHandler(request => request.Method == HttpMethod.Get
                ? artifact.CreateResponse(request.RequestUri!)
                : PlanResponse(artifact.PlanItem));
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().InstallAsync(
                new RuntimeRegistryPackageRequest(
                    origin,
                    packageId,
                    Tag: "beta",
                    IncludePrerelease: true),
                CancellationToken.None);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var installed = Assert.Single(await new InstalledPackageStore(paths).ListAsync());
            Assert.Equal(InstalledPackageVersionPolicy.FollowTag, installed.Provenance?.VersionPolicy);
            Assert.Equal("beta", installed.Provenance?.RequestedTag);
            Assert.True(installed.Provenance?.IncludePrerelease);
            Assert.Equal(artifact.PlanItem.PackageContentIdentity, installed.Provenance?.SourceIdentity);
            Assert.NotEqual(installed.ContentIdentity, installed.Provenance?.SourceIdentity);
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryInstall_SensitiveCapabilityConsentUsesVerifiedUniversalSourceManifestFence()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.install.sensitive";
            var paths = new RuntimePackagePaths(root);
            var artifact = await CreateRegistryArtifactAsync(
                root,
                "sensitive-install",
                origin,
                packageId,
                currentVersion: null,
                version: "1.0.0",
                runtimeIdentifiers: SunderPackageFormat.SupportedRuntimeIdentifiers,
                runtimeRequiredHostCapabilities:
                [
                    SunderSdkCapabilities.Baseline11V1,
                    SunderSdkCapabilities.CoreV1,
                    SunderSdkCapabilities.DockerEnvironmentV1,
                    SunderSdkCapabilities.RpcV1,
                    SunderWorkerProtocol.V1Capability,
                ]);
            var handler = new DelegateHandler(request => request.Method == HttpMethod.Get
                ? artifact.CreateResponse(request.RequestUri!)
                : PlanResponse(artifact.PlanItem));
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var orchestrator = provider.GetRequiredService<RegistryPackageChangeOrchestrator>();
            var plan = await orchestrator.ResolveAsync(
                new RuntimeRegistryPackageBatchRequest(
                    origin,
                    [new RuntimeRegistryPackageChangeRequest(packageId, "1.0.0", [])]),
                CancellationToken.None);
            var consent = Assert.Single(plan.RequiredCapabilityConsents);

            Assert.Equal(artifact.PlanItem.Artifacts[0].ManifestSha256, consent.ManifestSha256);
            Assert.Equal([SunderSdkCapabilities.DockerEnvironmentV1], consent.Capabilities);

            var denied = await orchestrator.InstallAsync(
                new RuntimeRegistryPackageRequest(origin, packageId, Version: "1.0.0", Tag: null),
                CancellationToken.None);
            Assert.Equal(RuntimeRegistryErrorCode.ConsentRequired, denied.ErrorCode);

            var result = await orchestrator.InstallAsync(
                new RuntimeRegistryPackageRequest(
                    origin,
                    packageId,
                    Version: "1.0.0",
                    Tag: null,
                    CapabilityConsents: [consent]),
                CancellationToken.None);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var installed = Assert.Single(await new InstalledPackageStore(paths).ListAsync());
            var grant = Assert.Single(installed.CapabilityGrants!);
            Assert.Equal(SunderSdkCapabilities.DockerEnvironmentV1, grant.Capability);
            Assert.Equal(PackageSensitiveCapabilityPolicy.GetManifestSha256(installed), grant.ManifestSha256);
            Assert.Equal(consent.ManifestSha256, grant.ManifestSha256);
            Assert.All(installed.AcquiredTargets!, target => Assert.Equal(PackageTargetSelection.GetCurrentRuntimeIdentifier(), target.Rid));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryExecution_BlockedResolutionRejectsConcurrentSourceAdoptionWithoutMutation()
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string firstOrigin = "https://first.example/";
            const string adoptedOrigin = "https://adopted.example/";
            const string packageId = "test.concurrent.adoption";
            var firstFingerprint = new string('a', 64);
            var adoptedFingerprint = new string('b', 64);
            var paths = new RuntimePackagePaths(root);
            var store = new InstalledPackageStore(paths);
            await store.WriteAsync([
                WriteInstalledPackage(
                    paths,
                    packageId,
                    "1.0.0",
                    InstalledPackageProvenanceRecord.Unknown),
            ]);
            var firstArtifact = await CreateRegistryArtifactAsync(
                root,
                "first-adoption",
                firstOrigin,
                packageId,
                currentVersion: "1.0.0",
                version: "1.0.0");
            var adoptedArtifact = await CreateRegistryArtifactAsync(
                root,
                "winning-adoption",
                adoptedOrigin,
                packageId,
                currentVersion: "1.0.0",
                version: "1.0.0");
            var firstResolutionBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstResolution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new AsyncDelegateHandler(async (request, cancellationToken) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    if (firstArtifact.TryCreateResponse(request.RequestUri!, out var response)
                        || adoptedArtifact.TryCreateResponse(request.RequestUri!, out response))
                    {
                        return response;
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (request.RequestUri!.Host == "first.example")
                {
                    firstResolutionBlocked.TrySetResult();
                    await releaseFirstResolution.Task.WaitAsync(cancellationToken);
                    return PlanResponse(firstArtifact.PlanItem, resolutionFingerprint: firstFingerprint);
                }
                return PlanResponse(adoptedArtifact.PlanItem, resolutionFingerprint: adoptedFingerprint);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var orchestrator = provider.GetRequiredService<RegistryPackageChangeOrchestrator>();
            var staleTask = orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    packageId,
                    firstOrigin,
                    Tag: "latest",
                    Confirm: true,
                    ExpectedResolutionFingerprint: firstFingerprint),
                CancellationToken.None);
            await firstResolutionBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            RuntimeRegistryPackageChangeResult adopted;
            try
            {
                adopted = await orchestrator.AdoptSourceAsync(
                    new RuntimeRegistrySourceAdoptionRequest(
                        packageId,
                        adoptedOrigin,
                        Tag: "beta",
                        IncludePrerelease: true,
                        Confirm: true,
                        ExpectedResolutionFingerprint: adoptedFingerprint),
                    CancellationToken.None);
            }
            finally
            {
                releaseFirstResolution.TrySetResult();
            }
            var stale = await staleTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(adopted.Success, string.Join(Environment.NewLine, adopted.Errors));
            Assert.False(stale.Success);
            Assert.Equal(RuntimeRegistryErrorCode.Conflict, stale.ErrorCode);
            Assert.Contains("stale", stale.Message, StringComparison.OrdinalIgnoreCase);
            var installed = Assert.Single(await store.ListAsync());
            Assert.Equal("1.0.0", installed.Version);
            Assert.Equal(adoptedOrigin, installed.Provenance?.RegistryOrigin);
            Assert.Equal("beta", installed.Provenance?.RequestedTag);
            Assert.True(installed.Provenance?.IncludePrerelease);
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RegistryPlanProvenance_DistinguishesDirectPinsFromMovableTransitiveDependencies()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new RuntimePackagePaths(root);
            var origin = new Uri("https://registry.example/");
            var tagRequest = new RuntimeRegistryPackageBatchRequest(
                origin.AbsoluteUri,
                [new RuntimeRegistryPackageChangeRequest("test.parent", null, [], "latest")]);
            var initial = RegistryPackageChangeOrchestrator.BuildProvenanceByPackageId(
                origin,
                tagRequest,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                [
                    PlanItem("test.parent", null, "1.0.0"),
                    PlanItem("test.dependency", null, "1.0.0"),
                ],
                new Dictionary<string, InstalledPackageRecord>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal(InstalledPackageVersionPolicy.FollowTag, initial["test.parent"].VersionPolicy);
            Assert.Equal(InstalledPackageVersionPolicy.TransitiveDependency, initial["test.dependency"].VersionPolicy);
            Assert.Null(initial["test.dependency"].RequestedVersion);

            var explicitSelection = RegistryPackageChangeOrchestrator.BuildProvenanceByPackageId(
                origin,
                new RuntimeRegistryPackageBatchRequest(
                    origin.AbsoluteUri,
                    [new RuntimeRegistryPackageChangeRequest("test.pinned", "1.0.0", [])]),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                [PlanItem("test.pinned", null, "1.0.0")],
                new Dictionary<string, InstalledPackageRecord>(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(InstalledPackageVersionPolicy.ExplicitVersion, explicitSelection["test.pinned"].VersionPolicy);
            Assert.Equal("1.0.0", explicitSelection["test.pinned"].RequestedVersion);

            var dependency = InstalledPackageStoreTests.CreatePackage(paths, "test.dependency") with
            {
                Provenance = initial["test.dependency"] with { SourceIdentity = new string('a', 64) },
            };
            var installed = new Dictionary<string, InstalledPackageRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [dependency.PackageId] = dependency,
            };
            var moved = RegistryPackageChangeOrchestrator.BuildProvenanceByPackageId(
                origin,
                tagRequest,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                [PlanItem("test.dependency", "1.0.0", "2.0.0")],
                installed);

            Assert.Equal(InstalledPackageVersionPolicy.TransitiveDependency, moved["test.dependency"].VersionPolicy);

            installed[dependency.PackageId] = dependency with
            {
                Provenance = new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.ExplicitVersion,
                    origin.AbsoluteUri,
                    dependency.PackageId,
                    RequestedVersion: "1.0.0",
                    SourceIdentity: new string('a', 64)),
            };
            var error = Assert.Throws<InvalidDataException>(() =>
                RegistryPackageChangeOrchestrator.BuildProvenanceByPackageId(
                    origin,
                    tagRequest,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    [PlanItem("test.dependency", "1.0.0", "2.0.0")],
                    installed));
            Assert.Contains("explicitly pinned", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static RegistryPackageInstallPlanItem PlanItem(string packageId, string? currentVersion, string version)
            => new(
                packageId,
                currentVersion,
                version,
                new string('b', 64),
                currentVersion is null
                    ? RegistryPackageActionKinds.PackageInstall
                    : RegistryPackageActionKinds.PackageUpdate,
                null,
                [],
                [],
                []);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("dependency")]
    [InlineData("runtime-policy")]
    public async Task RegistrySourceAdoption_StalePreviewIsRejectedBeforeDownloadsOrStaging(
        string changedAuthority)
    {
        var root = CreateTempDirectory();
        ServiceProvider? provider = null;
        try
        {
            const string origin = "https://registry.example/";
            const string packageId = "test.source.adoption.stale";
            var previewFingerprint = new string('a', 64);
            var currentFingerprint = new string('b', 64);
            var paths = new RuntimePackagePaths(root);
            var artifact = await CreateRegistryArtifactAsync(
                root,
                $"source-adoption-stale-{changedAuthority}",
                origin,
                packageId,
                currentVersion: "1.0.0",
                version: "1.0.0");
            var installPath = paths.GetInstalledPackagePath(packageId, "1.0.0");
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                installPath,
                packageId,
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
            var store = new InstalledPackageStore(paths);
            await store.WriteAsync([
                CanonicalPackageTestBuilder.CreateInstalledRecord(installPath, packageId, "1.0.0")
                    with { Provenance = InstalledPackageProvenanceRecord.Unknown },
            ]);
            var currentItem = changedAuthority switch
            {
                "tag" => artifact.PlanItem with
                {
                    Version = "2.0.0",
                    PackageContentIdentity = new string('c', 64),
                },
                "dependency" => new RegistryPackageInstallPlanItem(
                    artifact.PlanItem.PackageId,
                    artifact.PlanItem.CurrentVersion,
                    artifact.PlanItem.Version,
                    artifact.PlanItem.PackageContentIdentity,
                    artifact.PlanItem.ActionKind,
                    artifact.PlanItem.DeprecatedMessage,
                    artifact.PlanItem.DependsOn.Concat([
                        new RegistryPackageDependency("test.new.dependency", ">=1.0.0"),
                    ]).ToArray(),
                    artifact.PlanItem.Targets,
                    artifact.PlanItem.Artifacts,
                    artifact.PlanItem.RuntimeBindings,
                    artifact.PlanItem.RuntimeOnlyRebind),
                _ => artifact.PlanItem,
            };
            var resolutionRequests = new List<RegistryResolvePackageChangesRequest>();
            var artifactGets = 0;
            var handler = new DelegateHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    artifactGets++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                resolutionRequests.Add(request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!);
                var preview = resolutionRequests.Count == 1;
                return PlanResponse(
                    preview ? artifact.PlanItem : currentItem,
                    resolutionFingerprint: preview ? previewFingerprint : currentFingerprint,
                    runtimePolicyRevision: !preview && changedAuthority == "runtime-policy" ? 2 : 1);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);
            provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var orchestrator = provider.GetRequiredService<RegistryPackageChangeOrchestrator>();

            var preview = await orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    packageId,
                    origin,
                    Tag: "latest",
                    DryRun: true),
                CancellationToken.None);
            var expectedPlan = Assert.Single(preview.PlanExpectations);
            Assert.Equal(previewFingerprint, expectedPlan.ExpectedResolutionFingerprint);
            var coordinator = provider.GetRequiredService<PackageStoreCoordinator>();
            var catalogGeneration = coordinator.CatalogGeneration;
            var catalogJson = await File.ReadAllTextAsync(paths.StateFilePath);
            var packageStagingBefore = CaptureFileTree(paths.StagingRootPath);
            var packageTransactionsBefore = CaptureFileTree(paths.TransactionRootPath);
            var runtimeStagingBefore = CaptureFileTree(paths.ExecutionRuntimeStagingRootPath);
            var runtimeTransactionsBefore = CaptureFileTree(paths.ExecutionRuntimeTransactionRootPath);

            var result = await orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    packageId,
                    origin,
                    Tag: "latest",
                    Confirm: true,
                    ExpectedResolutionFingerprint: expectedPlan.ExpectedResolutionFingerprint),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.StalePlan, result.ErrorCode);
            Assert.Equal(0, artifactGets);
            Assert.Equal(
                new string?[] { null, previewFingerprint },
                resolutionRequests.Select(static request => request.ExpectedResolutionFingerprint).ToArray());
            Assert.Equal(catalogGeneration, coordinator.CatalogGeneration);
            Assert.Equal(catalogJson, await File.ReadAllTextAsync(paths.StateFilePath));
            Assert.Equal(InstalledPackageSourceKind.Unknown, (await store.GetAsync(packageId))?.Provenance?.SourceKind);
            Assert.Equal(packageStagingBefore, CaptureFileTree(paths.StagingRootPath));
            Assert.Equal(packageTransactionsBefore, CaptureFileTree(paths.TransactionRootPath));
            Assert.Equal(runtimeStagingBefore, CaptureFileTree(paths.ExecutionRuntimeStagingRootPath));
            Assert.Equal(runtimeTransactionsBefore, CaptureFileTree(paths.ExecutionRuntimeTransactionRootPath));
        }
        finally
        {
            if (provider is not null)
            {
                await ShutdownPackageLifecycleAsync(provider);
                await provider.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistrySourceAdoption_DryRunDoesNotMutateAndConfirmedRequestAllowsReplacementPlanning()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new RuntimePackagePaths(root);
            var installPath = paths.GetInstalledPackagePath("test.local", "1.0.0");
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                installPath,
                "test.local",
                "1.0.0",
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
            var store = new InstalledPackageStore(paths);
            await store.WriteAsync([
                CanonicalPackageTestBuilder.CreateInstalledRecord(installPath, "test.local", "1.0.0")
                    with { Provenance = InstalledPackageProvenanceRecord.Unknown },
            ]);
            var resolutionFingerprint = new string('f', 64);
            var captured = new List<RegistryResolvePackageChangesRequest>();
            var handler = new DelegateHandler(request =>
            {
                captured.Add(request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult()!);
                var now = DateTimeOffset.UtcNow;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                        true,
                        [new RegistryPackageInstallPlanItem(
                            "test.local",
                            "1.0.0",
                            "1.0.0",
                            new string('b', 64),
                            RegistryPackageActionKinds.PackageUpdate,
                            null,
                            [],
                            [],
                            [])],
                        [],
                        [],
                        [],
                        [],
                        ResolutionFingerprint: resolutionFingerprint,
                        RuntimePolicyRevision: 1,
                        ExpiresAtUtc: now.AddMinutes(5))),
                };
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var orchestrator = provider.GetRequiredService<RegistryPackageChangeOrchestrator>();
            var unconfirmed = await orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    "test.local",
                    "https://registry.example/",
                    Tag: "latest"),
                CancellationToken.None);
            Assert.Equal(RuntimeRegistryErrorCode.InvalidRequest, unconfirmed.ErrorCode);
            Assert.Empty(captured);

            var invalidFingerprint = await orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    "test.local",
                    "https://registry.example/",
                    Tag: "latest",
                    Confirm: true,
                    ExpectedResolutionFingerprint: new string('A', 64)),
                CancellationToken.None);
            Assert.Equal(RuntimeRegistryErrorCode.InvalidRequest, invalidFingerprint.ErrorCode);
            Assert.Empty(captured);

            var preview = await orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    "test.local",
                    "https://registry.example/",
                    Tag: "latest",
                    DryRun: true),
                CancellationToken.None);
            Assert.True(preview.Success, preview.Message);
            Assert.False(preview.RuntimeSessionApplied);
            var expectedPlan = Assert.Single(preview.PlanExpectations);
            Assert.Equal("https://registry.example/", expectedPlan.RegistryOrigin);
            Assert.Equal(resolutionFingerprint, expectedPlan.ExpectedResolutionFingerprint);
            Assert.Equal(InstalledPackageSourceKind.Unknown, (await store.GetAsync("test.local"))?.Provenance?.SourceKind);

            var result = await orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    "test.local",
                    "https://registry.example/",
                    Tag: "latest",
                    Confirm: true,
                    ExpectedResolutionFingerprint: expectedPlan.ExpectedResolutionFingerprint),
                CancellationToken.None);

            Assert.All(captured, static request => Assert.True(request.Reinstall));
            Assert.Equal(
                new string?[] { null, resolutionFingerprint },
                captured.Select(static request => request.ExpectedResolutionFingerprint).ToArray());
            Assert.Equal(RuntimeRegistryErrorCode.ArtifactVerificationFailed, result.ErrorCode);
            Assert.Empty(result.SkippedPackageIds);
            Assert.DoesNotContain("adoption is required", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryPackagePlan_DoesNotDeserializeTypedFailureResponse()
    {
        var root = CreateTempDirectory();
        try
        {
            var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(true, [], [], [], [], [])),
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(
                new RuntimePackagePaths(root),
                new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var orchestrator = provider.GetRequiredService<RegistryPackageChangeOrchestrator>();
            var plan = await orchestrator.ResolveAsync(
                new RuntimeRegistryPackageBatchRequest(
                    "https://registry.example/",
                    [new RuntimeRegistryPackageChangeRequest("agent", null, [], "latest")]),
                CancellationToken.None);

            Assert.False(plan.Success);
            Assert.Empty(plan.Items);
            Assert.Equal(["Registry rejected the request."], plan.Errors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "1.0.0", false, false)]
    [InlineData("1.0.0", "2.0.0", false, false)]
    [InlineData("1.0.0", "1.0.0", false, true)]
    [InlineData("1.0.0", "2.0.0", true, true)]
    public void RegistryPackageExecution_ReinstallsSameVersionProjectionAcquisition(
        string? currentVersion,
        string version,
        bool requested,
        bool expected)
        => Assert.Equal(
            expected,
            RegistryPackageChangeOrchestrator.RequiresReinstall(currentVersion, version, requested));

    [Fact]
    public async Task RegistryPackageExecution_RejectsUnadvertisedArtifactOriginBeforeDownload()
    {
        var root = CreateTempDirectory();
        try
        {
            var requestCount = 0;
            var handler = new DelegateHandler(_ =>
            {
                requestCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                        true,
                        [new RegistryPackageInstallPlanItem(
                            "agent",
                            null,
                            "1.0.0",
                            new string('e', 64),
                            RegistryPackageActionKinds.PackageInstall,
                            null,
                            [],
                            [],
                            [new RegistryPackageProjectionArtifact(
                                "shared",
                                null,
                                new string('a', 64),
                                1,
                                "https://untrusted.example/agent.shared.sunderpkg",
                                new string('b', 64),
                                new string('c', 64),
                                new string('d', 64),
                                1)])],
                        [],
                        [],
                        [],
                        [],
                        RuntimeArtifacts: [],
                        RuntimeConflicts: [],
                        ResolutionFingerprint: "test-untrusted-artifact-fingerprint",
                        RuntimePolicyRevision: 1,
                        ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5))),
                };
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(
                new RuntimePackagePaths(root),
                new RuntimeBearerTokenValidator("test-runtime-token"));
            services.UseTestExecutionRuntimeLaunchResolver();
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().ExecuteAsync(
                new RuntimeRegistryPackageBatchRequest(
                    "https://registry.example/",
                    [new RuntimeRegistryPackageChangeRequest("agent", null, [], "latest")]),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.ArtifactVerificationFailed, result.ErrorCode);
            Assert.Contains("explicitly advertised trusted artifact origin", result.Message, StringComparison.Ordinal);
            Assert.Equal(1, requestCount);
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
        var files = Directory.EnumerateFiles(
                Path.Combine(root, "languages", "csharp", "dotnet", "host", "Sunder.App"),
                "*.cs",
                SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(root, "languages", "csharp", "dotnet", "host", "Sunder.Cli"),
                "*.cs",
                SearchOption.AllDirectories));

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
        var app = File.ReadAllText(Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.App",
            "Services",
            "RegistryPackageInstallService.cs"));
        var cliDirectory = Path.Combine(root, "languages", "csharp", "dotnet", "host", "Sunder.Cli");
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

    private static InstalledPackageRecord WriteInstalledPackage(
        RuntimePackagePaths paths,
        string packageId,
        string version,
        InstalledPackageProvenanceRecord provenance)
    {
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var installPath = paths.GetInstalledPackagePath(packageId, version);
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            installPath,
            packageId,
            version,
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        var record = CanonicalPackageTestBuilder.CreateInstalledRecord(installPath, packageId, version) with
        {
            Provenance = provenance,
        };
        SeedExistingActivePolicyAsync(paths, record).GetAwaiter().GetResult();
        return record;
    }

    private static RuntimeRegistryPackageBatchRequest RuntimeActionRequest(string origin, string packageId)
        => new(
            origin,
            [new RuntimeRegistryPackageChangeRequest(packageId, null, [], "latest")]);

    private static async Task<RegistryPackageInstallPlanItem> CreateRuntimeActionItemAsync(
        InstalledPackageRecord package,
        string actionKind)
    {
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(package.InstallPath);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var manifest = Assert.IsType<SunderPackageManifest>(validation.Manifest);
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var target = Assert.Single(
            manifest.Targets ?? [],
            candidate => candidate is
            {
                Role: SunderPackageFormat.RuntimeHostRole,
                Kind: SunderPackageFormat.WorkerTargetKind,
                ExecutionRuntime: not null,
            } && string.Equals(candidate.Rid, rid, StringComparison.Ordinal));
        var previous = Assert.Single(RegistryPackagePlanResolver.ProjectRuntimeBindings(package, manifest));
        var next = previous with
        {
            Runtime = previous.Runtime with
            {
                PackagingRevision = previous.Runtime.PackagingRevision + 1,
            },
        };
        var selected = actionKind switch
        {
            RegistryPackageActionKinds.Keep => previous,
            RegistryPackageActionKinds.RuntimeOnlyRebind => next,
            _ => throw new ArgumentOutOfRangeException(nameof(actionKind)),
        };
        var rebind = actionKind == RegistryPackageActionKinds.RuntimeOnlyRebind
            ? new RegistryRuntimeOnlyRebindDetails(
                package.Version,
                package.Provenance.SourceIdentity!,
                [previous],
                [next])
            : null;
        return new RegistryPackageInstallPlanItem(
            package.PackageId,
            package.Version,
            package.Version,
            package.Provenance.SourceIdentity!,
            actionKind,
            null,
            package.DependsOn.Select(dependency => new RegistryPackageDependency(
                dependency.PackageId,
                dependency.VersionRange)).ToArray(),
            [new RegistryPackageTarget(
                target!.Role!,
                target.Rid!,
                target.Kind!,
                target.EntryPoint!,
                target.TargetFramework,
                target.SdkVersion,
                (target.RequiredHostCapabilities ?? []).Where(static capability => capability is not null).Select(static capability => capability!).ToArray(),
                ExecutionRuntime: selected.Requirement)],
            [],
            [selected],
            rebind);
    }

    private static string GetOnlyPendingStageId(IServiceProvider provider)
    {
        var owner = provider.GetRequiredService<RuntimeSessionOwner>();
        var field = typeof(RuntimeSessionOwner).GetField(
            "_stageGenerations",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(RuntimeSessionOwner).FullName, "_stageGenerations");
        var stages = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, long>>>(field.GetValue(owner));
        return Assert.Single(stages).Key;
    }

    private static async Task<RegistryArtifactFixture> CreateRegistryArtifactAsync(
        string root,
        string name,
        string origin,
        string packageId,
        string? currentVersion,
        string version,
        IReadOnlyList<string>? runtimeIdentifiers = null,
        IReadOnlyList<string>? runtimeRequiredHostCapabilities = null)
    {
        RuntimeLocalState.EnsureInitialized(root);
        var artifactRoot = Path.Combine(root, "registry-artifacts", name);
        var sourceRoot = Path.Combine(artifactRoot, "source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            sourceRoot,
            packageId,
            version,
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            runtimeIdentifiers: runtimeIdentifiers,
            runtimeRequiredHostCapabilities: runtimeRequiredHostCapabilities);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(sourceRoot);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var sourceArchiveSha256 = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{packageId}:{version}:{name}")))
            .ToLowerInvariant();
        var packageContentIdentity = PackageSessionPreparer.ComputeContentIdentity(sourceRoot);
        var rid = RuntimeInformation.RuntimeIdentifier;
        var keys = new[]
        {
            SunderPackageProjectionKey.Shared,
            SunderPackageProjectionKey.Runtime(rid),
            SunderPackageProjectionKey.App(rid),
        };
        var artifacts = new List<RegistryPackageProjectionArtifact>(keys.Length);
        var contentByUrl = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var path = Path.Combine(artifactRoot, $"{key.Kind}-{key.Rid ?? "none"}.sunderprojection");
            var descriptor = await SunderPackageProjectionArchiveWriter.WriteAsync(
                sourceRoot,
                validation.Manifest!,
                validation.ContentIndex!,
                sourceArchiveSha256,
                key,
                path);
            var content = await File.ReadAllBytesAsync(path);
            var downloadUrl = new Uri(
                RegistryOrigin.Normalize(origin),
                $"artifacts/{Uri.EscapeDataString(name)}-{key.Kind}-{key.Rid ?? "none"}.sunderprojection")
                .AbsoluteUri;
            contentByUrl.Add(downloadUrl, content);
            artifacts.Add(new RegistryPackageProjectionArtifact(
                key.Kind,
                key.Rid,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                content.LongLength,
                downloadUrl,
                descriptor.SourceArchiveSha256!,
                descriptor.ManifestSha256!,
                descriptor.ProjectionSha256!,
                descriptor.ProjectionFormatVersion));
        }

        return new RegistryArtifactFixture(
            new RegistryPackageInstallPlanItem(
                packageId,
                currentVersion,
                version,
                packageContentIdentity,
                currentVersion is null
                    ? RegistryPackageActionKinds.PackageInstall
                    : RegistryPackageActionKinds.PackageUpdate,
                null,
                [],
                validation.Manifest!.Targets!
                    .Where(static target => target is not null)
                    .Select(static target => new RegistryPackageTarget(
                        target!.Role!,
                        target.Rid!,
                        target.Kind!,
                        target.EntryPoint!,
                        target.TargetFramework,
                        target.SdkVersion,
                        (target.RequiredHostCapabilities ?? []).Select(static capability => capability!).ToArray(),
                        (target.Views ?? []).Where(static view => view is not null).Select(static view => new RegistryPackageWebView(
                            view!.ViewId!,
                            view.DisplayName!,
                            view.Route!,
                            view.Icon,
                            view.DefaultPlacement!,
                            view.ShowInHotbar!.Value)).ToArray(),
                         target.ExecutionRuntime is null
                             ? null
                             : new RegistryExecutionRuntimeRequirement(
                                 target.ExecutionRuntime.Id!,
                                 target.ExecutionRuntime.VersionRange!,
                                 target.ExecutionRuntime.ModuleAbi!)))
                     .ToArray(),
                 artifacts,
                 RuntimeBindings: validation.Manifest.Targets!
                     .Where(target => target is
                     {
                         Role: SunderPackageFormat.RuntimeHostRole,
                         Kind: SunderPackageFormat.WorkerTargetKind,
                         ExecutionRuntime: not null,
                     } && string.Equals(target.Rid, rid, StringComparison.Ordinal))
                     .Select(target => new RegistryPackageTargetRuntimeBinding(
                         target!.Role!,
                         target.Rid!,
                         new RegistryExecutionRuntimeRequirement(
                             target.ExecutionRuntime!.Id!,
                             target.ExecutionRuntime.VersionRange!,
                             target.ExecutionRuntime.ModuleAbi!),
                         new RegistryExecutionRuntimeIdentity(
                             target.ExecutionRuntime.Id!,
                             "10.0.1",
                             1,
                             target.Rid!,
                             target.ExecutionRuntime.ModuleAbi!)))
                     .ToArray()),
             contentByUrl);
    }

    private static HttpResponseMessage PlanResponse(
        RegistryPackageInstallPlanItem item,
        string resolutionFingerprint = "test-resolution-fingerprint",
        DateTimeOffset? now = null,
        long runtimePolicyRevision = 1,
        RegistryExecutionRuntimeLifecycle? lifecycle = null,
        InstalledPackageExecutionRuntimeBindingRecord? exactBinding = null,
        IReadOnlyList<string>? warnings = null)
    {
        var observedAtUtc = now ?? DateTimeOffset.UtcNow;
        lifecycle ??= new RegistryExecutionRuntimeLifecycle(
            ExecutionRuntimeLifecycleStates.Supported,
            observedAtUtc.AddDays(-2),
            observedAtUtc.AddDays(-1),
            DeprecatedAtUtc: null,
            InstallBlockedAtUtc: null,
            ActivationBlockedAtUtc: null,
            RevokedAtUtc: null,
            Message: null);
        var runtimeArtifacts = item.RuntimeBindings.Select(binding => new RegistryResolvedExecutionRuntimeArtifact(
            binding.Runtime,
            exactBinding?.RuntimeManifestSha256 ?? new string('1', 64),
            exactBinding?.RuntimeArtifactSha256 ?? new string('2', 64),
            exactBinding?.RuntimeArtifactSize ?? 4096,
            exactBinding?.SigningKeyId ?? "sunder.test.release.v1",
            exactBinding?.TrustStoreGeneration ?? 1,
            new string('3', 64),
            "sunder.test.attestation.v1",
            lifecycle,
            runtimePolicyRevision,
            "https://registry.example/artifacts/test-runtime.sunderruntime")).ToArray();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                true,
                [item],
                warnings ?? [],
                [],
                [],
                [],
                runtimeArtifacts,
                [],
                resolutionFingerprint,
                runtimePolicyRevision,
                observedAtUtc.AddMinutes(5))),
        };
    }

    private static RegistryExecutionRuntimeLifecycle DeprecatedRuntimeLifecycle(DateTimeOffset observedAtUtc)
        => new(
            ExecutionRuntimeLifecycleStates.Deprecated,
            observedAtUtc.AddDays(-3),
            observedAtUtc.AddDays(-2),
            observedAtUtc.AddDays(-1),
            InstallBlockedAtUtc: null,
            ActivationBlockedAtUtc: null,
            RevokedAtUtc: null,
            Message: "Update to a supported execution runtime.");

    private static RegistryExecutionRuntimeLifecycle SupportedRuntimeLifecycle(DateTimeOffset observedAtUtc)
        => new(
            ExecutionRuntimeLifecycleStates.Supported,
            observedAtUtc.AddDays(-3),
            observedAtUtc.AddDays(-2),
            DeprecatedAtUtc: null,
            InstallBlockedAtUtc: null,
            ActivationBlockedAtUtc: null,
            RevokedAtUtc: null,
            Message: null);

    private static RegistryExecutionRuntimeLifecycle RevokedRuntimeLifecycle(DateTimeOffset observedAtUtc)
        => new(
            ExecutionRuntimeLifecycleStates.Revoked,
            observedAtUtc.AddDays(-3),
            observedAtUtc.AddDays(-2),
            observedAtUtc.AddDays(-1),
            observedAtUtc,
            observedAtUtc,
            observedAtUtc,
            "This execution runtime release was revoked.");

    private static RegistryExecutionRuntimeLifecycle RetiredRuntimeLifecycle(DateTimeOffset observedAtUtc)
        => new(
            ExecutionRuntimeLifecycleStates.Retired,
            observedAtUtc.AddDays(-4),
            observedAtUtc.AddDays(-3),
            observedAtUtc.AddDays(-2),
            observedAtUtc.AddDays(-1),
            observedAtUtc,
            RevokedAtUtc: null,
            Message: "This execution runtime release was retired.");

    private static ExecutionRuntimeReceiptV1 CreateRuntimeReceipt(
        InstalledPackageExecutionRuntimeBindingRecord binding,
        DateTimeOffset now)
    {
        const string launcherPath = "payload/bin/test-runtime";
        return new ExecutionRuntimeReceiptV1(
            ExecutionRuntimeReceiptV1.CurrentSchemaVersion,
            new ExecutionRuntimeIdentity(
                binding.RuntimeId,
                binding.RuntimeVersion,
                binding.RuntimePackagingRevision,
                binding.Rid,
                binding.ModuleAbi),
            SunderExecutionRuntimeFormat.WorkerProtocol,
            binding.RuntimeArtifactSha256,
            binding.RuntimeArtifactSize,
            binding.RuntimeManifestSha256,
            new string('d', 64),
            launcherPath,
            new string('e', 64),
            1,
            [SunderExecutionRuntimeFormat.DotNetExecArgument, SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder],
            [new ExecutionRuntimeReceiptContentV1(launcherPath, new string('e', 64), 1)],
            new string('3', 64),
            "sunder.test.attestation.v1",
            binding.SigningKeyId,
            binding.TrustStoreGeneration,
            ExecutionRuntimeTrustEnvironment.Production,
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            binding.RegistryOrigin,
            now.AddDays(-1));
    }

    private static async Task WriteRuntimeReceiptAsync(
        ExecutionRuntimeStore store,
        ExecutionRuntimeStoreLayout layout,
        ExecutionRuntimeReceiptV1 receipt)
    {
        await store.InitializeAsync(CancellationToken.None);
        var path = layout.GetReceiptPath(receipt.Identity);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await DurableJsonDocument.WriteAsync(
            path,
            receipt,
            ExecutionRuntimeStore.JsonOptions,
            CancellationToken.None);
    }

    private static HttpResponseMessage RuntimeConflictPlanResponse(
        RegistryExecutionRuntimeResolutionConflict conflict,
        DateTimeOffset now,
        IReadOnlyList<string>? warnings = null)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                false,
                [],
                warnings ?? [],
                [],
                [],
                [],
                RuntimeArtifacts: [],
                RuntimeConflicts: [conflict],
                ResolutionFingerprint: "blocked-runtime-resolution",
                RuntimePolicyRevision: 2,
                ExpiresAtUtc: now.AddMinutes(5))),
        };

    private static HttpResponseMessage RuntimeReleaseResponse(
        ExecutionRuntimeReceiptV1 receipt,
        RegistryExecutionRuntimeLifecycle lifecycle,
        long runtimePolicyRevision,
        DateTimeOffset now)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new RegistryExecutionRuntimeReleaseDetails(
                new RegistryExecutionRuntimeReleaseSummary(
                    receipt.Identity.RuntimeId,
                    receipt.Identity.Version,
                    receipt.Identity.PackagingRevision,
                    receipt.Identity.ModuleAbi,
                    receipt.WorkerProtocol,
                    receipt.TrustGeneration,
                    lifecycle,
                    [receipt.Identity.Rid],
                    now.AddDays(-1)),
                [new RegistryExecutionRuntimeArtifactDetails(
                    new RegistryResolvedExecutionRuntimeArtifact(
                        new RegistryExecutionRuntimeIdentity(
                            receipt.Identity.RuntimeId,
                            receipt.Identity.Version,
                            receipt.Identity.PackagingRevision,
                            receipt.Identity.Rid,
                            receipt.Identity.ModuleAbi),
                        receipt.ManifestSha256,
                        receipt.ArtifactSha256,
                        receipt.ArtifactSize,
                        receipt.SigningKeyId,
                        receipt.TrustGeneration,
                        receipt.NativeAttestationSha256,
                        receipt.NativeAttestationKeyId,
                        lifecycle,
                        runtimePolicyRevision,
                        "https://registry.example/artifacts/test-runtime.sunderruntime"),
                    receipt.ContentIdentity,
                    now.AddDays(-1))])),
        };

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]), StringComparer.OrdinalIgnoreCase);

    private static async Task ShutdownPackageLifecycleAsync(IServiceProvider provider)
    {
        await provider.GetRequiredService<InstalledPackageLifecycleService>().ShutdownAsync(
            provider.GetRequiredService<PackageSessionLifecycleService>());
        var projections = provider.GetRequiredService<PackageProjectionCache>();
        await using var reset = await projections.AcquireOfflineResetScopeAsync();
        await reset.ValidateAsync();
        await reset.PrepareForDeletionAsync();
        if (Directory.Exists(projections.RootPath))
        {
            Directory.Delete(projections.RootPath, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(condition(), "Condition was not reached before the test deadline.");
    }

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

    private static IReadOnlyList<string> CaptureFileTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return $"{Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}:{stream.Length}:{hash}";
            })
            .ToArray();
    }

    private static IReadOnlyList<string> CaptureDirectoryTree(string root)
        => !Directory.Exists(root)
            ? []
            : Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();

    private sealed class CandidateFailingExecutionRuntimeLaunchResolver(string rootPath)
        : IExecutionRuntimeLaunchResolver
    {
        private readonly List<ObservedRuntimeLaunch> _launches = [];

        public IReadOnlyList<ObservedRuntimeLaunch> Launches => _launches;

        public Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
            PreparedRuntimePackage package,
            Guid activationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = package.ExecutionRuntimeBinding
                          ?? throw new InvalidDataException("The process-backed test package is missing its exact runtime binding.");
            var entryPoint = package.EntryAssemblyPath
                             ?? throw new InvalidDataException("The process-backed test package is missing its entry point.");
            var identity = binding.Identity;
            var candidate = _launches.Count > 0 && identity != _launches[0].Identity;
            var runtimeRoot = Path.Combine(rootPath, candidate ? "candidate-runtime-root" : "active-runtime-root");
            Directory.CreateDirectory(runtimeRoot);
            var lease = new TestExecutionRuntimeProcessLease(runtimeRoot);
            var arguments = candidate
                ? new[]
                {
                    SunderExecutionRuntimeFormat.DotNetExecArgument,
                    Path.Combine(runtimeRoot, "missing-candidate-worker.dll"),
                }
                :
                [
                    SunderExecutionRuntimeFormat.DotNetExecArgument,
                    Path.GetFullPath(entryPoint),
                ];
            _launches.Add(new ObservedRuntimeLaunch(
                identity,
                runtimeRoot,
                package.ShadowFolder,
                lease));
            return Task.FromResult(new ResolvedExecutionRuntimeLaunch(
                runtimeRoot,
                TestExecutionRuntimeLaunchResolver.Instance.LauncherPath,
                arguments,
                lease,
                identity));
        }
    }

    private sealed record ObservedRuntimeLaunch(
        ExecutionRuntimeIdentity Identity,
        string RuntimeRootPath,
        string ProjectionPath,
        TestExecutionRuntimeProcessLease Lease);

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool StopRequested { get; private set; }

        public void StopApplication() => StopRequested = true;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        private readonly ActivePolicyRegistryFixture _policy = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => await _policy.SendAsync(request, () => Task.FromResult(send(request)), cancellationToken);
    }

    private sealed class AsyncDelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private readonly ActivePolicyRegistryFixture _policy = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _policy.SendAsync(request, () => send(request, cancellationToken), cancellationToken);
    }

    private sealed record RegistryArtifactFixture(
        RegistryPackageInstallPlanItem PlanItem,
        IReadOnlyDictionary<string, byte[]> ContentByUrl)
    {
        public HttpResponseMessage CreateResponse(Uri uri)
            => TryCreateResponse(uri, out var response)
                ? response
                : throw new InvalidOperationException($"No test Registry artifact exists at '{uri}'.");

        public bool TryCreateResponse(Uri uri, out HttpResponseMessage response)
        {
            if (!ContentByUrl.TryGetValue(uri.AbsoluteUri, out var content))
            {
                response = null!;
                return false;
            }
            response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri),
            };
            return true;
        }
    }

    private sealed class TrackingReadStream(byte[] content) : Stream
    {
        private int _offset;
        private int _disposed;
        public int BytesRead => Volatile.Read(ref _offset);
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, content.Length - _offset);
            content.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Volatile.Write(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }

    private sealed class CancellationBlockingResponseStream : Stream
    {
        private int _disposed;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) Volatile.Write(ref _disposed, 1);
            base.Dispose(disposing);
        }
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
