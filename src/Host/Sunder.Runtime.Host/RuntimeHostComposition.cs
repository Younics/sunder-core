using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host;

internal static class RuntimeHostComposition
{
    internal static IServiceCollection AddRuntimeHostServices(
        this IServiceCollection services,
        RuntimePackagePaths packagePaths,
        RuntimeBearerTokenValidator bearerTokenValidator)
    {
        services.AddLogging();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddSingleton(bearerTokenValidator);
        services.AddSingleton(packagePaths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RuntimeTransportPolicyOptions>();
        services.AddSingleton<RuntimeAuthPolicyOptions>();
        services.AddSingleton<RuntimeStackPolicyOptions>();
        services.AddSingleton<RuntimePackageOperationPolicyOptions>();
        services.AddSingleton<RuntimeLifecyclePolicyOptions>();
        services.AddSingleton<RuntimeProtocolDescriptor>();
        services.AddSingleton<RuntimeOperationGate>();
        services.AddSingleton<RuntimeResetChallengeService>();
        services.AddSingleton<InstalledPackageStore>();
        services.AddSingleton<SunderPackageArchiveInstaller>();
        services.AddSingleton<PackageStoreCoordinator>();
        services.AddSingleton<PackageUiSnapshotStore>();
        services.AddSingleton<RuntimeContentTransferStore>();
        services.AddHostedService<RuntimeContentTransferCleanupService>();
        services.AddSingleton<RuntimeContentTransferService>();
        services.AddSingleton<RuntimeEventStreamService>();
        services.AddSingleton(provider => new PackageLogStreamService(
            provider.GetRequiredService<RuntimePackagePaths>().PackageDataRootPath,
            provider.GetRequiredService<RuntimeProtocolDescriptor>().RuntimeInstanceId));
        services.AddSingleton(provider => new RuntimeSessionOwner(
            provider.GetRequiredService<ILogger<RuntimeSessionOwner>>(),
            provider.GetRequiredService<RuntimeEventStreamService>(),
            provider.GetRequiredService<RuntimeAuthPolicyOptions>(),
            provider.GetRequiredService<RuntimePackageOperationPolicyOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IHostApplicationLifetime>(),
            provider.GetRequiredService<PackageUiSnapshotStore>(),
            provider.GetRequiredService<RuntimeLifecyclePolicyOptions>()));
        services.AddSingleton(provider => provider.GetRequiredService<RuntimeSessionOwner>().State);
        services.AddSingleton<RuntimeSnapshotService>();
        services.AddSingleton(provider => new PackageSessionLoadService(
            provider.GetRequiredService<ILogger<PackageSessionLoadService>>(),
            provider.GetRequiredService<RuntimePackagePaths>()));
        services.AddSingleton<PackageSessionReconciler>();
        services.AddSingleton<PackageSessionPublisher>();
        services.AddSingleton<PackageLifecycleStageStore>();
        services.AddSingleton<RuntimePackageUiService>();
        services.AddSingleton<PackageSessionLifecycleService>();
        services.AddSingleton<InstalledPackageLifecycleService>();
        services.AddHostedService<PackageStageCleanupService>();
        services.AddSingleton<PackageSessionCommandService>();
        services.AddSingleton<RuntimePackageDataService>();
        services.AddSingleton(provider => new RuntimePackageOperationService(
            provider.GetRequiredService<PackageSessionState>(),
            provider.GetRequiredService<RuntimePackageOperationPolicyOptions>(),
            provider.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None));
        services.AddSingleton<PackageSettingsAccessService>();
        services.AddSingleton<PackageCallbackAccessService>();
        services.AddSingleton<PackageAuthAccessService>();
        services.AddHostedService<PackageCallbackSessionCleanupService>();
        services.AddSingleton<RuntimeStackExportService>();
        services.AddSingleton<RuntimeStackImportService>();
        services.AddHostedService<RuntimeStackImportPlanCleanupService>();
        services.AddSingleton<DevPackageWatchService>();
        services.AddSingleton<DevPackageOwnerLeaseService>();
        services.AddHostedService<DevPackageOwnerLeaseReaper>();
        services.AddSingleton<PackageCallbackServer>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<PackageCallbackServer>());
        services.AddHttpClient("registry", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sunder-Runtime/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<RegistryHttpClient>();
        services.AddSingleton<RegistryCredentialStore>();
        services.AddSingleton<RegistryAuthCoordinator>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<RegistryAuthCoordinator>());
        services.AddSingleton<RegistryPackageArtifactDownloader>();
        services.AddSingleton<RegistryPackagePlanResolver>();
        services.AddSingleton<RegistryPackageChangeOrchestrator>();
        services.AddSingleton<RegistryAuthenticatedOperations>();
        return services;
    }
}
