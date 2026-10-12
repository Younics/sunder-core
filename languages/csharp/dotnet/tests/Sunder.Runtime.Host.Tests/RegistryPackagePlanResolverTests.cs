using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RegistryPackagePlanResolverTests
{
    [Fact]
    public async Task ResolveAsync_ExportsPackageProjectionsAndSourceIdentityWithoutRuntimeSelectionAuthority()
    {
        using var temp = TemporaryDirectoryScope.Create("official-registry-planning");
        var paths = new RuntimePackagePaths(temp.RootPath);
        var binding = InstalledPackageExecutionRuntimeBindingTests.Binding();
        var root = paths.GetInstalledPackagePath("projection.package", "1.0.0");
        CanonicalPackageTestBuilder.WriteExplodedPackage(root, "projection.package", "1.0.0", typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: ["runtime"], runtimeIdentifiers: [binding.Rid]);
        var package = CanonicalPackageTestBuilder.CreateInstalledRecord(root, "projection.package", "1.0.0", executionRuntimeBindings: [binding]) with
        { Provenance = new(InstalledPackageSourceKind.Registry, InstalledPackageVersionPolicy.FollowTag,
            "https://registry.example/", "projection.package", RequestedTag: "latest", SourceIdentity: new('d', 64)) };
        var store = new InstalledPackageStore(paths);
        await store.WriteAsync([package]);
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var resolver = new RegistryPackagePlanResolver(new(new Factory(client), new(), TimeProvider.System), store, NullLogger<RegistryPackagePlanResolver>.Instance);
        var result = await resolver.ResolveAsync(new("https://registry.example/", [new("requested.package", null, [])]), default);
        Assert.True(result.Success);
        var captured = handler.Captured!;
        Assert.Equal(package.Provenance.SourceIdentity, captured["installedPackages"]![0]!["packageContentIdentity"]!.GetValue<string>());
        Assert.False(captured.AsObject().ContainsKey("runtimeTrustProfile"));
        Assert.False(captured["installedPackages"]![0]!.AsObject().ContainsKey("runtimeBindings"));
        Assert.NotEmpty(captured["installedPackages"]![0]!["acquiredProjections"]!.AsArray());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("role")]
    [InlineData("rid")]
    [InlineData("requirement")]
    [InlineData("version-range")]
    public void ValidateLocalBindings_RejectsNonExactInventoryBeforeRegistryPlanning(string mismatch)
    {
        using var temp = TemporaryDirectoryScope.Create("official-plan-binding");
        var paths = new RuntimePackagePaths(temp.RootPath);
        var binding = InstalledPackageExecutionRuntimeBindingTests.Binding();
        var package = InstalledPackageStoreTests.CreatePackage(paths, "projection.package") with { ExecutionRuntimeBindings = [binding] };
        var manifest = new SunderPackageManifest { Targets = [new() { Role = "runtime", Rid = binding.Rid,
            ExecutionRuntime = new() { Id = binding.Requirement.RuntimeId, VersionRange = binding.Requirement.VersionRange, ModuleAbi = binding.Requirement.ModuleAbi } }] };
        var otherRid = SunderPackageFormat.SupportedRuntimeIdentifiers.First(rid => rid != binding.Rid);
        var changed = mismatch switch
        {
            "missing" => Array.Empty<InstalledPackageExecutionRuntimeBindingRecord>(),
            "extra" => [binding, binding with { Rid = otherRid }],
            "duplicate" => [binding, binding],
            "role" => [binding with { Role = "app" }],
            "rid" => [binding with { Rid = otherRid }],
            "requirement" => [binding with { Requirement = binding.Requirement with { ModuleAbi = SunderPackageFormat.NodeWorkerModuleAbi } }],
            _ => [binding with { Runtime = binding.Runtime with { VendorVersion = "11.0.0" } }],
        };
        Assert.Throws<InvalidDataException>(() => RegistryPackagePlanResolver.ValidateRuntimeBindings(package with { ExecutionRuntimeBindings = changed }, manifest));
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler : HttpMessageHandler
    {
        internal JsonNode? Captured { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Captured = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new RegistryResolveInstallPlanResponse(true, [], [], [], [], [])) };
        }
    }
}
