using Sunder.App.ViewModels;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageInformationPresentationTests
{
    [Fact]
    public void CapabilitiesArePlatformScopedGroupedAndOrderedWithoutInventingDescriptionsOrGrants()
    {
        var facts = new SunderPackageInformation([
            new("app", "osx-arm64", "storage.v1"), new("runtime", "osx-arm64", "storage.v1"),
            new("runtime", "osx-arm64", "secrets.v1"), new("runtime", "win-x64", "docker-environment.v1"),
            new("runtime", "osx-arm64", "rpc.v1"), new("runtime", "osx-arm64", "future-feature.v1")], [], [], []);
        var view = PackageInformationViewModel.Create(facts, "osx-arm64");
        Assert.Equal(3, view.Features.Count);
        Assert.Equal("Package secrets", view.Features[0].Title);
        Assert.True(view.Features[0].IsSensitive);
        Assert.DoesNotContain(view.Features, value => value.Identifiers.Contains("docker", StringComparison.Ordinal) || value.Identifiers == "rpc.v1");
        var storage = Assert.Single(view.Features, value => value.Title == "Package storage");
        Assert.Contains("Desktop UI", storage.Scope, StringComparison.Ordinal);
        Assert.Contains("Background worker", storage.Scope, StringComparison.Ordinal);
        Assert.Equal("future-feature.v1", Assert.Single(view.Features, value => value.Identifiers == "future-feature.v1").Title);
        Assert.Contains(view.TechnicalFeatures, value => value.Identifiers == "rpc.v1");
    }

    [Fact]
    public void RequiredRelationshipsSortBeforeOptionalButUnavailableOptionalDoesNotBecomeAWarning()
    {
        var facts = new SunderPackageInformation([new("runtime", "osx-arm64", "core.v1")],
            [new("a.optional", ">=1.0.0", "optional", []), new("z.required", ">=2.0.0", "required", [new("runtime", "example.tools", "1.0.0", "implements")])], [], []);
        string? navigated = null;
        var view = PackageInformationViewModel.Create(facts, "osx-arm64", (id, _) => new(id, "Not installed"), id => navigated = id);
        Assert.Equal(["z.required", "a.optional"], view.Dependencies.Select(value => value.PackageId));
        Assert.Equal("Implements extension", Assert.Single(view.Dependencies[0].Bindings).Kind);
        Assert.False(view.Dependencies[1].IsRequired);
        Assert.Contains("not a missing prerequisite", view.Dependencies[1].Explanation, StringComparison.Ordinal);
        view.Dependencies[1].NavigateCommand!.Execute(null);
        Assert.Equal("a.optional", navigated);
        Assert.Equal("1 required · 1 optional", view.DependencySummary);
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(12, false)]
    [InlineData(0, true)]
    [InlineData(0, false)]
    public void UnknownMetadataIsNotEmptyAndOnlySensitiveFeaturesStayOutsideMore(int ordinaryFeatureCount, bool hasSensitiveAccess)
    {
        var unavailable = PackageInformationViewModel.Unavailable();
        Assert.False(unavailable.HasNoDependencies);
        Assert.False(unavailable.HasNoPublicBindings);
        var features = Enumerable.Range(0, ordinaryFeatureCount).Select(index => new SunderPackageFeature("runtime", "osx-arm64", $"extra-{index}.v1"))
            .Concat(hasSensitiveAccess ? [new("runtime", "osx-arm64", "secrets.v1")] : []).ToArray();
        var view = PackageInformationViewModel.Create(new(features, [], [], []), "osx-arm64");
        Assert.Equal(hasSensitiveAccess ? 1 : 0, view.PrimaryFeatures.Count);
        Assert.All(view.PrimaryFeatures, value => Assert.True(value.IsSensitive));
        Assert.Equal(ordinaryFeatureCount, view.AdditionalFeatures.Count);
        Assert.All(view.AdditionalFeatures, value => Assert.False(value.IsSensitive));
        Assert.Equal(view.Features, view.PrimaryFeatures.Concat(view.AdditionalFeatures));
        Assert.Equal(hasSensitiveAccess, view.HasSensitiveFeatures);
        Assert.Equal(ordinaryFeatureCount > 0, view.HasAdditionalFeatures);
        Assert.True(view.HasNoDependencies);
        Assert.True(view.HasNoPublicBindings);
    }
}
