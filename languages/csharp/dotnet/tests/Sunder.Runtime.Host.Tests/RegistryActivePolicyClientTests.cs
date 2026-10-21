using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;
using static Sunder.Runtime.Host.Tests.ActivePolicyTestContext;

namespace Sunder.Runtime.Host.Tests;

public sealed class RegistryActivePolicyClientTests
{
    private static RegistryPackageVersionIdentity Package => ActivePolicyTestContext.Package;
    [Fact]
    public async Task Request_IsAnonymousNoStoreFreshNonceAndCanonicalTupleOrder()
    {
        using var context = new ActivePolicyTestContext();
        var requests = new List<RegistryPolicySynchronizationRequest>();
        context.Send = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(Origin + RegistryApiRoutes.PolicySynchronization, message.RequestUri!.AbsoluteUri);
            Assert.Null(message.Headers.Authorization);
            Assert.True(message.Headers.CacheControl!.NoCache);
            Assert.True(message.Headers.CacheControl.NoStore);
            var request = (await message.Content!.ReadFromJsonAsync<RegistryPolicySynchronizationRequest>(RegistryActivePolicyClient.Json, token))!;
            requests.Add(request);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Allowed(request), options: RegistryActivePolicyClient.Json) };
        };
        RegistryPackageVersionIdentity[] subjects = [Package with { PackageId = "example.a.b" }, Package with { PackageId = "example.a" }];
        await context.RefreshAsync(subjects);
        await context.RefreshAsync(subjects);
        Assert.Equal(["example.a", "example.a.b"], requests[0].Packages.Select(value => value.PackageId));
        Assert.Matches("^[0-9a-f]{32}$", requests[0].Nonce);
        Assert.NotEqual(requests[0].Nonce, requests[1].Nonce);
        Assert.Null(requests[0].MinimumPackagePolicy);
        Assert.Equal(1, requests[1].MinimumPackagePolicy!.Revision);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("origin")]
    [InlineData("interval")]
    [InlineData("timezone")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("platform-omission")]
    [InlineData("package-contradiction")]
    [InlineData("platform-contradiction")]
    public async Task RejectsIncompleteOrContradictoryResponses(string mutation)
    {
        using var context = new ActivePolicyTestContext();
        context.Response = request =>
        {
            var response = Allowed(request);
            return mutation switch
            {
                "nonce" => response with { Nonce = new('f', 32) },
                "origin" => response with { RegistryOrigin = "https://other.example" },
                "interval" => response with { ValidUntilUtc = response.ValidUntilUtc.AddTicks(1) },
                "timezone" => response with { ObservedAtUtc = response.ObservedAtUtc.ToOffset(TimeSpan.FromHours(1)) },
                "missing" => response with { Packages = [] },
                "extra" => response with { Packages = [.. response.Packages, response.Packages[0] with { Package = Package with { Version = "2.0.0" } }] },
                "revision" => response with { PackagePolicy = response.PackagePolicy with { Revision = 0 } },
                "hash" => response with { PackagePolicy = response.PackagePolicy with { Fingerprint = "bad" } },
                "platform-omission" => response with { PlatformCatalogs = [] },
                "package-contradiction" => response with { Packages = [response.Packages[0] with { VersionAvailability = "SecurityBlocked" }] },
                "platform-contradiction" => response with { PlatformCatalogs = [response.PlatformCatalogs[0] with { LifecycleState = "Revoked" }] },
                _ => throw new InvalidOperationException(),
            };
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => context.FetchAsync(platforms: [Platform]));
        Assert.False(File.Exists(context.Cache.FilePath(Origin)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedPolicyIdentityRejectsOlderRevisionOrSameRevisionFingerprintConflict(bool conflictingFingerprint)
    {
        using var context = new ActivePolicyTestContext();
        context.Response = request => Allowed(request, 2);
        await context.RefreshAsync();
        var before = await File.ReadAllBytesAsync(context.Cache.FilePath(Origin));
        context.Response = request => conflictingFingerprint
            ? Allowed(request, 2) with { PackagePolicy = new(2, Hash("different-policy")) }
            : Allowed(request, 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => context.RefreshAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(context.Cache.FilePath(Origin)));
        context.Require();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    [InlineData("case")]
    [InlineData("removed-authority")]
    public async Task JsonBoundaryRejectsMalformedShape(string mutation)
    {
        using var context = new ActivePolicyTestContext();
        context.Send = async (message, token) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<RegistryPolicySynchronizationRequest>(RegistryActivePolicyClient.Json, token))!;
            var json = JsonSerializer.SerializeToNode(Allowed(request), RegistryActivePolicyClient.Json)!.AsObject();
            switch (mutation)
            {
                case "unknown": json["unexpected"] = true; break;
                case "missing": json.Remove("platformCatalogs"); break;
                case "null": json["packagePolicy"] = null; break;
                case "number": json["packagePolicy"] = "1"; break;
                case "case": json["Nonce"] = json["nonce"]!.DeepClone(); json.Remove("nonce"); break;
                case "removed-authority": json["checkpoint"] = null; break;
            }
            var bytes = json.ToJsonString();
            if (mutation == "duplicate") bytes = bytes.Insert(1, "\"nonce\":\"duplicate\",");
            return new(HttpStatusCode.OK) { Content = new StringContent(bytes, Encoding.UTF8, "application/json") };
        };
        await Assert.ThrowsAsync<JsonException>(() => context.FetchAsync());
    }

    [Theory]
    [InlineData(204)]
    [InlineData(304)]
    [InlineData(503)]
    public async Task NonSnapshotStatusNeverRenews(int status)
    {
        using var context = new ActivePolicyTestContext();
        await context.RefreshAsync();
        var before = await File.ReadAllBytesAsync(context.Cache.FilePath(Origin));
        context.Send = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        Assert.NotNull(await Record.ExceptionAsync(() => context.RefreshAsync()));
        Assert.Equal(before, await File.ReadAllBytesAsync(context.Cache.FilePath(Origin)));
    }

    [Fact]
    public async Task ChangedTransportOriginIsRejected()
    {
        using var context = new ActivePolicyTestContext();
        context.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = new(HttpMethod.Post, "https://other.example/policy"), Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        await Assert.ThrowsAsync<InvalidDataException>(() => context.FetchAsync());
    }

    [Fact]
    public async Task DeclaredOversizedBodyIsRejected()
    {
        using var context = new ActivePolicyTestContext();
        context.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new(' ', RegistryActivePolicyClient.MaximumBytes + 1), Encoding.UTF8, "application/json") });
        await Assert.ThrowsAsync<InvalidDataException>(() => context.FetchAsync());
    }

    [Fact]
    public async Task ChunkedOversizedBodyIsRejectedWithoutTrustingDeclaredLength()
    {
        using var context = new ActivePolicyTestContext();
        context.Send = (_, _) =>
        {
            var content = new StreamContent(new PolicyResponseStream(new byte[RegistryActivePolicyClient.MaximumBytes + 1]));
            content.Headers.ContentType = new("application/json");
            Assert.Null(content.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => context.FetchAsync());
    }

    [Fact]
    public async Task ResponseBodyReadRemainsInsideTheFifteenSecondDeadline()
    {
        using var context = new ActivePolicyTestContext();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Send = (_, _) =>
        {
            var content = new StreamContent(new PolicyResponseStream([], waiting));
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        var fetch = context.FetchAsync();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        context.Time.Advance(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<TimeoutException>(() => fetch);
    }

    private sealed class PolicyResponseStream(byte[] bytes, TaskCompletionSource? waiting = null) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (waiting is null) return await base.ReadAsync(buffer, cancellationToken);
            waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestDeadlineAndCallerCancellationRemainDistinct(bool callerCancellation)
    {
        using var context = new ActivePolicyTestContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Send = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation was ignored.");
        };
        using var cancellation = new CancellationTokenSource();
        var fetch = context.FetchAsync(token: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (callerCancellation) cancellation.Cancel();
        else context.Time.Advance(TimeSpan.FromSeconds(15));
        if (callerCancellation) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        else await Assert.ThrowsAsync<TimeoutException>(() => fetch);
    }

    [Fact]
    public async Task SuspendDuringRequestCannotEvadeDeadline()
    {
        using var context = new ActivePolicyTestContext();
        context.Response = request => { context.Clock.Advance(TimeSpan.FromSeconds(16)); return Allowed(request); };
        await Assert.ThrowsAsync<InvalidDataException>(() => context.FetchAsync());
    }

    [Fact]
    public void NativeClockHasStableBootIdentityAndNondecreasingSuspendAwareTicks()
    {
        var clock = new PolicyContinuousClock();
        var first = clock.Read();
        var second = clock.Read();
        RegistryActivePolicyClient.ValidateClock(first);
        Assert.True(first.SameEpoch(second));
        Assert.True(second.Ticks >= first.Ticks);
        Assert.NotEqual(Guid.Empty, Guid.Parse(first.BootSessionId));
    }
}
