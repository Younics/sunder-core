using Avalonia.Controls;

namespace Sunder.Sdk.Abstractions;

public interface IPackageWorkspaceFactory
{
    Control CreateRootView(IServiceProvider services);
}
