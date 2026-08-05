using System.Reflection;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Hosting;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;

namespace Sunder.App.Services;

internal sealed class AppSharedAssemblyRegistry : IDisposable
{
    private readonly Dictionary<string, Assembly> _hostSharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        [typeof(Control).Assembly.GetName().Name!] = typeof(Control).Assembly,
        [typeof(Avalonia.AvaloniaObject).Assembly.GetName().Name!] = typeof(Avalonia.AvaloniaObject).Assembly,
        [typeof(Avalonia.Markup.Xaml.AvaloniaXamlLoader).Assembly.GetName().Name!] = typeof(Avalonia.Markup.Xaml.AvaloniaXamlLoader).Assembly,
        [typeof(IServiceCollection).Assembly.GetName().Name!] = typeof(IServiceCollection).Assembly,
        [typeof(ILoggerFactory).Assembly.GetName().Name!] = typeof(ILoggerFactory).Assembly,
        [typeof(ISunderRuntimePackageModule).Assembly.GetName().Name!] = typeof(ISunderRuntimePackageModule).Assembly,
        [typeof(IAvaloniaPackageContributionRegistry).Assembly.GetName().Name!] = typeof(IAvaloniaPackageContributionRegistry).Assembly,
        [typeof(IPackageStackExporter).Assembly.GetName().Name!] = typeof(IPackageStackExporter).Assembly,
    };

    private readonly HostSharedAssemblyRegistryCore _core;

    public AppSharedAssemblyRegistry(IEnumerable<string> probeDirectories)
    {
        _core = new HostSharedAssemblyRegistryCore(_hostSharedAssemblies);

        _core.RegisterOptionalHostAssemblyName("Avalonia");
        _core.RegisterOptionalHostAssemblyName("AvaloniaEdit");
        _core.RegisterOptionalHostAssemblyName("Avalonia.Markup");
        _core.RegisterOptionalHostAssemblyName("Avalonia.Dialogs");
        _core.RegisterOptionalHostAssemblyName("Avalonia.Remote.Protocol");
        _core.RegisterOptionalHostAssemblyName("Avalonia.Metal");
        _core.RegisterOptionalHostAssemblyName("Avalonia.OpenGL");
        _core.RegisterOptionalHostAssemblyName("Avalonia.Vulkan");
        _core.RegisterOptionalHostAssemblyName("Avalonia.MicroCom");
        _core.RegisterOptionalHostAssemblyName("MicroCom.Runtime");

    }

    public void AddProbeDirectories(IEnumerable<string> probeDirectories)
    {
    }

    public bool TryRemoveProbeDirectories(IEnumerable<string> probeDirectories)
        => true;

    public Assembly? ResolveSharedAssembly(AssemblyName assemblyName)
        => _core.ResolveSharedAssembly(assemblyName);

    internal static bool IsSharedAssemblyReferenceSatisfiedBy(
        AssemblyName requestedAssemblyName,
        AssemblyName loadedAssemblyName)
        => HostSharedAssemblyRegistryCore.IsReferenceSatisfiedBy(requestedAssemblyName, loadedAssemblyName);

    public void Dispose() => _core.Dispose();

}
