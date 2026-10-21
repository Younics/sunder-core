using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class ExecutionRuntimeManagementServiceTests
{
    [Fact]
    public async Task DevelopmentPrerequisites_PinCanonicalFolderAndRejectRebuildDriftWithoutInstallingPackage()
    {
        await using var fixture = await ManagementFixture.CreateAsync();
        var folder = Path.Combine(fixture.Root, "dev");
        CanonicalPackageTestBuilder.WriteExplodedPackage(folder, "development.package", "1.0.0", typeof(ExecutionRuntimeManagementServiceTests).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole], runtimeIdentifiers: [PackageTargetSelection.GetCurrentRuntimeIdentifier()]);
        var request = new RuntimePackageRuntimePrerequisiteRequest { DevelopmentFolder = folder };
        var preview = await fixture.Prerequisites.PreviewAsync(request, default);
        Assert.True(preview.Success);
        Assert.False(preview.Satisfied);
        Assert.Equal("development.package", preview.DevelopmentPackageId);
        var runtime = Assert.Single(preview.Requirements);
        Assert.Equal(fixture.EnsureRequest.Requirement, runtime.Requirement);
        Assert.Equal(fixture.Artifact.Identity, runtime.Installation!.Resolution!.Artifact.Identity);
        Assert.Empty(await fixture.InstalledPackages.ListAsync());
        Assert.DoesNotContain("download", fixture.Handler.Operations);

        await fixture.EnsureAsync();
        var ready = await fixture.Prerequisites.PreviewAsync(request with { ExpectedSourceFingerprint = preview.SourceFingerprint }, default);
        Assert.True(ready.Satisfied);
        Assert.Equal(preview.SourceFingerprint, ready.SourceFingerprint);
        // Rebuild with valid new content: hashes pass, but the prior approval is stale.
        File.AppendAllText(Path.Combine(folder, "payload", "shared", "lib", Path.GetFileName(typeof(ExecutionRuntimeManagementServiceTests).Assembly.Location)), "changed");
        CanonicalPackageTestBuilder.WriteContentIndex(folder);
        await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() => fixture.Prerequisites.PreviewAsync(
            request with { ExpectedSourceFingerprint = preview.SourceFingerprint }, default));
        Assert.Empty(await fixture.InstalledPackages.ListAsync());
    }
}
