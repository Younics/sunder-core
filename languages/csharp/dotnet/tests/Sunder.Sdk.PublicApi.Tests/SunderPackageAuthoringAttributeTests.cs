using System.Reflection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderPackageAuthoringAttributeTests
{
    [Fact]
    public void SingleProjectIntent_HasExplicitRoleAndPrivateServiceDefaults()
    {
        Assert.Equal(SunderServiceExposure.Package, new SunderServiceAttribute().Exposure);
        Assert.Equal(new[] { SunderPackageCodeRole.Worker, SunderPackageCodeRole.App, SunderPackageCodeRole.Shared }, Enum.GetValues<SunderPackageCodeRole>());
        Assert.Equal(SunderPackageCodeRole.Shared, new SunderPackageCodeRoleAttribute(SunderPackageCodeRole.Shared).Role);
        var usage = typeof(SunderPackageCodeRoleAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;
        Assert.False(usage.AllowMultiple); Assert.False(usage.Inherited);
        Assert.Equal(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate, usage.ValidOn);
    }

    [Fact]
    public void ViewIntent_DeclaresMetadataWithoutConstructingViewModels()
    {
        var view = new SunderPackageViewAttribute("example.view", "View") { ViewModel = typeof(ThrowingViewModel), IconPath = "icons/view.png" };
        Assert.Equal("example.view", view.ViewId); Assert.Equal("View", view.DisplayName);
        Assert.Equal(PackageViewPlacement.Middle, view.DefaultPlacement);
        Assert.Equal(typeof(ThrowingViewModel), view.ViewModel);
        Assert.Equal(SunderSdkCapabilities.ViewsV1, Assert.Single(typeof(SunderPackageViewAttribute).GetCustomAttributes<SunderSdkCapabilityAttribute>()).Capability);
        Assert.Equal(SunderSdkCapabilities.SettingsViewsV1, Assert.Single(typeof(SunderPackageSettingsViewAttribute).GetCustomAttributes<SunderSdkCapabilityAttribute>()).Capability);
        Assert.Null(new SunderPackageSettingsViewAttribute().ViewModel);
    }

    private sealed class ThrowingViewModel
    {
        static ThrowingViewModel() => throw new InvalidOperationException("Metadata access must not initialize the view model.");
    }
}
