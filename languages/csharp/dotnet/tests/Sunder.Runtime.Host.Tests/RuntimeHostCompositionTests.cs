using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeHostCompositionTests
{
    [Fact]
    public async Task ProductionComposition_ValidatesAndResolvesEveryRuntimeService()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new RuntimePackagePaths(root);
            var services = new ServiceCollection();
            services.AddSingleton<IHostApplicationLifetime>(new TestApplicationLifetime());
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            Assert.NotNull(provider.GetRequiredService<PackageSessionLifecycleService>());
            Assert.NotNull(provider.GetRequiredService<InstalledPackageLifecycleService>());
            Assert.NotNull(provider.GetRequiredService<DevPackageWatchService>());
            Assert.NotNull(provider.GetRequiredService<PackageLogStreamService>());
            Assert.NotNull(provider.GetRequiredService<RuntimeEventStreamService>());
            Assert.NotNull(provider.GetRequiredService<RuntimePackageRecoveryCoordinator>());
            Assert.NotNull(provider.GetRequiredService<RuntimeContentTransferService>());
            Assert.NotNull(provider.GetRequiredService<RuntimePackageUiService>());
            Assert.NotNull(provider.GetRequiredService<PackageSessionCommandService>());
            Assert.NotNull(provider.GetRequiredService<PackageDataAccessService>());
            Assert.NotNull(provider.GetRequiredService<PackageSettingsAccessService>());
            Assert.NotNull(provider.GetRequiredService<PackageAuthAccessService>());
            Assert.NotNull(provider.GetRequiredService<RuntimeStackExportService>());
            Assert.NotNull(provider.GetRequiredService<RuntimeStackImportService>());
            Assert.NotNull(provider.GetRequiredService<PackageCallbackAccessService>());
            Assert.NotNull(provider.GetRequiredService<PackageCallbackServer>());
            Assert.NotNull(provider.GetRequiredService<RegistryCredentialStore>());
            Assert.NotNull(provider.GetRequiredService<RegistryAuthCoordinator>());
            Assert.NotNull(provider.GetRequiredService<ExecutionRuntimeArtifactDownloader>());
            Assert.NotNull(provider.GetRequiredService<ExecutionRuntimeManagementService>());
            Assert.NotNull(provider.GetRequiredService<ExecutionRuntimeStartupMaintenance>());
            Assert.NotNull(provider.GetRequiredService<RegistryPackagePlanResolver>());
            Assert.NotNull(provider.GetRequiredService<RegistryPackageChangeOrchestrator>());
            Assert.NotNull(provider.GetRequiredService<RegistryAuthenticatedOperations>());
            var runtimeLayout = provider.GetRequiredService<ExecutionRuntimeStoreLayout>();
            var runtimeStore = provider.GetRequiredService<ExecutionRuntimeStore>();
            Assert.Equal(paths.ExecutionRuntimeStoreRootPath, runtimeLayout.RootPath);
            Assert.IsType<ExecutionRuntimeLaunchResolver>(
                provider.GetRequiredService<IExecutionRuntimeLaunchResolver>());
            await runtimeStore.InitializeAsync();
            Assert.All(runtimeLayout.DurableRootPaths, path => Assert.True(Directory.Exists(path)));

            Assert.Same(
                provider.GetRequiredService<RuntimeSessionOwner>().State,
                provider.GetRequiredService<PackageSessionState>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RegistryProductionServices_DoNotDependOnCompatibilityFacade()
    {
        var productionTypes = new[]
        {
            typeof(RegistryPackageChangeOrchestrator),
            typeof(RegistryAuthenticatedOperations),
        };

        var facadeDependencies = productionTypes
            .SelectMany(type => type.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .Where(parameter => parameter.ParameterType.Name.Contains("SessionService", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(facadeDependencies);
    }

    [Fact]
    public async Task ServiceProviderDisposal_CleansTransferAndSnapshotStores()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(root);
        var services = new ServiceCollection();
        services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));
        var provider = services.BuildServiceProvider();
        var transfers = provider.GetRequiredService<RuntimeContentTransferStore>();
        var snapshots = provider.GetRequiredService<PackageUiSnapshotStore>();
        await using var uploadContent = new MemoryStream([1, 2, 3]);
        await transfers.CreateUploadAsync(
            RuntimeUploadKind.Package,
            uploadContent,
            uploadContent.Length,
            expectedHash: null,
            "test.sunderpkg",
            "application/vnd.sunder.package",
            generation: 0,
            CancellationToken.None);
        var source = Path.Combine(root, "snapshot-source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source,
            "test.package",
            "1.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        snapshots.CreateSnapshots([
            CanonicalPackageTestBuilder.CreateTargetSource(
                source,
                Sunder.Runtime.Contracts.PackageSourceKind.Dev),
        ], generation: 1);
        Assert.NotEmpty(Directory.EnumerateFiles(paths.TransferRootPath));

        await provider.DisposeAsync();

        Assert.Empty(Directory.EnumerateFiles(paths.TransferRootPath));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ResourcePolicy_ConfiguresBrokerTransportProcessAndHandshakeExactly()
    {
        var root = CreateTempDirectory();
        var policy = new RuntimeResourcePolicy(
            3 * 1024 * 1024,
            23,
            123_456,
            1_234,
            80 * 1024,
            9 * 1024 * 1024);
        try
        {
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(
                new RuntimePackagePaths(root),
                new RuntimeBearerTokenValidator("test-runtime-token"),
                policy);
            await using var provider = services.BuildServiceProvider();

            var rpc = provider.GetRequiredService<RuntimeRpcPolicyOptions>();
            var transport = provider.GetRequiredService<RuntimeTransportPolicyOptions>();
            var process = provider.GetRequiredService<RuntimeProcessPolicyOptions>();
            var handshake = provider.GetRequiredService<RuntimeProtocolDescriptor>().Handshake;
            var httpJson = provider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;

            Assert.Same(policy, provider.GetRequiredService<RuntimeResourcePolicy>());
            Assert.Equal(policy.MaximumRpcValueBytes, rpc.MaxPayloadBytes);
            Assert.Equal(policy.MaximumValueDepth, rpc.MaxPayloadDepth);
            Assert.Equal(policy.MaximumValueNodes, rpc.MaxPayloadNodes);
            Assert.Equal(policy.MaximumObjectMembers, rpc.MaxObjectMembers);
            Assert.Equal(policy.MaximumInlineDecodedBytes, rpc.MaxInlineDecodedBytes);
            Assert.Equal(policy.MaximumContentReferenceBytes, transport.MaxRpcContentBytes);
            Assert.Equal(36L * 1024 * 1024, transport.MaxRpcContentBytesPerCall);
            Assert.Equal(144L * 1024 * 1024, transport.MaxRpcContentBytesPerPackage);
            Assert.Equal(288L * 1024 * 1024, transport.MaxRpcContentBytesPerRuntime);
            Assert.Same(policy, process.EffectiveResourcePolicy);
            Assert.Equal(72, httpJson.SerializerOptions.MaxDepth);
            Assert.Contains(RuntimeProtocolFeatures.RuntimeResourcePolicyV1, handshake.SupportedFeatures);
            Assert.Contains(RuntimeProtocolFeatures.PackageRuntimeRecoveryV1, handshake.SupportedFeatures);
            Assert.Contains(RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1, handshake.SupportedFeatures);
            Assert.DoesNotContain("execution-runtime-management.v1", handshake.SupportedFeatures);
            Assert.DoesNotContain("execution-runtime-updates.v1", handshake.SupportedFeatures);
            Assert.Equal(
                RuntimeResourcePolicyContract.GetEffectiveProfileHash(policy),
                handshake.ResourceProfile?.EffectiveProfileHash);
            Assert.Equal(policy.MaximumRpcValueBytes, handshake.ResourceProfile?.MaximumRpcValueBytes);
            Assert.Equal(
                policy.MaximumContentReferenceBytes,
                handshake.ResourceProfile?.MaximumContentReferenceBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StandardAndLargePolicies_DeriveClosedRuntimeLimits()
    {
        var standardRpc = RuntimeRpcPolicyOptions.FromResourcePolicy(RuntimeResourcePolicies.Standard);
        var standardTransport = RuntimeTransportPolicyOptions.FromResourcePolicy(RuntimeResourcePolicies.Standard);
        var largeRpc = RuntimeRpcPolicyOptions.FromResourcePolicy(RuntimeResourcePolicies.Large);
        var largeTransport = RuntimeTransportPolicyOptions.FromResourcePolicy(RuntimeResourcePolicies.Large);

        Assert.Equal(8 * 1024 * 1024, standardRpc.MaxPayloadBytes);
        Assert.Equal(524_288, standardRpc.MaxPayloadNodes);
        Assert.Equal(4_096, standardRpc.MaxObjectMembers);
        Assert.Equal(512 * 1024, standardRpc.MaxInlineDecodedBytes);
        Assert.Equal(64L * 1024 * 1024, standardTransport.MaxRpcContentBytes);
        Assert.Equal(256L * 1024 * 1024, standardTransport.MaxRpcContentBytesPerCall);
        Assert.Equal(1024L * 1024 * 1024, standardTransport.MaxRpcContentBytesPerPackage);
        Assert.Equal(2L * 1024 * 1024 * 1024, standardTransport.MaxRpcContentBytesPerRuntime);

        Assert.Equal(16 * 1024 * 1024, largeRpc.MaxPayloadBytes);
        Assert.Equal(2_097_152, largeRpc.MaxPayloadNodes);
        Assert.Equal(16_384, largeRpc.MaxObjectMembers);
        Assert.Equal(2 * 1024 * 1024, largeRpc.MaxInlineDecodedBytes);
        Assert.Equal(256L * 1024 * 1024, largeTransport.MaxRpcContentBytes);
        Assert.Equal(RuntimeTransportPolicyOptions.MaximumRpcContentBytesPerCall, largeTransport.MaxRpcContentBytesPerCall);
        Assert.Equal(RuntimeTransportPolicyOptions.MaximumRpcContentBytesPerPackage, largeTransport.MaxRpcContentBytesPerPackage);
        Assert.Equal(RuntimeTransportPolicyOptions.MaximumRpcContentBytesPerRuntime, largeTransport.MaxRpcContentBytesPerRuntime);
    }

    [Fact]
    public void PackageRecoveryPolicy_DefaultsAreBoundedAndEnabled()
    {
        var policy = new RuntimePackageRecoveryPolicyOptions();

        Assert.True(policy.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), policy.FailureWindow);
        Assert.Equal(
            [250d, 500d, 1000d, 2000d, 4000d],
            policy.RestartDelays.Select(delay => delay.TotalMilliseconds).ToArray());
        Assert.Equal(6, policy.SuspensionFailureCount);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SunderRuntimeCompositionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
