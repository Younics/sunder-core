using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.Registry.Contracts.Tests;

public sealed class RegistryPublishTokenContractTests
{
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
