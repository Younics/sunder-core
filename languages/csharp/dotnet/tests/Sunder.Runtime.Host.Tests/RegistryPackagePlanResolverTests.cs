using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RegistryPackagePlanResolverTests
{
    [Fact]
    public async Task ResolveAsync_ProjectsExactInstalledRuntimeBindingsInCanonicalOrder()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new RuntimePackagePaths(root);
            const string packageId = "projection.package";
            const string version = "1.0.0";
            var installPath = paths.GetInstalledPackagePath(packageId, version);
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                installPath,
                packageId,
                version,
                typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: ["win-x64", "linux-x64"]);
            var registryPackageContentIdentity = new string('d', 64);
            var package = CanonicalPackageTestBuilder.CreateInstalledRecord(installPath, packageId, version) with
            {
                Provenance = new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.FollowTag,
                    "https://registry.example/",
                    packageId,
                    RequestedTag: "latest",
                    SourceIdentity: registryPackageContentIdentity),
            };
            var linuxBinding = package.ExecutionRuntimeBindings.Single(static binding => binding.Rid == "linux-x64") with
            {
                RuntimeVersion = "10.2.3",
                RuntimePackagingRevision = 7,
            };
            var windowsBinding = package.ExecutionRuntimeBindings.Single(static binding => binding.Rid == "win-x64") with
            {
                RuntimeVersion = "10.1.4",
                RuntimePackagingRevision = 3,
            };
            package = package with
            {
                ExecutionRuntimeBindings = [windowsBinding, linuxBinding],
            };
            var store = new InstalledPackageStore(paths);
            await store.WriteAsync([package]);

            RegistryResolvePackageChangesRequest? captured = null;
            var handler = new DelegateHandler(request =>
            {
                captured = request.Content!.ReadFromJsonAsync<RegistryResolvePackageChangesRequest>()
                    .GetAwaiter()
                    .GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(true, [], [], [], [], [])),
                };
            });
            var resolver = CreateResolver(store, handler);

            var result = await resolver.ResolveAsync(
                new RuntimeRegistryPackageBatchRequest(
                    "https://registry.example/",
                    [new RuntimeRegistryPackageChangeRequest("requested.package", null, [])]),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.NotNull(captured);
            var installed = Assert.Single(captured.InstalledPackages);
            Assert.Equal(registryPackageContentIdentity, installed.PackageContentIdentity);
            Assert.NotEqual(package.ContentIdentity, installed.PackageContentIdentity);
            Assert.Equal(
                ["linux-x64", "win-x64"],
                installed.RuntimeBindings.Select(static binding => binding.Rid));
            var projectedLinux = installed.RuntimeBindings[0];
            Assert.Equal(SunderPackageFormat.RuntimeHostRole, projectedLinux.Role);
            Assert.Equal(SunderPackageFormat.DotNetExecutionRuntimeId, projectedLinux.Requirement.RuntimeId);
            Assert.Equal(">=10.0.0 <11.0.0", projectedLinux.Requirement.VersionRange);
            Assert.Equal(SunderPackageFormat.DotNetWorkerModuleAbi, projectedLinux.Requirement.ModuleAbi);
            Assert.Equal(linuxBinding.RuntimeId, projectedLinux.Runtime.RuntimeId);
            Assert.Equal(linuxBinding.RuntimeVersion, projectedLinux.Runtime.Version);
            Assert.Equal(linuxBinding.RuntimePackagingRevision, projectedLinux.Runtime.PackagingRevision);
            Assert.Equal(linuxBinding.Rid, projectedLinux.Runtime.Rid);
            Assert.Equal(linuxBinding.ModuleAbi, projectedLinux.Runtime.ModuleAbi);
            Assert.Equal(windowsBinding.RuntimeVersion, installed.RuntimeBindings[1].Runtime.Version);
            Assert.Equal(windowsBinding.RuntimePackagingRevision, installed.RuntimeBindings[1].Runtime.PackagingRevision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_PreservesTypedRuntimeConflictFromHttpConflictResponse()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new InstalledPackageStore(new RuntimePackagePaths(root));
            var conflict = new RegistryExecutionRuntimeResolutionConflict(
                ExecutionRuntimeResolutionConflictCodes.RidUnavailable,
                "No compatible execution runtime artifact exists for the requested RID.",
                new RegistryExecutionRuntimeRequirement(
                    SunderPackageFormat.DotNetExecutionRuntimeId,
                    ">=10.0.0 <11.0.0",
                    SunderPackageFormat.DotNetWorkerModuleAbi),
                "linux-x64",
                "requested.package",
                SunderPackageFormat.RuntimeHostRole);
            var handler = new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(
                    false,
                    [],
                    [],
                    [],
                    [],
                    [],
                    RuntimeConflicts: [conflict])),
            });

            var result = await CreateResolver(store, handler).ResolveAsync(
                new RuntimeRegistryPackageBatchRequest(
                    "https://registry.example/",
                    [new RuntimeRegistryPackageChangeRequest("requested.package", null, [])]),
                CancellationToken.None);

            Assert.False(result.Success);
            var projected = Assert.Single(result.RuntimeConflicts);
            Assert.Equal(ExecutionRuntimeResolutionConflictCodes.RidUnavailable, projected.Code);
            Assert.Empty(result.Errors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("role")]
    [InlineData("rid")]
    [InlineData("requirement")]
    [InlineData("version-range")]
    public void ProjectRuntimeBindings_RejectsNonExactBindingInventory(string mismatch)
    {
        var (package, manifest, binding) = CreateProjectionInput();
        IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> mismatched = mismatch switch
        {
            "missing" => [],
            "extra" => [binding, binding with { Rid = "win-x64" }],
            "duplicate" => [binding, binding],
            "role" => [binding with { Role = SunderPackageFormat.AppHostRole }],
            "rid" => [binding with { Rid = "win-x64" }],
            "requirement" =>
            [
                binding with
                {
                    RuntimeId = SunderPackageFormat.NodeExecutionRuntimeId,
                    ModuleAbi = SunderPackageFormat.NodeWorkerModuleAbi,
                },
            ],
            "version-range" => [binding with { RuntimeVersion = "11.0.0" }],
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            RegistryPackagePlanResolver.ProjectRuntimeBindings(
                package with { ExecutionRuntimeBindings = mismatched },
                manifest));

        Assert.Contains(package.PackageId, error.Message, StringComparison.Ordinal);
        Assert.Contains("do not exactly match", error.Message, StringComparison.Ordinal);
    }

    private static RegistryPackagePlanResolver CreateResolver(
        InstalledPackageStore store,
        HttpMessageHandler handler)
        => new(
            new RegistryHttpClient(
                new FakeHttpClientFactory(handler),
                new RuntimeTransportPolicyOptions(),
                TimeProvider.System),
            store,
            new ExecutionRuntimeHostTrustService(
                "test-runtime-trust",
                1,
                developmentTrustEnabled: false,
                productionKeys: []),
            NullLogger<RegistryPackagePlanResolver>.Instance);

    private static (
        InstalledPackageRecord Package,
        SunderPackageManifest Manifest,
        InstalledPackageExecutionRuntimeBindingRecord Binding) CreateProjectionInput()
    {
        var binding = new InstalledPackageExecutionRuntimeBindingRecord(
            SunderPackageFormat.RuntimeHostRole,
            "linux-x64",
            SunderPackageFormat.DotNetExecutionRuntimeId,
            "10.1.2",
            2,
            SunderPackageFormat.DotNetWorkerModuleAbi,
            new string('a', 64),
            new string('b', 64),
            1024,
            "https://registry.example/",
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            "test-signing-key",
            1,
            1,
            ExecutionRuntimeLifecycleStates.Supported,
            DateTimeOffset.Parse("2026-08-28T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-28T10:01:00Z"));
        var package = new InstalledPackageRecord(
            "projection.package",
            "Projection Package",
            null,
            "1.0.0",
            null,
            "/unused",
            "/unused/manifest/sunder-package.json",
            new string('c', 64),
            [],
            [],
            true,
            DateTimeOffset.Parse("2026-08-28T09:00:00Z"),
            InstalledPackageProvenanceRecord.Unknown,
            [],
            [binding]);
        var manifest = new SunderPackageManifest
        {
            Targets =
            [
                new SunderPackageTargetManifest
                {
                    Role = SunderPackageFormat.RuntimeHostRole,
                    Rid = "linux-x64",
                    ExecutionRuntime = new SunderPackageExecutionRuntimeManifest
                    {
                        Id = SunderPackageFormat.DotNetExecutionRuntimeId,
                        VersionRange = ">=10.0.0 <11.0.0",
                        ModuleAbi = SunderPackageFormat.DotNetWorkerModuleAbi,
                    },
                },
            ],
        };
        return (package, manifest, binding);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-registry-plan-resolver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
