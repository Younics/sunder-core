using Avalonia.Controls;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.App.Bridge;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Avalonia.Hosting;

/// <summary>SDK-owned composition used by the generated managed App entry point.</summary>
public static class AppPackageRuntime
{
    internal static Control AttachViewCallbacks(Control view)
    {
        if (AppViewCallbacksV1.Get(view) is null) AppViewCallbacksV1.Attach(view, new ViewCallbacks(view));
        return view;
    }

    /// <summary>Creates package-local adapters and a service provider over App V1 authority.</summary>
    public static async ValueTask<IAppPackageActivationV1> CreateAsync(ISunderAppPackageModule module, IAppPackageHostV1 host, IAppContributionSinkV1 contributions)
    {
        using var resolution = AssemblyLoadContext.EnterContextualReflection(module.GetType().Assembly);
        var channel = new AppChannel(host);
        var context = new AppContextAdapter(host, channel);
        var services = new ServiceCollection();
        module.ConfigureAppServices(services, context);
        // Package composition cannot replace activation-owned capabilities.
        var reserved = new[] { typeof(IPackageContext), typeof(IPackageCallbackClient), typeof(ILoggerFactory), typeof(ILogger<>),
            typeof(ISunderRpcClient), typeof(IPackageShellViewService), typeof(IPackageSettingsNavigationService),
            typeof(IPackageDevelopmentService), typeof(IPackageLogNavigationService), typeof(IPackageAppStateUpdates),
            typeof(IPackageWorkerExecutionObservations), typeof(IBackgroundTaskQueue), typeof(IPackageNotificationService) };
        if (services.FirstOrDefault(service => reserved.Contains(service.ServiceType)
                || service.ServiceType.IsConstructedGenericType && reserved.Contains(service.ServiceType.GetGenericTypeDefinition())) is { } replacement)
            throw new InvalidOperationException($"Package composition cannot replace App-owned capability '{replacement.ServiceType.FullName}'.");
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton(context.Callbacks);
        services.AddSingleton(context.Logging.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<ISunderRpcClient>(new AppRpcClient(channel));
        services.AddSingleton<IPackageShellViewService>(new AppShellAdapter(channel));
        services.AddSingleton<IPackageSettingsNavigationService>(new AppSettingsNavigationAdapter(channel));
        services.AddSingleton<IPackageDevelopmentService>(new AppDevelopmentAdapter(channel));
        services.AddSingleton<IPackageLogNavigationService>(new AppLogNavigationAdapter(channel));
        services.AddSingleton<IPackageAppStateUpdates>(new AppStateUpdatesAdapter(host));
        services.AddSingleton<IPackageWorkerExecutionObservations>(new AppWorkerObservationsAdapter(channel));
        services.AddSingleton<IBackgroundTaskQueue>(_ => new AppBackgroundTasksAdapter(host, channel));
        services.AddSingleton<IPackageNotificationService>(new AppNotificationsAdapter(channel));
        var provider = services.BuildServiceProvider();
        try
        {
            module.RegisterAppContributions(new Contributions(provider, contributions), provider);
            return new Activation(module, provider);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Activation(ISunderAppPackageModule module, ServiceProvider provider) : IAppPackageActivationV1
    {
        public void Start(CancellationToken token)
        {
            using var resolution = AssemblyLoadContext.EnterContextualReflection(module.GetType().Assembly);
            module.StartAppServices(provider, token);
        }
        public async ValueTask DisposeAsync()
        {
            using var resolution = AssemblyLoadContext.EnterContextualReflection(module.GetType().Assembly);
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Contributions(IServiceProvider services, IAppContributionSinkV1 sink) : IAvaloniaPackageContributionRegistry
    {
        public void RegisterPackageView<TView>(PackageViewRegistration registration) where TView : Control
            => sink.RegisterView(new(registration.Id, registration.Name, registration.IconAssetPath,
                (int)registration.DefaultPlacement, registration.ShowInHotbarByDefault), Create<TView>);
        public void RegisterSettingsView<TView>() where TView : Control => sink.RegisterSettingsView(Create<TView>);
        private Control Create<TView>() where TView : Control
        {
            using var resolution = AssemblyLoadContext.EnterContextualReflection(typeof(TView).Assembly);
            var view = services.GetService<TView>() ?? ActivatorUtilities.CreateInstance<TView>(services);
            return AttachViewCallbacks(view);
        }
    }

    private sealed class ViewCallbacks(Control view) : IAppViewCallbacksV1
    {
        private T? Target<T>() where T : class => view as T ?? view.DataContext as T;
        private static PackageViewNavigationContext Context(string id, IReadOnlyDictionary<string, string?> parameters) => new(id, parameters);
        public async ValueTask WarmupAsync(CancellationToken token)
        {
            using var resolution = AssemblyLoadContext.EnterContextualReflection(view.GetType().Assembly);
            if (Target<IPackageViewWarmupTarget>() is { } target) await target.WarmupAsync(token);
        }
        public async ValueTask NavigateAsync(string id, IReadOnlyDictionary<string, string?> parameters, CancellationToken token)
        {
            using var resolution = AssemblyLoadContext.EnterContextualReflection(view.GetType().Assembly);
            if (Target<IPackageViewNavigationTarget>() is { } target) await target.OnNavigatedToAsync(Context(id, parameters), token);
        }
        public bool SupportsPreparation => Target<IPackageViewNavigationPreparationTarget>() is not null;
        public async ValueTask<bool> PrepareAsync(string id, IReadOnlyDictionary<string, string?> parameters, CancellationToken token)
        {
            using var resolution = AssemblyLoadContext.EnterContextualReflection(view.GetType().Assembly);
            return Target<IPackageViewNavigationPreparationTarget>() is { } target && await target.PrepareNavigationAsync(Context(id, parameters), token);
        }
        public async ValueTask PresentedAsync(string id, IReadOnlyDictionary<string, string?> parameters, CancellationToken token)
        {
            using var resolution = AssemblyLoadContext.EnterContextualReflection(view.GetType().Assembly);
            if (Target<IPackageViewNavigationPreparationTarget>() is { } target) await target.OnNavigationPresentedAsync(Context(id, parameters), token);
        }
    }
}
