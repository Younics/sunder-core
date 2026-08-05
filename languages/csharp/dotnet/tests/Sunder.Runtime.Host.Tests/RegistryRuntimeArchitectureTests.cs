using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
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
        var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":true}"),
        });
        var client = new RegistryHttpClient(
            new FakeHttpClientFactory(handler),
            new RuntimeTransportPolicyOptions { MaxRegistryJsonBytes = 4 },
            TimeProvider.System);
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://registry.example/test"),
            HttpCompletionOption.ResponseContentRead,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadJsonAsync<object>(response, CancellationToken.None));
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
            HttpCompletionOption.ResponseContentRead,
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
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
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
                var package = Assert.Single(body.Packages);
                requests.Add((
                    request.RequestUri!.GetLeftPart(UriPartial.Authority) + "/",
                    package.PackageId,
                    package.Tag,
                    body.IncludePrerelease));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(true, [], [], [], [], [])),
                };
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().UpdateAsync(
                new RuntimeRegistryUpdateRequest(),
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
                            [],
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
                            true,
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
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var result = await provider.GetRequiredService<RegistryPackageChangeOrchestrator>().UpdateAsync(
                new RuntimeRegistryUpdateRequest(),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeRegistryErrorCode.Conflict, result.ErrorCode);
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
    public async Task RegistryUpdate_IncludePrereleaseOverrideIsResolverOnlyAndPreservesStoredPolicy()
    {
        var root = CreateTempDirectory();
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
                return PlanResponse(artifact.PlanItem);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
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
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryInstall_PersistsExplicitPrereleasePolicy()
    {
        var root = CreateTempDirectory();
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
                version: "1.0.0-beta.1");
            var handler = new DelegateHandler(request => request.Method == HttpMethod.Get
                ? artifact.CreateResponse(request.RequestUri!)
                : PlanResponse(artifact.PlanItem));
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
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
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryExecution_BlockedResolutionRejectsConcurrentSourceAdoptionWithoutMutation()
    {
        var root = CreateTempDirectory();
        try
        {
            const string firstOrigin = "https://first.example/";
            const string adoptedOrigin = "https://adopted.example/";
            const string packageId = "test.concurrent.adoption";
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
                    return PlanResponse(firstArtifact.PlanItem);
                }
                return PlanResponse(adoptedArtifact.PlanItem);
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
            services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
            var orchestrator = provider.GetRequiredService<RegistryPackageChangeOrchestrator>();
            var staleTask = orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    packageId,
                    firstOrigin,
                    Tag: "latest",
                    Confirm: true),
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
                        Confirm: true),
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
            => new(packageId, currentVersion, version, currentVersion is not null, null, [], [], []);
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
            RegistryResolvePackageChangesRequest? captured = null;
            var handler = new DelegateHandler(request =>
            {
                captured = request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                        true,
                        [new RegistryPackageInstallPlanItem(
                            "test.local",
                            "1.0.0",
                            "1.0.0",
                            true,
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
            Assert.Null(captured);

            var preview = await orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    "test.local",
                    "https://registry.example/",
                    Tag: "latest",
                    DryRun: true),
                CancellationToken.None);
            Assert.True(preview.Success, preview.Message);
            Assert.False(preview.RuntimeSessionApplied);
            Assert.Equal(InstalledPackageSourceKind.Unknown, (await store.GetAsync("test.local"))?.Provenance?.SourceKind);

            var result = await orchestrator.AdoptSourceAsync(
                new RuntimeRegistrySourceAdoptionRequest(
                    "test.local",
                    "https://registry.example/",
                    Tag: "latest",
                    Confirm: true),
                CancellationToken.None);

            Assert.True(captured?.Reinstall == true);
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
                            false,
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
                        [])),
                };
            });
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(
                new RuntimePackagePaths(root),
                new RuntimeBearerTokenValidator("test-runtime-token"));
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
        var installPath = paths.GetInstalledPackagePath(packageId, version);
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            installPath,
            packageId,
            version,
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        return CanonicalPackageTestBuilder.CreateInstalledRecord(installPath, packageId, version) with
        {
            Provenance = provenance,
        };
    }

    private static async Task<RegistryArtifactFixture> CreateRegistryArtifactAsync(
        string root,
        string name,
        string origin,
        string packageId,
        string? currentVersion,
        string version)
    {
        var artifactRoot = Path.Combine(root, "registry-artifacts", name);
        var sourceRoot = Path.Combine(artifactRoot, "source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            sourceRoot,
            packageId,
            version,
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(sourceRoot);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var sourceArchiveSha256 = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{packageId}:{version}:{name}")))
            .ToLowerInvariant();
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
                currentVersion is not null,
                null,
                [],
                [],
                artifacts),
            contentByUrl);
    }

    private static HttpResponseMessage PlanResponse(RegistryPackageInstallPlanItem item)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                true,
                [item],
                [],
                [],
                [],
                [])),
        };

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]), StringComparer.OrdinalIgnoreCase);

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

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class AsyncDelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
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
