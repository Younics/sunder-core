using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Hosting;

namespace Sunder.App.Services;

internal sealed class AppPackageActivationState
{
    public AppLoadedPackageInfo? PackageInfo { get; set; }

    public ServiceProvider? ServiceProvider { get; set; }

    public AppPackageLoadContext? LoadContext { get; set; }

    public PackageExtensionOwnerActivation? ExtensionOwner { get; set; }
}
