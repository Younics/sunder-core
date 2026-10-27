using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeProtocolHttpIntegrationTests
{
    [Fact]
    public async Task SelectedPackageInformationRequiresAuthenticationAndReadsTheExactFaultedOverlay()
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-package-information-http");
        await using var server = await RuntimeHttpServer.StartAsync(
            app => app.MapGroup("/api/v1").MapInstalledPackageEndpoints(),
            services => services.AddRuntimeHostServices(new RuntimePackagePaths(temporary.RootPath), new RuntimeBearerTokenValidator(RuntimeHttpServer.Token)));
        var owner = server.Services.GetRequiredService<RuntimeSessionOwner>();
        var source = new RuntimePackageSource("example.package", PackageSourceKind.Dev, temporary.GetPath("source"),
            ContentIdentity: new('c', 64), Manifest: new SunderPackageManifest
            {
                Id = "example.package", Version = "2.0.0",
                DependsOn = [new() { PackageId = "example.agent", VersionRange = ">=1.0.0", Presence = "optional" }],
                Targets = [new() { Role = "runtime", Rid = "osx-arm64", RequiredHostCapabilities = ["storage.v1"] }],
            });
        var descriptor = new SessionPackageDescriptor(source.PackageId, "Example", "2.0.0", PackageHostRoles.Runtime, null,
            false, PackageReadinessState.Failed, [], PackageFailureOrigin.RuntimeActivation, "example failure", null, 1);
        await owner.State.PublishSessionAsync(new ActivePackageSession(null, new Dictionary<string, ActiveLoadedPackage>(),
            new Dictionary<string, SessionPackageDescriptor> { [source.PackageId] = descriptor }, readySources: [source]));
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        try
        {
            using var denied = await client.GetAsync("api/v1/packages/example.package/information");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RuntimeHttpServer.Token);
            var information = await client.GetFromJsonAsync<PackageInformationDescriptor>("api/v1/packages/example.package/information");
            Assert.NotNull(information);
            Assert.Equal("2.0.0", information.Version);
            Assert.Equal(source.ContentIdentity, information.ContentIdentity);
            Assert.Equal(PackageSourceKind.Dev, information.SourceKind);
            Assert.Equal("optional", Assert.Single(information.Dependencies).Presence);
            Assert.Equal("storage.v1", Assert.Single(information.Features).Capability);
        }
        finally { await owner.State.ClearActiveSessionAsync(); }
    }
}
