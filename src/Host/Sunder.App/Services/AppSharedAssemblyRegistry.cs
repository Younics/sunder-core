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

    private readonly SharedContractAssemblyRegistryCore _core;
    private readonly Dictionary<string, string> _sharedAssemblyPaths;
    private readonly Dictionary<string, AssemblyName> _sharedAssemblyNames;

    public AppSharedAssemblyRegistry(IEnumerable<string> probeDirectories)
    {
        _core = new SharedContractAssemblyRegistryCore("App", _hostSharedAssemblies);
        _sharedAssemblyPaths = _core.SharedAssemblyPaths;
        _sharedAssemblyNames = _core.SharedAssemblyNames;

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

        AddProbeDirectories(probeDirectories);
    }

    public void AddProbeDirectories(IEnumerable<string> probeDirectories)
        => _core.AddProbeDirectories(probeDirectories);

    public void RemoveProbeDirectories(IEnumerable<string> probeDirectories)
        => _core.RemoveProbeDirectories(probeDirectories);

    public Assembly? ResolveSharedAssembly(AssemblyName assemblyName)
        => _core.ResolveSharedAssembly(assemblyName);

    internal static bool IsSharedAssemblyReferenceSatisfiedBy(
        AssemblyName requestedAssemblyName,
        AssemblyName loadedAssemblyName)
        => SharedContractAssemblyPolicy.IsReferenceSatisfiedBy(requestedAssemblyName, loadedAssemblyName);

    public void Dispose() => _core.Dispose();

    private void TryRegisterSharedAssemblyPath(AssemblyCandidate candidate, AssemblyName? requestedAssemblyName = null)
        => _core.RegisterCandidate(new SharedAssemblyCandidate(candidate.Path, candidate.Name), requestedAssemblyName);

    private readonly record struct AssemblyCandidate(string Path, AssemblyName Name);
}
