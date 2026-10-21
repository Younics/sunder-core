using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class AppActivationFingerprintTests
{
    [Fact]
    public async Task ValidatedWorkerOnlyEditPreservesAppComparisonAndSnapshotButChangesFullPolicyAuthority()
    {
        using var root = TemporaryDirectoryScope.Create("app-role-continuation");
        var folder = CanonicalPackageTestBuilder.WriteExplodedPackage(root.GetPath("package"), "package", "1.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        var workerAsset = Path.Combine(folder, "payload", "runtime", PackageTargetSelection.GetCurrentRuntimeIdentifier(), "assets", "worker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(workerAsset)!);
        File.WriteAllText(workerAsset, "before");
        CanonicalPackageTestBuilder.WriteContentIndex(folder);
        var preparer = new PackageSessionPreparer(ephemeralRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance,
            projectionCache: new PackageProjectionCache(root.GetPath("cache")));
        var before = await Prepare("before");
        var runtime = Guid.NewGuid();
        var states = new[] { State("package") };
        var fingerprint = AppActivationFingerprint.Create(runtime, "package", [before], states, developmentAppContinuation: true);
        using var snapshots = new PackageUiSnapshotStore(new RuntimePackagePaths(root.GetPath("runtime")));
        var key = new SunderPackageTargetKey("app", PackageTargetSelection.GetCurrentRuntimeIdentifier());
        Assert.True(SunderPackageTargetResolver.TryResolveTarget(before.Manifest!, key, out var target));
        var previous = Assert.Single(snapshots.CreateSnapshots([new(before, key, target!)], 1));

        File.WriteAllText(workerAsset, "after");
        var errors = new List<string>();
        Assert.Null(await preparer.PrepareDevPackageAsync(0, folder, root.GetPath("invalid"), errors, default));
        Assert.NotEmpty(errors); // Unindexed edits never qualify for either continuation mode.
        CanonicalPackageTestBuilder.WriteContentIndex(folder);
        var after = await Prepare("after");
        var next = Assert.Single(snapshots.CreateSnapshots([new(after, key, target!)], 2));

        Assert.Equal(fingerprint, AppActivationFingerprint.Create(runtime, "package", [after], states, developmentAppContinuation: true));
        Assert.Equal(previous.ContentHash, next.ContentHash);
        Assert.NotEqual(previous.SnapshotId, next.SnapshotId);
        Assert.NotEqual(before.ContentIdentity, after.ContentIdentity);
        Assert.Equal(before.ContentIdentity, previous.SourceContentIdentity);
        Assert.Equal(after.ContentIdentity, next.SourceContentIdentity);
        Assert.NotEqual(previous.SourceContentIdentity, next.SourceContentIdentity); // A retained App projection is not the complete new source revision.
        Assert.NotEqual(previous.PolicySubject, next.PolicySubject);
        Assert.Equal(after.ContentIdentity, next.PolicySubject!.PackageContentIdentity);
        Assert.NotEqual(AppActivationFingerprint.Create(runtime, "package", [before], states),
            AppActivationFingerprint.Create(runtime, "package", [after], states));
        snapshots.RemoveOlderGenerations(2);
        Assert.Null(snapshots.Acquire(previous.SnapshotId, 1, null));
        using var lease = snapshots.Acquire(next.SnapshotId, 2, null);
        Assert.NotNull(lease);

        async Task<RuntimePackageSource> Prepare(string session)
        {
            var failures = new List<string>();
            var prepared = await preparer.PrepareDevPackageAsync(0, folder, root.GetPath(session), failures, default);
            Assert.Empty(failures);
            Assert.NotNull(prepared);
            await using var runtimeRetention = prepared.ExecutionRuntimeRetention;
            await using var projection = prepared.PackageProjectionLease;
            return prepared.Source;
        }
    }

    [Theory]
    [InlineData("payload/runtime/shared/assets/service.txt", false)]
    [InlineData("payload/runtime/osx-arm64/lib/service.dll", false)]
    [InlineData("payload/app/shared/assets/view.axaml", true)]
    [InlineData("payload/app/osx-arm64/lib/view.dll", true)]
    [InlineData("payload/shared/lib/shared.dll", true)]
    [InlineData("payload/shared/contracts/api.json", true)]
    [InlineData("manifest/sunder-package.json", true)]
    public void AppComparisonExcludesOnlyItsWorkerPayload(string path, bool invalidates)
    {
        var before = QualifiedDevelopmentSource("package", path);
        var changedIndex = before.ContentIndex! with
        {
            Files = before.ContentIndex!.Files!.Select(entry => entry.Path == path ? entry with { Sha256 = new('b', 64) } : entry).ToArray(),
        };
        var after = before with { ContentIdentity = new('b', 64), ContentIndex = changedIndex,
            DevelopmentWorkerContentIdentity = DevelopmentWorkerContentIdentity.Create(changedIndex) };
        Assert.Equal(invalidates, AppComparison(before) != AppComparison(after));
    }

    [Theory]
    [InlineData("unqualified")]
    [InlineData("stale-index-evidence")]
    [InlineData("installed")]
    [InlineData("origin")]
    [InlineData("authority")]
    [InlineData("omission")]
    [InlineData("source-folder")]
    [InlineData("target")]
    [InlineData("dependency")]
    [InlineData("dependency-readiness")]
    [InlineData("runtime-instance")]
    public void AppComparisonDoesNotRelaxQualificationDependenciesOrSecurity(string change)
    {
        var before = QualifiedDevelopmentSource("package", "payload/runtime/shared/assets/service.txt");
        before = before with { Manifest = new()
        {
            Id = "package", Version = "1.0.0",
            DependsOn = [new() { PackageId = "dependency", VersionRange = "*", Presence = "required" }],
        } };
        if (change == "unqualified") before = before with { DevelopmentWorkerContentIdentity = null };
        if (change == "installed") before = before with { Kind = PackageSourceKind.Installed };
        var index = before.ContentIndex! with { Files = before.ContentIndex!.Files!.Select(entry =>
            entry.Path!.StartsWith("payload/runtime/", StringComparison.Ordinal) ? entry with { Sha256 = new('b', 64) } : entry).ToArray() };
        var after = before with { ContentIdentity = new('b', 64), ContentIndex = index,
            DevelopmentWorkerContentIdentity = change == "unqualified" ? null : change == "stale-index-evidence"
                ? before.DevelopmentWorkerContentIdentity : DevelopmentWorkerContentIdentity.Create(index) };
        after = change switch
        {
            "origin" => after with { RegistryPolicyOrigin = "https://another.registry/" },
            "authority" => after with { DevelopmentImports = new(1, "local", "package", "1.0.0", new('a', 64), null, [], []) },
            "omission" => after with { DevelopmentOmittedPackages = ["provider"] },
            "source-folder" => after with { SourceFolder = "/another/package" },
            "target" => after with { SelectedTargetKey = new("runtime", "linux-x64") },
            _ => after,
        };
        var dependency = QualifiedDevelopmentSource("dependency", "payload/runtime/shared/assets/service.txt");
        var changedDependency = change == "dependency" ? dependency with { ContentIdentity = new('c', 64) } : dependency;
        var states = new[] { State("package"), State("dependency") };
        var nextStates = change == "dependency-readiness" ? [states[0], states[1] with { Readiness = PackageReadinessState.Failed }] : states;
        var runtime = Guid.NewGuid();
        Assert.NotEqual(AppActivationFingerprint.Create(runtime, "package", [before, dependency], states, developmentAppContinuation: true),
            AppActivationFingerprint.Create(change == "runtime-instance" ? Guid.NewGuid() : runtime, "package", [after, changedDependency], nextStates,
                developmentAppContinuation: true));
    }

    private static RuntimePackageSource QualifiedDevelopmentSource(string id, string path)
    {
        var entries = new[] { "manifest/sunder-package.json", path }.Distinct(StringComparer.Ordinal)
            .Select(value => new SunderPackageContentIndexEntry(value, new('a', 64), 10)).ToArray();
        var index = new SunderPackageContentIndex(1, entries);
        // Model the Host-owned qualification at this comparison unit boundary;
        // the canonical-shadow test above exercises the real strict preparer.
        return new(id, PackageSourceKind.Dev, "/dev/" + id, ContentIdentity: new('a', 64),
            Manifest: new() { Id = id, Version = "1.0.0" }, ContentIndex: index)
        {
            DevelopmentWorkerContentIdentity = DevelopmentWorkerContentIdentity.Create(index),
        };
    }

    private static string AppComparison(RuntimePackageSource source)
        => AppActivationFingerprint.Create(Guid.Empty, source.PackageId, [source], [State(source.PackageId)], developmentAppContinuation: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedProviderPresenceReadinessAndDevelopmentOmissionInvalidateRetention(bool developmentAppContinuation)
    {
        var runtime = Guid.NewGuid();
        var consumer = (developmentAppContinuation
            ? QualifiedDevelopmentSource("consumer", "payload/runtime/shared/assets/service.txt")
            : Source("consumer")) with { Manifest = new()
        {
            UsesContracts = [new() { Role = "app", ContractId = "sample.api", ContractVersion = "1.0.0", Actions = ["discover", "invoke"] }],
        } };
        var provider = Source("provider") with { Manifest = new()
        {
            Provides = [new() { Role = "runtime", ProviderId = "sample.provider", ContractId = "sample.api", ContractVersion = "1.0.0", Audience = "public" }],
        } };
        var missing = AppActivationFingerprint.Create(runtime, "consumer", [consumer], [State("consumer")], developmentAppContinuation: developmentAppContinuation);
        var present = AppActivationFingerprint.Create(runtime, "consumer", [consumer, provider], [State("consumer"), State("provider")],
            developmentAppContinuation: developmentAppContinuation);
        var failed = AppActivationFingerprint.Create(runtime, "consumer", [consumer, provider],
            [State("consumer"), State("provider") with { Readiness = PackageReadinessState.Failed }], developmentAppContinuation: developmentAppContinuation);
        var omitted = AppActivationFingerprint.Create(runtime, "consumer", [consumer with { DevelopmentOmittedPackages = ["provider"] }, provider],
            [State("consumer"), State("provider")], developmentAppContinuation: developmentAppContinuation);
        Assert.NotEqual(missing, present);
        Assert.NotEqual(present, failed);
        Assert.NotEqual(present, omitted);
    }

    [Theory]
    [InlineData("unrelated", false)]
    [InlineData("runtime", true)]
    [InlineData("source", true)]
    [InlineData("installed-origin", true)]
    [InlineData("dependency", true)]
    [InlineData("dependency-readiness", true)]
    [InlineData("optional-presence", true)]
    public void FingerprintPinsSourceAndEffectiveClosureButNotUnrelatedDevelopmentPackages(string change, bool invalidates)
    {
        var runtime = Guid.NewGuid();
        var builder = Source("builder") with { Manifest = new()
        {
            Id = "builder", Version = "1.0.0", DependsOn = [new() { PackageId = "dependency", VersionRange = "*", Presence = "required" },
                new() { PackageId = "optional", VersionRange = "*", Presence = "optional" }],
        } };
        var dependency = Source("dependency");
        var states = new[] { State("builder"), State("dependency") };
        var before = AppActivationFingerprint.Create(runtime, "builder", [builder, dependency], states);
        var sources = new[] { builder, dependency }.ToList();
        switch (change)
        {
            case "runtime": runtime = Guid.NewGuid(); break;
            case "source": sources[0] = builder with { Kind = PackageSourceKind.Dev }; break;
            case "installed-origin": sources[0] = builder with { RegistryPolicyOrigin = "https://another.registry/" }; break;
            case "dependency": sources[1] = dependency with { ContentIdentity = new('b', 64) }; break;
            case "dependency-readiness": states[1] = states[1] with { Readiness = PackageReadinessState.Failed }; break;
            case "optional-presence": sources.Add(Source("optional")); break;
            default: sources.Add(Source("unrelated") with { Kind = PackageSourceKind.Dev }); break;
        }
        var after = AppActivationFingerprint.Create(runtime, "builder", sources, states);
        Assert.Equal(invalidates, before != after);
    }

    private static RuntimePackageSource Source(string id) => new(id, PackageSourceKind.Installed, "/installed/" + id,
        ContentIdentity: new string('a', 64));
    private static SessionPackageDescriptor State(string id) => new(id, id, "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime,
        null, true, PackageReadinessState.Ready, [], null, null, null, 0);
}
