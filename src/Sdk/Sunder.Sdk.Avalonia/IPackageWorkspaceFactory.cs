using Avalonia.Controls;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Avalonia;

/// <summary>Creates an App-owned Avalonia root view for one package workspace activation.</summary>
/// <remarks>The App calls this on the UI thread and owns/disposes the returned control with the workspace. The service provider is activation-scoped and must not be retained.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.WorkspacesV1)]
public interface IPackageWorkspaceFactory
{
    /// <summary>Creates the root control using the current App package service provider.</summary>
    Control CreateRootView(IServiceProvider services);
}
