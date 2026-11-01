using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed class PackageInformationContractTests
{
    [Fact]
    public async Task SelectedPackageInformationUsesAuthenticatedReadAndRoundTripsPresenceAndDirection()
    {
        var value = new PackageInformationDescriptor("example.package", "1.0.0", new('a', 64), PackageSourceKind.Dev,
            [new("runtime", "osx-arm64", "storage.v1")],
            [new("example.agent", ">=1.0.0", "optional", [new("runtime", "example.tools", "2.0.0", "implements")])],
            [new("example.tools", "2.0.0", "service", [new("runtime", "tools")])], [])
        { DeclaredTargets = [new("runtime", "osx-arm64"), new("runtime", "win-x64")] };
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/handshake")
                return Reply(new RuntimeHandshakeResponse(RuntimeProtocol.Identity, 1, 1, 1, Guid.NewGuid(),
                    [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1], new("Sunder.Runtime.Host", "Development", "Development")));
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/packages/example.package/information", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
            return Reply(value);
        });
        using var client = new RuntimeManagementClient(() => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler);
        var copy = await client.GetPackageInformationAsync("example.package");
        Assert.Equal(value.ContentIdentity, copy.ContentIdentity);
        Assert.Equal(PackageSourceKind.Dev, copy.SourceKind);
        Assert.Equal(value.DeclaredTargets, copy.DeclaredTargets);
        var dependency = Assert.Single(copy.Dependencies);
        Assert.Equal("optional", dependency.Presence);
        Assert.Equal("implements", Assert.Single(dependency.Bindings).Kind);
        Assert.Throws<NotSupportedException>(() => ((IList<PackageRelationshipDescriptor>)copy.Dependencies).Clear());
    }

    [Fact]
    public void InformationCollectionsAreBoundedAndSnapshotNestedListsInBothTransports()
    {
        var source = new List<PackageIntegrationDescriptor> { new("runtime", "example.tools", "1.0.0", "uses") };
        var runtime = new PackageRelationshipDescriptor("example.owner", ">=1.0.0", "required", source);
        source.Clear();
        Assert.Single(runtime.Bindings);
        Assert.Throws<ArgumentException>(() => new PackageRelationshipDescriptor("owner", "*", "required",
            Enumerable.Repeat(new PackageIntegrationDescriptor("runtime", "tools", "1.0.0", "uses"), 257).ToArray()));
        var registry = new RegistryPackageInformation(new('a', 64), [],
            [new("example.owner", ">=1.0.0", "optional", [new("runtime", "example.tools", "1.0.0", "implements")])], [], []);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var copy = JsonSerializer.Deserialize<RegistryPackageInformation>(JsonSerializer.Serialize(registry, json), json)!;
        Assert.Equal("optional", Assert.Single(copy.Dependencies).Presence);
        Assert.Throws<NotSupportedException>(() => ((IList<RegistryPackageIntegration>)copy.Dependencies[0].Bindings).Clear());
    }

    [Fact]
    public void DeclaredTargetsAreBoundedImmutableAndDistinguishUnknownFromContractOnly()
    {
        var targets = new List<PackagePlatformTargetDescriptor> { new("app", "win-x64") };
        var value = new PackageInformationDescriptor("example.package", "1.0.0", new('a', 64), PackageSourceKind.Installed, [], [], [], [])
        { DeclaredTargets = targets };
        targets.Clear();
        Assert.Single(value.DeclaredTargets!);
        Assert.Throws<NotSupportedException>(() => ((IList<PackagePlatformTargetDescriptor>)value.DeclaredTargets!).Clear());
        Assert.Throws<ArgumentException>(() => value with
        { DeclaredTargets = Enumerable.Repeat(new PackagePlatformTargetDescriptor("app", "win-x64"), 13).ToArray() });
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var unknown = value with { DeclaredTargets = null };
        Assert.DoesNotContain("declaredTargets", JsonSerializer.Serialize(unknown, json), StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<PackageInformationDescriptor>(JsonSerializer.Serialize(unknown, json), json)!.DeclaredTargets);
        var shared = value with { DeclaredTargets = [] };
        Assert.Empty(JsonSerializer.Deserialize<PackageInformationDescriptor>(JsonSerializer.Serialize(shared, json), json)!.DeclaredTargets!);
    }

    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
