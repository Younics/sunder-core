using System.Reflection;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(Id = "compatibility.app.probe", Name = "Compatibility App activation probe")]

namespace Compatibility.App;

public sealed class ProbeModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => services.AddSingleton(new ProbeModel(context.PackageId));
    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterPackageView<ProbeView>("compatibility.app.probe");
}

public sealed record ProbeModel(string PackageId);

public sealed class ProbeView : ContentControl, IDisposable
{
    public ProbeView(ProbeModel model) { DataContext = model; Content = "retained-app-probe:" + model.PackageId; }
    public Assembly SdkAssembly => typeof(IPackageContext).Assembly;
    public Assembly AvaloniaSdkAssembly => typeof(IAvaloniaPackageContributionRegistry).Assembly;
    public bool IsDisposed { get; private set; }
    public string Echo(string nonce) => "app:" + nonce;
    public void Dispose() => IsDisposed = true;
}
