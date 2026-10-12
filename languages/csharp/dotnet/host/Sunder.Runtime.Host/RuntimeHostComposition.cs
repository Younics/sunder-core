using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sunder.Package.Projections;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host;

internal static class RuntimeHostComposition
{
    internal static IServiceCollection AddRuntimeHostServices(
        this IServiceCollection services,
        RuntimePackagePaths packagePaths,
        RuntimeBearerTokenValidator bearerTokenValidator,
        RuntimeResourcePolicy? resourcePolicy = null,
        string? packageSessionStartupTracePath = null,
        PackageSessionConcurrencyOptions? packageSessionConcurrency = null)
    {
        resourcePolicy ??= RuntimeResourcePolicies.Standard;
        packageSessionConcurrency ??= new PackageSessionConcurrencyOptions();
        packageSessionConcurrency.Validate();
        services.AddLogging();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.MaxDepth = RuntimeResourcePolicies.ProtocolCeilings.MaximumValueDepth + 8);
        services.AddSingleton(bearerTokenValidator);
        services.AddSingleton(packagePaths);
        services.AddSingleton(resourcePolicy);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider => PackageSessionStartupTraceWriter.Open(
            packageSessionStartupTracePath,
            provider.GetRequiredService<TimeProvider>()));
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
        services.AddSingleton(packageSessionConcurrency);
        services.AddSingleton<RuntimeProtocolDescriptor>();
        services.AddSingleton(provider => new PackageProjectionCache(
            provider.GetRequiredService<RuntimePackagePaths>().RootPath,
            timeProvider: provider.GetRequiredService<TimeProvider>(),
            executionRuntimeLeaseRootPath: provider.GetRequiredService<ExecutionRuntimeStoreLayout>().LeaseRootPath));
        services.AddSingleton<PackageProjectionMaintenance>();
        services.AddSingleton<RuntimeOperationGate>();
        services.AddSingleton<RuntimeResetChallengeService>();
        services.AddSingleton<ExecutionRuntimePhaseTelemetry>();
        services.AddSingleton<ExecutionRuntimeStoreLayout>();
        services.AddSingleton<ExecutionRuntimeManagementState>();
        services.AddSingleton<ExecutionRuntimeManagementPlans>();
        services.AddSingleton<ExecutionRuntimeInstallationService>();
        services.AddSingleton<PackageRuntimePrerequisiteService>();
        services.AddSingleton<ExecutionRuntimeUsageService>();
        services.AddSingleton<ExecutionRuntimeUpdateService>();
        services.AddSingleton(provider => new ExecutionRuntimeActivationUpdates(
            token => provider.GetRequiredService<ExecutionRuntimeUpdateService>().ApplyPreparedWhenStoppedAsync(token),
            provider.GetRequiredService<ILogger<ExecutionRuntimeActivationUpdates>>()));
        services.AddHostedService<ExecutionRuntimeUpdateScheduler>();
        services.AddSingleton<IPolicyContinuousClock, PolicyContinuousClock>();
        services.AddSingleton<PackageLifecyclePolicyCache>();
        services.AddSingleton<PackagePolicyAdmission>();
        services.AddSingleton<InstalledPackagePolicyCoordinator>();
        services.AddSingleton<IAppPolicyProcessController, AppPolicyProcessController>();
        services.AddSingleton<AppProcessPolicySessions>();
        services.AddSingleton<IAppPolicySnapshotSource>(provider => provider.GetRequiredService<PackageUiSnapshotStore>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<AppProcessPolicySessions>());
        services.AddSingleton<RegistryActivePolicyClient>();
        services.AddSingleton<RegistryPackagePolicySynchronizer>();
        services.AddSingleton<DevelopmentImportAuthorityStore>();
        services.AddSingleton<DevelopmentImportAuthorityPreparation>();
        services.AddSingleton<ActivePackagePolicySynchronizer>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ActivePackagePolicySynchronizer>());
        services.AddSingleton<InstalledPackageStore>();
        services.AddSingleton<SunderPackageArchiveInstaller>();
        services.AddSingleton<PackageStoreCoordinator>();
        services.AddSingleton<PackageExecutionRuntimeGarbageCollectionAuthority>();
        services.AddSingleton<IExecutionRuntimeGarbageCollectionAuthority>(provider =>
            provider.GetRequiredService<PackageExecutionRuntimeGarbageCollectionAuthority>());
        services.AddSingleton(provider => new ExecutionRuntimeStore(
            provider.GetRequiredService<ExecutionRuntimeStoreLayout>(),
            provider.GetRequiredService<IExecutionRuntimeGarbageCollectionAuthority>(),
            provider.GetRequiredService<TimeProvider>(),
            hostInstanceId: provider.GetRequiredService<RuntimeProtocolDescriptor>().RuntimeInstanceId,
            telemetry: provider.GetRequiredService<ExecutionRuntimePhaseTelemetry>(),
            materializer: provider.GetRequiredService<OfficialRuntimeMaterializer>()));
        services.AddSingleton<IExecutionRuntimeRetentionManager>(provider =>
            provider.GetRequiredService<ExecutionRuntimeStore>());
        services.AddSingleton<LocalExecutionRuntimeBindingResolver>();
        services.AddSingleton<IEphemeralExecutionRuntimeBindingResolver>(provider =>
            provider.GetRequiredService<LocalExecutionRuntimeBindingResolver>());
        services.AddSingleton<IExecutionRuntimeLaunchResolver, ExecutionRuntimeLaunchResolver>();
        services.AddSingleton(provider => new PackageUiSnapshotStore(
            provider.GetRequiredService<RuntimePackagePaths>(),
            provider.GetService<ILogger<PackageUiSnapshotStore>>(),
            provider.GetRequiredService<PackageProjectionCache>(),
            provider.GetRequiredService<RuntimeProtocolDescriptor>().RuntimeInstanceId));
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
             packageSessionState: provider.GetRequiredService<PackageSessionState>(),
             executionRuntimeLaunchResolver: provider.GetRequiredService<IExecutionRuntimeLaunchResolver>(),
             ephemeralExecutionRuntimeBindingResolver:
                 provider.GetRequiredService<IEphemeralExecutionRuntimeBindingResolver>(),
              executionRuntimeTelemetry: provider.GetRequiredService<ExecutionRuntimePhaseTelemetry>(),
              projectionCache: provider.GetRequiredService<PackageProjectionCache>(),
              hostInstanceId: provider.GetRequiredService<RuntimeProtocolDescriptor>().RuntimeInstanceId,
               concurrency: provider.GetRequiredService<PackageSessionConcurrencyOptions>(),
               developmentImports: provider.GetRequiredService<DevelopmentImportAuthorityStore>()));
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
        services.AddHttpClient(OfficialRuntimeHttpClient.ClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sunder-Runtime/1.0");
        }).ConfigurePrimaryHttpMessageHandler(OfficialRuntimeHttpClient.CreateHandler);
        services.AddSingleton<OfficialRuntimeHttpClient>();
        services.AddSingleton<OfficialRuntimeResolver>();
        services.AddSingleton<IOfficialRuntimePlatformProbe, OfficialRuntimePlatformProbe>();
        services.AddSingleton<OfficialRuntimeMaterializer>();
        services.AddSingleton(provider => new ExecutionRuntimeArtifactDownloader(
            provider.GetRequiredService<OfficialRuntimeHttpClient>(),
            provider.GetRequiredService<ExecutionRuntimeStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ExecutionRuntimePhaseTelemetry>()));
        services.AddSingleton<ExecutionRuntimeManagementService>();
        services.AddSingleton<ExecutionRuntimeStartupMaintenance>();
        services.AddSingleton<HostExecutionRuntimeBindingPreparer>();
        services.AddSingleton<IHostExecutionRuntimeBindingPreparer>(provider => provider.GetRequiredService<HostExecutionRuntimeBindingPreparer>());
        services.AddSingleton<RegistryPackageArtifactDownloader>();
        services.AddSingleton<RegistryPackagePlanResolver>();
        services.AddSingleton<RegistryPackageChangeOrchestrator>();
        services.AddSingleton<RegistryAuthenticatedOperations>();
        return services;
    }
}
