using System.Text.Json;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.Registry.Contracts.Tests;

public sealed class RegistryPublishTokenContractTests
{
    [Fact]
    public void ApiKeys_LegacyRequestsRetainFiniteExactDefaults_AndAllPackagesAndNeverAreExplicit()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string legacy = """{"displayName":"CI","grants":[{"permission":"package:publish","resourceId":"example.package"}]}""";
        var request = JsonSerializer.Deserialize<RegistryCreateApiKeyRequest>(legacy, options)!;
        Assert.Null(request.LifetimeDays);
        Assert.False(request.NeverExpires);
        Assert.False(Assert.Single(request.Grants).AllPackages);
        var all = new RegistryCreateApiKeyRequest("CI", [new(RegistryApiKeyPermissions.PackagePublish, AllPackages: true)], NeverExpires: true);
        var roundTrip = JsonSerializer.Deserialize<RegistryCreateApiKeyRequest>(JsonSerializer.Serialize(all, options), options)!;
        Assert.True(roundTrip.NeverExpires);
        Assert.Equal(new RegistryApiKeyGrant(RegistryApiKeyPermissions.PackagePublish, AllPackages: true), Assert.Single(roundTrip.Grants));
        var summary = new RegistryApiKeySummary(Guid.NewGuid(), "CI", all.Grants, DateTimeOffset.UtcNow, null);
        Assert.Null(JsonSerializer.Deserialize<RegistryApiKeySummary>(JsonSerializer.Serialize(summary, options), options)!.ExpiresAtUtc);
    }

    [Fact]
    public void PublishRequestHeaders_AreStableAndDistinct()
    {
        Assert.Equal("X-Sunder-Expected-Resource-Id", RegistryPublishRequestHeaders.ExpectedResourceId);
        Assert.Equal("X-Sunder-Set-Latest", RegistryPublishRequestHeaders.SetLatest);
        Assert.NotEqual(RegistryPublishRequestHeaders.ExpectedResourceId, RegistryPublishRequestHeaders.SetLatest);
    }

    [Fact]
    public void PublishTokenScopes_AreStableAndDistinct()
    {
        Assert.Equal("package:publish", RegistryPublishTokenScopes.PackagePublish);
        Assert.Equal("package:promote-latest", RegistryPublishTokenScopes.PackagePromoteLatest);
        Assert.Equal("stack:publish", RegistryPublishTokenScopes.StackPublish);
        Assert.Equal(3, new HashSet<string>(
        [
            RegistryPublishTokenScopes.PackagePublish,
            RegistryPublishTokenScopes.PackagePromoteLatest,
            RegistryPublishTokenScopes.StackPublish,
        ], StringComparer.Ordinal).Count);
    }

    [Fact]
    public void PublishTokenInventory_DoesNotExposeSecretOrHash()
    {
        var propertyNames = typeof(RegistryPublishTokenSummary)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
    }
}
