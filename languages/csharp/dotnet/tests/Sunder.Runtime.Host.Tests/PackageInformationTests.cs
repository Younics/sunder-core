using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageInformationTests
{
    [Fact]
    public async Task DisabledDevelopmentSourceRemainsAuthoritativeAndMetadataDoesNotStartWorkers()
    {
        var state = new PackageSessionState(NullLogger.Instance, () => { }, _ => { });
        var descriptor = new SessionPackageDescriptor("example.package", "Example", "2.0.0", PackageHostRoles.Runtime, null,
            false, PackageReadinessState.Failed, [], PackageFailureOrigin.RuntimeActivation, "failed", null, 1);
        var source = new RuntimePackageSource("example.package", PackageSourceKind.Dev, "unused", ContentIdentity: new('b', 64),
            Manifest: new SunderPackageManifest
            {
                Id = "example.package", Version = "2.0.0", Summary = "Current development source",
                DependsOn = [new() { PackageId = "example.agent", VersionRange = ">=1.0.0", Presence = "optional" }],
                Targets = [new() { Role = "runtime", Rid = "osx-arm64", RequiredHostCapabilities = ["secrets.v1"] }],
            });
        await state.PublishSessionAsync(new ActivePackageSession(null, new Dictionary<string, ActiveLoadedPackage>(),
            new Dictionary<string, SessionPackageDescriptor> { [descriptor.PackageId] = descriptor }, readySources: [source]));
        try
        {
            var result = state.GetPackageInformation(descriptor.PackageId);
            Assert.True(result.HasSource);
            var info = Assert.IsType<PackageInformationDescriptor>(result.Information);
            Assert.Equal("2.0.0", info.Version);
            Assert.Equal(PackageSourceKind.Dev, info.SourceKind);
            Assert.Equal("Current development source", info.Summary);
            Assert.Equal(new string('b', 64), info.ContentIdentity);
            Assert.Equal(new PackagePlatformTargetDescriptor("runtime", "osx-arm64"), Assert.Single(info.DeclaredTargets!));
            Assert.Equal("optional", Assert.Single(info.Dependencies).Presence);
            Assert.Empty(state.GetActivePackages());
            Assert.False(state.GetPackageInformation("absent.package").HasSource);
        }
        finally { await state.ClearActiveSessionAsync(); }
    }

    [Fact]
    public void AcquiredRoleScopeIsPreservedAndUnknownSourceIsNotAnEmptyDeclaration()
    {
        var source = new RuntimePackageSource("example.package", PackageSourceKind.Installed, "unused", ContentIdentity: new('a', 64),
            Manifest: new SunderPackageManifest { Id = "example.package", Version = "1.0.0", Targets = [
                new() { Role = "app", Rid = "osx-arm64", RequiredHostCapabilities = ["views.v1"] },
                new() { Role = "runtime", Rid = "osx-arm64", RequiredHostCapabilities = ["secrets.v1"] },
                new() { Role = "runtime", Rid = "win-x64", RequiredHostCapabilities = ["secrets.v1"] }] })
        { AcquiredTargets = [new("runtime", "osx-arm64")] };
        var info = Assert.IsType<PackageInformationDescriptor>(PackageInformationProjection.FromSource(source));
        Assert.Equal("secrets.v1", Assert.Single(info.Features).Capability);
        Assert.Equal(3, info.DeclaredTargets!.Count);
        Assert.Contains(new PackagePlatformTargetDescriptor("app", "osx-arm64"), info.DeclaredTargets);
        Assert.Contains(new PackagePlatformTargetDescriptor("runtime", "win-x64"), info.DeclaredTargets);
        Assert.Single(source.AcquiredTargets!); // Declaration metadata does not expand acquisition or activation.
        Assert.Null(PackageInformationProjection.FromSource(source with { ContentIdentity = null }));
    }
}
