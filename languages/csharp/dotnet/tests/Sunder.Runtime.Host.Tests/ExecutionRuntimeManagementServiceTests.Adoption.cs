using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class ExecutionRuntimeManagementServiceTests
{
    [Fact]
    public async Task OfficialPrerequisiteInstall_RepreviewAndConfirmedSourceAdoptionUseActualHttpHandlers()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var originalRuntime = await fixture.EnsureAndBindPackageAsync("official.adoption");
        var original = Assert.Single(await fixture.InstalledPackages.ListAsync());
        fixture.CorruptInstalledContent(originalRuntime);
        const string origin = "https://registry.example/";
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var published = await RegistryRuntimeArchitectureTests.CreateRegistryArtifactAsync(fixture.Paths.RootPath,
            "official-adoption", origin, original.PackageId, original.Version, original.Version,
            runtimeIdentifiers: [rid], roles: [SunderPackageFormat.RuntimeHostRole]);
        var registry = fixture.Provider.GetRequiredService<PackagePolicyHandler>();
        var authority = ActivePolicyTestContext.Allowed(new("", [], [], null));
        registry.Plan = new(true, [published.PlanItem], [], [], [], [], new('e', 64), Now.AddMinutes(5))
        {
            PackagePolicy = authority.PackagePolicy,
            ResolutionPolicy = new(authority.PackagePolicy, [], [], [], []),
        };
        registry.Artifacts = published.ContentByUrl;
        fixture.Handler.Version = "10.0.2";
        fixture.Handler.ArchiveBytes = fixture.CreateArchiveBytes("10.0.2", 0x43);
        await using var app = await StartOfficialAdoptionHttpAsync(fixture);
        var uri = new Uri(Assert.Single(app.Urls));
        var adoption = new RuntimeRegistrySourceAdoptionRequest(original.PackageId, origin,
            Version: original.Version, DesiredTargets: [new("runtime", rid)], DryRun: true);
        using (var anonymous = new HttpClient { BaseAddress = uri })
        using (var denied = await anonymous.PostAsJsonAsync(RuntimeApiRoutes.PackageRuntimePrerequisites,
            new RuntimePackageRuntimePrerequisiteRequest { Adoption = adoption }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Empty(registry.PlansRequested);
        }
        using var client = new RuntimeManagementClient(() => new RuntimeConnectionInfo(uri, "management-test-token"));
        try
        {
            var pending = await client.PreviewPackageRuntimePrerequisitesAsync(new() { Adoption = adoption });
            Assert.True(pending.Success, pending.Message);
            Assert.False(pending.Satisfied);
            var install = Assert.IsType<RuntimeExecutionRuntimeInstallPlan>(Assert.Single(pending.Requirements).Installation);
            Assert.True(install.Success, install.Message);
            Assert.True(install.RetainUntilUninstall);
            Assert.Equal(0, registry.Downloads);
            var acquired = await client.ApplyExecutionRuntimeInstallAsync(new(install.Plan!.PlanId, install.Plan.Fingerprint));
            Assert.True(acquired.Success, acquired.Message);
            var untouched = Assert.Single(await fixture.InstalledPackages.ListAsync());
            Assert.Equal(original.ContentIdentity, untouched.ContentIdentity);
            Assert.Equal(original.Provenance, untouched.Provenance);
            Assert.Equal(originalRuntime.Identity.ToContract(), Assert.Single(untouched.ExecutionRuntimeBindings).Runtime);
            Assert.Equal(0, registry.Downloads);

            var ready = await client.PreviewPackageRuntimePrerequisitesAsync(new(ExpectedSourceFingerprint: pending.SourceFingerprint) { Adoption = adoption });
            Assert.True(ready.Satisfied, ready.Message);
            Assert.Equal(pending.SourceFingerprint, ready.SourceFingerprint);
            Assert.Equal(install.Resolution!.Artifact.Identity, Assert.Single(ready.Requirements).InstalledRuntime);
            var fenced = adoption with { RuntimePrerequisiteExpectation = new(ready.SourceFingerprint, ready.Rid) };
            var preview = await client.AdoptRegistryPackageSourceAsync(fenced);
            Assert.True(preview.Success, preview.Message);
            var committed = await client.AdoptRegistryPackageSourceAsync(fenced with
            {
                DryRun = false, Confirm = true,
                ExpectedResolutionFingerprint = Assert.Single(preview.PlanExpectations).ExpectedResolutionFingerprint,
                CapabilityConsents = preview.RequiredCapabilityConsents,
            });
            Assert.True(committed.Success, committed.Message + string.Join("; ", committed.Errors));
            var adopted = Assert.Single(await fixture.InstalledPackages.ListAsync());
            Assert.Equal(original.Version, adopted.Version);
            Assert.Equal(InstalledPackageSourceKind.Registry, adopted.Provenance.SourceKind);
            Assert.Equal(origin, adopted.Provenance.RegistryOrigin);
            Assert.Equal(published.PlanItem.PackageContentIdentity, adopted.SourcePackageContentIdentity);
            var binding = Assert.Single(adopted.ExecutionRuntimeBindings);
            Assert.Equal(install.Resolution.Artifact.Identity, binding.Runtime);
            Assert.Equal(ConfiguredOfficialSourceId.DotNet, binding.Runtime.SourceId);
            Assert.StartsWith("https://builds.dotnet.microsoft.com/", binding.InstalledArtifact.Provenance.FinalDownloadUrl, StringComparison.Ordinal);
            Assert.True(registry.Downloads > 0);
            Assert.All(registry.PlansRequested, request => Assert.True(request.Reinstall));
            Assert.Contains((await fixture.ManagementState.ReadAsync(default)).ManualInstalls, retained => retained.Runtime == binding.Runtime);
        }
        finally
        {
            await fixture.PackageLifecycle.ShutdownAsync(fixture.Provider.GetRequiredService<PackageSessionLifecycleService>());
            await app.StopAsync();
        }
    }

    private static async Task<WebApplication> StartOfficialAdoptionHttpAsync(ManagementFixture fixture)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(fixture.Provider.GetRequiredService<RuntimeBearerTokenValidator>());
        builder.Services.AddSingleton(fixture.Provider.GetRequiredService<RuntimeProtocolDescriptor>());
        builder.Services.AddSingleton(fixture.Provider.GetRequiredService<RuntimeOperationGate>());
        builder.Services.AddSingleton(fixture.ManagementState);
        builder.Services.AddSingleton(fixture.Installation);
        builder.Services.AddSingleton(fixture.Updates);
        builder.Services.AddSingleton(fixture.Management);
        builder.Services.AddSingleton(fixture.Prerequisites);
        builder.Services.AddSingleton(fixture.Provider.GetRequiredService<RegistryPackageChangeOrchestrator>());
        builder.Services.AddSingleton(fixture.Provider.GetRequiredService<RegistryAuthCoordinator>());
        builder.Services.AddSingleton(fixture.Provider.GetRequiredService<RegistryAuthenticatedOperations>());
        var app = builder.Build();
        app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
        app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
        app.MapRuntimeHandshakeEndpoint();
        app.MapGroup("/api/v1").MapExecutionRuntimeEndpoints().MapRegistryEndpoints();
        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }
}
