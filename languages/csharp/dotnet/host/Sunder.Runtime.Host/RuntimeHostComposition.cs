using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host;

internal static class RuntimeHostComposition
{
    internal static IServiceCollection AddRuntimeHostServices(
        this IServiceCollection services,
        RuntimePackagePaths packagePaths,
        RuntimeBearerTokenValidator bearerTokenValidator,
        RuntimeResourcePolicy? resourcePolicy = null)
    {
        resourcePolicy ??= RuntimeResourcePolicies.Standard;
        services.AddLogging();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.MaxDepth = RuntimeResourcePolicies.ProtocolCeilings.MaximumValueDepth + 8);
        services.AddSingleton(bearerTokenValidator);
        services.AddSingleton(packagePaths);
        services.AddSingleton(resourcePolicy);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(RuntimeTransportPolicyOptions.FromResourcePolicy(resourcePolicy));
        services.AddSingleton<RuntimeAuthPolicyOptions>();
        services.AddSingleton<RuntimeStackPolicyOptions>();
        services.AddSingleton(RuntimeRpcPolicyOptions.FromResourcePolicy(resourcePolicy));
        services.AddSingleton(new RuntimeProcessPolicyOptions
        {
            EffectiveResourcePolicy = resourcePolicy,
        });
        services.AddSingleton<RuntimeLifecyclePolicyOptions>();
        services.AddSingleton<RuntimePackageRecoveryPolicyOptions>();
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
        services.AddSingleton<RuntimePackageRecoveryQueue>();
        services.AddSingleton<RuntimeRpcCatalog>();
        services.AddSingleton<RuntimeRpcPermissionStore>();
        services.AddSingleton<RuntimeRpcReadinessEvaluator>();
        services.AddSingleton(provider => new PackageLogStreamService(
            provider.GetRequiredService<RuntimePackagePaths>().PackageDataRootPath,
            provider.GetRequiredService<RuntimeProtocolDescriptor>().RuntimeInstanceId));
        services.AddSingleton(provider => new RuntimeSessionOwner(
            provider.GetRequiredService<ILogger<RuntimeSessionOwner>>(),
            provider.GetRequiredService<RuntimeEventStreamService>(),
            provider.GetRequiredService<RuntimeAuthPolicyOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IHostApplicationLifetime>(),
            provider.GetRequiredService<PackageUiSnapshotStore>(),
            provider.GetRequiredService<RuntimeLifecyclePolicyOptions>(),
            provider.GetRequiredService<RuntimeRpcCatalog>(),
            provider.GetRequiredService<RuntimeRpcReadinessEvaluator>(),
            provider.GetRequiredService<RuntimePackageRecoveryQueue>()));
        services.AddSingleton(provider => provider.GetRequiredService<RuntimeSessionOwner>().State);
        services.AddSingleton<RuntimePackageRecoveryCoordinator>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<RuntimePackageRecoveryCoordinator>());
        services.AddSingleton<RuntimeRpcAppSessionManager>();
        services.AddSingleton<RuntimeRpcBroker>();
        services.AddSingleton<RuntimeRpcPermissionService>();
        services.AddSingleton<RuntimeSnapshotService>();
        services.AddSingleton(provider => new PackageSessionLoadService(
            provider.GetRequiredService<ILogger<PackageSessionLoadService>>(),
            provider.GetRequiredService<RuntimePackagePaths>(),
            rpcBroker: provider.GetRequiredService<RuntimeRpcBroker>(),
            processPolicy: provider.GetRequiredService<RuntimeProcessPolicyOptions>(),
            hostStopping: provider.GetService<IHostApplicationLifetime>()?.ApplicationStopping
                          ?? CancellationToken.None,
            packageSessionState: provider.GetRequiredService<PackageSessionState>()));
        services.AddSingleton<PackageSessionReconciler>();
        services.AddSingleton<PackageSessionPublisher>();
        services.AddSingleton<PackageLifecycleStageStore>();
        services.AddSingleton<RuntimePackageUiService>();
        services.AddSingleton<PackageSessionLifecycleService>();
        services.AddSingleton<InstalledPackageLifecycleService>();
        services.AddHostedService<PackageStageCleanupService>();
        services.AddSingleton<PackageSessionCommandService>();
        services.AddSingleton<PackageDataAccessService>();
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
