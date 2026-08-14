using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageCatalogPresentationTests
{
    [Fact]
    public void RpcAccessProjection_GroupsByCallerRoleAndPresentsExactVersions()
    {
        var projected = PackageRpcAccessProjection.FromRuntime(
        [
            new PackageRpcContractUseDescriptor(
                SunderPackageFormat.RuntimeHostRole,
                "example.rpc",
                "2.0.0+runtime",
                new string('b', 64),
                Required: false,
                Actions: ["subscribe"]),
            new PackageRpcContractUseDescriptor(
                SunderPackageFormat.AppHostRole,
                "example.rpc",
                "1.0.0+app",
                new string('a', 64),
                Required: true,
                Actions: ["invoke", "discover"]),
        ]);

        Assert.Collection(
            projected,
            access =>
            {
                Assert.Equal(SunderPackageFormat.AppHostRole, access.Role);
                Assert.Equal("1.0.0+app", access.ContractVersion);
                Assert.Equal("App caller", access.CallerRoleText);
                Assert.Equal("Version 1.0.0+app", access.ContractVersionText);
                Assert.Equal("Required", access.RequirementText);
                Assert.Equal("discover | invoke", access.ActionsText);
            },
            access =>
            {
                Assert.Equal(SunderPackageFormat.RuntimeHostRole, access.Role);
                Assert.Equal("2.0.0+runtime", access.ContractVersion);
                Assert.Equal("Runtime caller", access.CallerRoleText);
                Assert.Equal("Version 2.0.0+runtime", access.ContractVersionText);
                Assert.Equal("Optional", access.RequirementText);
                Assert.Equal("subscribe", access.ActionsText);
            });
    }

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
    public void InstalledPackageProjector_OffersRegistrySourceAdoptionForUnknownProvenance()
    {
        var installed = new InstalledPackageDescriptor(
            "agent",
            "Agent",
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            Summary: null,
            Icon: null,
            IsEnabled: true,
            DependsOn: [],
            DateTimeOffset.UtcNow,
            StatusMessage: null);
        var session = new SessionPackageDescriptor(
            "agent",
            "Agent",
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            Icon: null,
            IsEnabled: true,
            PackageReadinessState.Ready,
            Views: [],
            FailureOrigin: null,
            LastError: null,
            LastFailureAtUtc: null,
            FailureCount: 0);

        var state = PackageCatalogItemViewModel.CreateState(session, installed, update: null, iconUri: null);

        Assert.Equal("Ready", state.StatusText);
        Assert.True(state.CanDisable);
        Assert.True(state.CanUninstall);
        Assert.True(state.CanAdoptRegistrySource);
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
                PackageHostRoles.App | PackageHostRoles.Runtime,
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
