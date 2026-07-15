using Sunder.App.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageSessionServiceTests
{
    [Fact]
    public void DevelopmentSessions_WhenRuntimePathsCannotBeShared_ExposeUnavailableReason()
    {
        var service = new AppPackageSessionService();

        Assert.False(service.Availability.IsAvailable);
        Assert.Contains("cannot pass local paths", service.Availability.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadDevelopmentPackageAsync_WhenUnavailable_ReturnsStructuredUnsupportedOutcome()
    {
        var service = new AppPackageSessionService();

        var result = await service.LoadDevelopmentPackageAsync(new PackageDevelopmentSessionLoadRequest("/tmp/sunder-dev"));

        Assert.Equal(PackageDevelopmentSessionOperationOutcome.Unsupported, result.Outcome);
        Assert.Contains("cannot pass local paths", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
