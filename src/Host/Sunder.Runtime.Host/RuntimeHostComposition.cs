using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton(bearerTokenValidator);
        services.AddSingleton(packagePaths);
        services.AddSingleton<RuntimeOperationGate>();
        services.AddSingleton<RuntimeResetChallengeService>();
        services.AddSingleton<InstalledPackageStore>();
        services.AddSingleton<SunderPackageArchiveInstaller>();
        services.AddSingleton<PackageStoreCoordinator>();
        services.AddSingleton<PackageUiSnapshotStore>();
        services.AddSingleton<RuntimeContentTransferStore>();
        services.AddSingleton<RuntimeContentTransferService>();
        services.AddSingleton<RuntimeEventStreamService>();
        services.AddSingleton(provider => new PackageLogStreamService(
            provider.GetRequiredService<RuntimePackagePaths>().PackageDataRootPath));
        services.AddSingleton<RuntimeSessionOwner>();
        services.AddSingleton(provider => provider.GetRequiredService<RuntimeSessionOwner>().State);
        services.AddSingleton(provider => new PackageSessionReconciler(
            provider.GetRequiredService<ILogger<PackageSessionReconciler>>(),
            provider.GetRequiredService<InstalledPackageStore>(),
            provider.GetRequiredService<RuntimePackagePaths>()));
        services.AddSingleton<RuntimePackageUiService>();
        services.AddSingleton<PackageSessionLifecycleService>();
        services.AddSingleton<InstalledPackageLifecycleService>();
        services.AddSingleton<PackageSessionCommandService>();
        services.AddSingleton<RuntimePackageDataService>();
        services.AddSingleton<PackageConfigurationAccessService>();
        services.AddSingleton<PackageAuthAccessService>();
        services.AddSingleton<PackageFaultService>();
        services.AddSingleton<RuntimeStackExportService>();
        services.AddSingleton<RuntimeStackImportService>();
        services.AddSingleton<DevPackageWatchService>();
        services.AddSingleton<PackageAuthCallbackServer>();
        services.AddHttpClient("registry", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sunder-Runtime/1.0");
        });
        services.AddSingleton<RegistryCredentialStore>();
        services.AddSingleton<RegistryAuthCoordinator>();
        services.AddSingleton<RegistryPackageChangeOrchestrator>();
        services.AddSingleton<RegistryAuthenticatedOperations>();
        return services;
    }
}
