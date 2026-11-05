using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Compatibility;

namespace Sunder.Runtime.Host.TestSupport;

internal static partial class ExecutionRuntimeFixture
{
    internal static async Task<WebApplication> StartOfficialAdoptionHttpAsync(ManagementFixture fixture)
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

    internal static async Task<RegistryArtifactFixture> ConfigureReviewedPackageAsync(
        ManagementFixture fixture, PackagePolicyHandler registry, InstalledPackageRecord original, string origin, string version, string fingerprint)
    {
        var published = await RegistryArtifactFixture.CreateAsync(fixture.Paths.RootPath,
            original.PackageId, origin, original.PackageId, original.Version, version, typeof(ExecutionRuntimeFixture).Assembly.Location,
            runtimeIdentifiers: [PackageTargetSelection.GetCurrentRuntimeIdentifier()], roles: ["runtime"],
            runtimeRequiredHostCapabilities: ["core.v1", "execution-runtime.v1", "rpc.v1", "worker-protocol.v1",
                SunderSdkCapabilities.DockerEnvironmentV1]);
        var authority = ActivePolicyTestContext.Allowed(new("", [], [], null));
        var plan = new RegistryResolveInstallPlanResponse(true, [published.PlanItem], [], [], [], [], fingerprint,
            fixture.Provider.GetRequiredService<TimeProvider>().GetUtcNow().AddMinutes(5))
        {
            PackagePolicy = authority.PackagePolicy,
            ResolutionPolicy = new(authority.PackagePolicy, [], [], [], []),
        };
        registry.PlansByOrigin[new Uri(origin).GetLeftPart(UriPartial.Authority)] = plan;
        registry.Artifacts = published.ContentByUrl;
        return published;
    }
}
