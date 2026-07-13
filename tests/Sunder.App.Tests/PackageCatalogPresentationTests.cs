using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageCatalogPresentationTests
{
    [Fact]
    public void Projection_FiltersAcrossSharedFieldsAndSortsDeterministically()
    {
        var source = new[]
        {
            new TestPackage("z.tools", "Tools", "Utilities for local agents", "1.0.0", "Installed package"),
            new TestPackage("a.agent", "Agent", "Local assistant", "2.0.0", "Dev package"),
        };

        var projected = PackageCatalogProjection.Project(
            source,
            package => new PackageCatalogSearchDocument(
                package.Id,
                package.Name,
                package.Version,
                package.Summary,
                package.Source),
            package => package.Id,
            "  package  ",
            PackageCatalogSort.DisplayName);

        Assert.Equal(["a.agent", "z.tools"], projected);
    }

    [Fact]
    public void Projection_SourceOrderPreservesRegistryRankingAndSourceSpecificFactory()
    {
        var source = new[]
        {
            new TestPackage("popular", "Popular", null, "1.0.0", "Marketplace package"),
            new TestPackage("recent", "Recent", null, "2.0.0", "Marketplace package"),
        };

        var projected = PackageCatalogProjection.Project(
            source,
            package => new PackageCatalogSearchDocument(package.Id, package.Name),
            package => $"install:{package.Id}");

        Assert.Equal(["install:popular", "install:recent"], projected);
    }

    [Fact]
    public void OperationState_RepresentsMutuallyExclusiveLoadingOutcomes()
    {
        Assert.True(PresentationOperationState.Running.IsRunning);
        Assert.False(PresentationOperationState.Running.IsSucceeded);
        Assert.False(PresentationOperationState.Succeeded.IsRunning);
        Assert.True(PresentationOperationState.Succeeded.IsSucceeded);

        var failed = PresentationOperationState.Failed("unavailable");
        Assert.True(failed.HasError);
        Assert.Equal("unavailable", failed.ErrorMessage);
    }

    [Fact]
    public async Task SourceProjectors_PreserveSourceSpecificSelectionActions()
    {
        PackageCatalogItemViewModel? selectedInstalled = null;
        var installedState = Assert.Single(InstalledPackageCatalogProjector.Build(
            [],
            [new InstalledPackageDescriptor(
                "agent",
                "Agent",
                "1.0.0",
                Summary: null,
                Icon: null,
                IsEnabled: true,
                DependsOn: [],
                DateTimeOffset.UtcNow,
                StatusMessage: null)],
            [],
            string.Empty,
            (_, _) => null));
        using var installed = new PackageCatalogItemViewModel(installedState, item => selectedInstalled = item);

        RegistryPackageSearchItemViewModel? selectedMarketplace = null;
        var marketplace = Assert.Single(MarketplacePackageSearchProjector.Build(
            [new RegistryPackageSummary(
                "agent",
                "Agent",
                "Local assistant",
                "2.0.0",
                IconUrl: null,
                IsYanked: false,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)],
            [],
            [],
            item =>
            {
                selectedMarketplace = item;
                return Task.CompletedTask;
            }));
        using (marketplace)
        {
            installed.SelectCommand.Execute(null);
            await marketplace.SelectCommand.ExecuteAsync(null);

            Assert.Same(installed, selectedInstalled);
            Assert.Same(marketplace, selectedMarketplace);
        }
    }

    [Fact]
    public void RegistryAccountPresentation_ProjectsSignedInAndSignedOutStates()
    {
        var signedIn = RegistryAccountPresentationState.FromAuthState(RegistryAuthState.SignedIn(
            new Uri("https://registry.example/"),
            new RegistryCurrentUserResponse(
                Guid.NewGuid().ToString("N"),
                "user_generated",
                "agent@example.test",
                "agent-user",
                "file:///avatar.png",
                RequiresUsername: false),
            DateTimeOffset.UtcNow.AddHours(1)));

        Assert.True(signedIn.IsSignedIn);
        Assert.Equal("@agent-user", signedIn.DisplayName);
        Assert.Equal("AU", signedIn.AvatarText);
        Assert.Null(signedIn.AvatarUrl);

        var signedOut = RegistryAccountPresentationState.SignedOut("Session expired.");
        Assert.False(signedOut.IsSignedIn);
        Assert.Equal("Session expired.", signedOut.StatusText);
    }

    private sealed record TestPackage(string Id, string Name, string? Summary, string Version, string Source);
}
