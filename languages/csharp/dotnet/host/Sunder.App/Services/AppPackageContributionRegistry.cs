using Avalonia.Controls;
using Sunder.App.Bridge;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppPackageContributionRegistry(
    AppPackageViewRegistry viewRegistry,
    string packageId) : IAppContributionSinkV1
{
    public void RegisterView(AppViewRegistrationV1 registration, Func<Control> factory)
    {
        viewRegistry.RegisterPackageView(packageId, new PackageViewRegistration(registration.Id, registration.Name,
            registration.IconAssetPath, (PackageViewPlacement)registration.DefaultPlacement, registration.ShowInHotbarByDefault), factory);
    }

    public void RegisterSettingsView(Func<Control> factory)
    {
        viewRegistry.RegisterSettingsView(packageId, factory);
    }

}
