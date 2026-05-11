using Avalonia.Controls;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.WorkspacesV1)]
public interface IPackageWorkspaceFactory
{
    Control CreateRootView(IServiceProvider services);
}
