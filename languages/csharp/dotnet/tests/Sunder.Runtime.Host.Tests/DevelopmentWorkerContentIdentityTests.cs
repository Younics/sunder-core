using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class DevelopmentWorkerContentIdentityTests
{
    [Fact]
    public async Task AppOnlyContinuationKeepsExactPlatformPolicyAndCannotAuthorizeRevokedRecoveryBytes()
    {
        // This boundary case covers Registry-backed imports separately from the
        // real-process retention fixture's locally owned contract.
        using var policy = new ActivePolicyTestContext();
        var platform = ActivePolicyTestContext.Platform;
        await policy.RefreshAsync(packages: [], platforms: [platform]);
        var admission = new PackagePolicyAdmission(policy.Cache);
        var index = new SunderPackageContentIndex(1, [new(SunderPackageFormat.ManifestPath, new('a', 64), 10),
            new("payload/app/shared/assets/view.axaml", new('b', 64), 20)]);
        var source = new RuntimePackageSource("package", PackageSourceKind.Dev, "/dev/package", ContentIdentity: new('c', 64),
            Manifest: new()
            {
                Id = "package", Version = "1.0.0",
                PlatformImports = [new()
                {
                    Key = "core", AuthorityKind = "platform", AuthorityId = platform.AuthorityId,
                    AuthorityReleaseRange = ">=1.1.0 <2.0.0", SelectedAuthorityRelease = platform.ReleaseVersion,
                    SelectedAuthorityContentIdentity = platform.CatalogSha256,
                }],
                ContractBundles = [new()
                {
                    Source = "imported", AuthorityKind = "platform", AuthorityId = platform.AuthorityId,
                    SelectedAuthorityRelease = platform.ReleaseVersion, SelectedAuthorityContentIdentity = platform.CatalogSha256,
                }],
            }, ContentIndex: index)
        {
            DevelopmentWorkerContentIdentity = DevelopmentWorkerContentIdentity.Create(index),
            DevelopmentImports = new(1, "registry", "package", "1.0.0", new('a', 64), ActivePolicyTestContext.Origin, [], []),
        };
        SessionPackageDescriptor[] states = [new("package", "package", "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime,
            null, true, PackageReadinessState.Ready, [], null, null, null, 0)];
        var latest = source;
        for (var revision = 1; revision <= 2; revision++)
        {
            var changedIndex = index with { Files = [index.Files![0], index.Files[1] with { Sha256 = ActivePolicyTestContext.Hash("app-" + revision) }] };
            latest = source with { ContentIdentity = ActivePolicyTestContext.Hash("content-" + revision), ContentIndex = changedIndex };
            Assert.Equal(AppActivationFingerprint.Create(Guid.Empty, "package", [source], states, developmentWorkerContinuation: true),
                AppActivationFingerprint.Create(Guid.Empty, "package", [latest], states, developmentWorkerContinuation: true));
            var subjects = Assert.Single(admission.Subjects(latest));
            Assert.Equal(ActivePolicyTestContext.Origin, subjects.Origin);
            Assert.Empty(subjects.Packages);
            Assert.Equal(platform, Assert.Single(subjects.Platforms));
            admission.Require(latest);
        }
        admission.Require(source);
        Assert.NotEqual(PackageUiSnapshotStore.PolicySubject(source), PackageUiSnapshotStore.PolicySubject(latest));

        policy.Response = request => ActivePolicyTestContext.Allowed(request, revision: 2) with
        {
            PlatformCatalogs = request.PlatformCatalogs.Select(value => new RegistryPlatformPolicyDecision(value, "Revoked", "Denied",
                PackagePolicyErrorCodes.ContractAuthorityRevoked, ActivePolicyTestContext.ObservedAt)).ToArray(),
        };
        await policy.RefreshAsync(packages: [], platforms: [platform]);
        foreach (var checkedSource in new[] { source, latest })
            Assert.Equal(PackagePolicyErrorCodes.ContractAuthorityRevoked,
                Assert.Throws<PackagePolicyDeniedException>(() => admission.Require(checkedSource)).Code);
    }

    [Theory]
    [InlineData("payload/app/shared/assets/view.axaml", false)]
    [InlineData("payload/app/osx-arm64/lib/view.dll", false)]
    [InlineData("payload/runtime/shared/lib/service.dll", true)]
    [InlineData("payload/runtime/osx-arm64/lib/service.dll", true)]
    [InlineData("payload/shared/lib/shared.dll", true)]
    [InlineData("payload/shared/contracts/contract.json", true)]
    [InlineData("manifest/sunder-package.json", true)]
    public void ContinuationExcludesOnlyAppPayloadAndPinsMetadataAndSharedBytes(string path, bool invalidates)
    {
        var before = new SunderPackageContentIndex(1, [new(path, new('a', 64), 10)]);
        var after = new SunderPackageContentIndex(1, [new(path, new('b', 64), 10)]);
        var removed = new SunderPackageContentIndex(1, []);
        Assert.Equal(invalidates, DevelopmentWorkerContentIdentity.Create(before) != DevelopmentWorkerContentIdentity.Create(after));
        Assert.Equal(invalidates, DevelopmentWorkerContentIdentity.Create(before) != DevelopmentWorkerContentIdentity.Create(removed));
    }

    [Theory]
    [InlineData("app", false)]
    [InlineData("unqualified", true)]
    [InlineData("installed", true)]
    [InlineData("app-provider", true)]
    [InlineData("origin", true)]
    [InlineData("authority", true)]
    [InlineData("omission", true)]
    [InlineData("target", true)]
    public void WorkerContinuationRequiresValidatedDevelopmentEvidenceAndExactSecurityContext(string change, bool invalidates)
    {
        var index = new SunderPackageContentIndex(1, [new("manifest/sunder-package.json", new('a', 64), 10),
            new("payload/app/shared/assets/view.axaml", new('b', 64), 20)]);
        var source = new RuntimePackageSource("package", PackageSourceKind.Dev, "/dev/package", ContentIdentity: new('c', 64),
            Manifest: new() { Id = "package", Version = "1.0.0" }, ContentIndex: index)
        {
            DevelopmentWorkerContentIdentity = DevelopmentWorkerContentIdentity.Create(index),
        };
        if (change == "unqualified") source = source with { DevelopmentWorkerContentIdentity = null };
        if (change == "installed") source = source with { Kind = PackageSourceKind.Installed };
        if (change == "app-provider") source = source with { Manifest = new()
        {
            Id = "package", Version = "1.0.0", Provides = [new() { Role = "app", ProviderId = "app.provider" }],
        } };
        var changedIndex = index with { Files = [index.Files![0], index.Files[1] with { Sha256 = new('d', 64) }] };
        var next = source with { ContentIdentity = new('e', 64), ContentIndex = changedIndex };
        next = change switch
        {
            "origin" => next with { RegistryPolicyOrigin = "https://another.registry/" },
            "authority" => next with { DevelopmentImports = new(1, "local", "package", "1.0.0", new('a', 64), null, [], []) },
            "omission" => next with { DevelopmentOmittedPackages = ["provider"] },
            "target" => next with { SelectedTargetKey = new("runtime", "linux-x64") },
            _ => next,
        };
        SessionPackageDescriptor[] states = [new("package", "package", "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime,
            null, true, PackageReadinessState.Ready, [], null, null, null, 0)];
        Assert.Equal(invalidates, AppActivationFingerprint.Create(Guid.Empty, "package", [source], states, developmentWorkerContinuation: true)
            != AppActivationFingerprint.Create(Guid.Empty, "package", [next], states, developmentWorkerContinuation: true));
        // The same role hint cannot make App policy/full-content authority equal.
        Assert.NotEqual(AppActivationFingerprint.Create(Guid.Empty, "package", [source], states),
            AppActivationFingerprint.Create(Guid.Empty, "package", [next], states));
        Assert.NotEqual(PackageUiSnapshotStore.PolicySubject(source), PackageUiSnapshotStore.PolicySubject(next));
    }
}
