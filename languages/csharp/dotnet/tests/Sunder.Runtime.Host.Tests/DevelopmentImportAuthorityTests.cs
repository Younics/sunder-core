using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class DevelopmentImportAuthorityTests
{
    [Fact]
    public async Task DevelopmentPrerequisitePreviewRequiresExplicitImportEvidenceWithoutRegistryFallback()
    {
        using var fixture = new Fixture(optional: true) { Offline = true };
        // This App-only fixture has no runtime requirements. No installer, Registry
        // resolver, upload or package-store dependency should be consulted by preview.
        var prerequisites = new PackageRuntimePrerequisiteService(null!, null!, null!, null!, null!, null!, null!, fixture.Store);
        var request = new RuntimePackageRuntimePrerequisiteRequest { DevelopmentFolder = fixture.Folder };
        var error = await Assert.ThrowsAsync<RuntimePackageValidationException>(() => prerequisites.PreviewAsync(request, default));
        Assert.Contains("prepare-imports", error.Message);
        Assert.Contains("--candidate/--platform-catalog", error.Message);
        Assert.Contains("--registry", error.Message);
        Assert.Equal(0, fixture.Requests);
        await fixture.Preparation.PrepareAsync(await fixture.LocalInputs(), default);
        var plan = await prerequisites.PreviewAsync(request, default);
        Assert.True(plan.Success);
        Assert.True(plan.Satisfied);
        Assert.Equal("example.local", plan.DevelopmentPackageId);
        Assert.Empty(plan.Requirements);
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedLocalHandoffLoadsFirstLaunchAndRebuiltManifestWithoutManualPreparation(bool platform)
    {
        using var fixture = new Fixture(optional: true, platform: platform) { Offline = true };
        var prepared = await fixture.Preparation.PrepareAsync(await fixture.LocalInputs(), default);
        var retainedPath = fixture.Store.PathFor(prepared.ManifestSha256);
        var handoff = fixture.Folder + SunderPackageFormat.DevelopmentImportsSuffix;
        File.Copy(retainedPath, handoff);
        File.Delete(retainedPath);
        var host = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance,
            new(fixture.Policy.Paths), new(fixture.Policy.Paths), developmentImports: fixture.Store);
        try
        {
            Assert.True((await host.LoadDevPackageFromRuntimeInputAsync(fixture.Folder, watch: false)).Success);
            var active = Assert.Single(host.GetActiveRuntimePackageSources());
            Assert.True(active.DevelopmentImports!.IsLocal);
            Assert.Null(active.InstalledPolicy);
            Assert.Null(active.RegistryPolicyContentIdentity);
            Assert.Equal(File.ReadAllBytes(handoff), File.ReadAllBytes(retainedPath));
        }
        finally { await host.ShutdownAsync(); }

        var manifest = JsonNode.Parse(File.ReadAllBytes(fixture.ManifestPath))!;
        manifest["name"] = "Rebuilt local package";
        File.WriteAllText(fixture.ManifestPath, manifest.ToJsonString());
        CanonicalPackageTestBuilder.WriteContentIndex(fixture.Folder);
        var source = fixture.Source();
        var hash = RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex!);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ResolveAsync(source, default));
        Assert.False(File.Exists(fixture.Store.PathFor(hash)));
        var next = JsonNode.Parse(File.ReadAllBytes(handoff))!;
        next["manifestSha256"] = hash;
        File.WriteAllText(handoff, next.ToJsonString());
        Assert.Equal(hash, (await fixture.Store.ResolveAsync(source, default)).ManifestSha256);
        Assert.True(File.Exists(retainedPath));
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData("registry")]
    [InlineData("inventory")]
    [InlineData("hash")]
    [InlineData("catalog")]
    [InlineData("duplicate")]
    [InlineData("symlink")]
    [InlineData("disabled")]
    public async Task GeneratedHandoffRejectsUnverifiedOrNonlocalEvidenceBeforeRetention(string failure)
    {
        using var fixture = new Fixture(optional: true, platform: true) { Offline = true };
        var prepared = await fixture.Preparation.PrepareAsync(await fixture.LocalInputs(), default);
        var retainedPath = fixture.Store.PathFor(prepared.ManifestSha256);
        var valid = File.ReadAllBytes(retainedPath);
        File.Delete(retainedPath);
        var handoff = fixture.Folder + SunderPackageFormat.DevelopmentImportsSuffix;
        var value = JsonNode.Parse(valid)!;
        switch (failure)
        {
            case "registry": value["sourceKind"] = "registry"; value["registryOrigin"] = ActivePolicyTestContext.Origin; break;
            case "inventory": value["inventories"] = new JsonArray(); break;
            case "hash": value["manifestSha256"] = new string('0', 64); break;
            case "catalog": value["catalogs"] = new JsonArray(); break;
        }
        File.WriteAllText(handoff, failure switch
        {
            "duplicate" => value.ToJsonString().Insert(1, "\"sourceKind\":\"local\","),
            "disabled" => "null\n",
            _ => value.ToJsonString(),
        });
        if (failure == "symlink")
        {
            var outside = fixture.Temp.GetPath("outside.json");
            File.WriteAllBytes(outside, valid);
            File.Delete(handoff);
            File.CreateSymbolicLink(handoff, outside);
        }
        var exception = await Record.ExceptionAsync(() => fixture.Store.ResolveAsync(fixture.Source(), default));
        Assert.True(exception is InvalidDataException or JsonException, exception?.ToString() ?? "Invalid handoff was accepted.");
        Assert.False(File.Exists(retainedPath));
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalCandidatesPrepareAndLoadWithoutRegistryOrPublication(bool platform)
    {
        using var fixture = new Fixture(optional: true, platform: platform) { Offline = true };
        var request = await fixture.LocalInputs();
        var before = File.ReadAllBytes(fixture.ManifestPath);
        var result = await fixture.Preparation.PrepareAsync(request, default);
        var evidence = await fixture.Store.ResolveAsync(fixture.Source(), default);
        Assert.True(evidence.IsLocal); Assert.Null(evidence.RegistryOrigin);
        Assert.Equal(platform ? 1 : 0, result.PlatformCatalogs);
        Assert.Equal(before, File.ReadAllBytes(fixture.ManifestPath));
        Assert.Empty(await new InstalledPackageStore(fixture.Policy.Paths).ListAsync());
        fixture.Policy.Clock.Advance(TimeSpan.FromHours(25));
        var source = fixture.Source() with { DevelopmentImports = await fixture.Store.ResolveAsync(fixture.Source(), default) };
        var admission = new PackagePolicyAdmission(fixture.Policy.Cache);
        admission.Require(source); Assert.Empty(admission.Subjects(source));

        var host = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance,
            new(fixture.Policy.Paths), new(fixture.Policy.Paths), developmentImports: fixture.Store);
        try
        {
            Assert.True((await host.LoadDevPackageFromRuntimeInputAsync(fixture.Folder, watch: false)).Success);
            var active = Assert.Single(host.GetActiveRuntimePackageSources());
            Assert.True(active.DevelopmentImports!.IsLocal);
            Assert.Null(active.RegistryPolicyContentIdentity); Assert.Null(active.InstalledPolicy);
        }
        finally { await host.ShutdownAsync(); }
        Assert.Equal(0, fixture.Requests);

        var path = fixture.Store.PathFor(result.ManifestSha256); var retained = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Preparation.PrepareAsync(new(fixture.Folder), default));
        Assert.Equal(retained, File.ReadAllBytes(path));
        var missingKind = JsonNode.Parse(retained)!.AsObject(); missingKind.Remove("sourceKind");
        File.WriteAllText(path, missingKind.ToJsonString());
        await Assert.ThrowsAsync<JsonException>(() => fixture.Store.ResolveAsync(fixture.Source(), default));
    }

    [Fact]
    public async Task UnpublishedPreparationRetainsOnlyImportEvidenceAndReplaysOfflineAcrossBodyChanges()
    {
        using var fixture = new Fixture(); var source = fixture.Source();
        var before = File.ReadAllBytes(fixture.ManifestPath);
        var result = await fixture.Prepare();
        Assert.Equal("example.local", result.PackageId); Assert.Equal(1, result.PackageArtifacts); Assert.Equal(0, result.PlatformCatalogs);
        var path = fixture.Store.PathFor(result.ManifestSha256); var bytes = File.ReadAllBytes(path); var time = File.GetLastWriteTimeUtc(path);
        await fixture.Prepare(); Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        Assert.Equal(before, File.ReadAllBytes(fixture.ManifestPath)); Assert.Empty(await new InstalledPackageStore(fixture.Policy.Paths).ListAsync());
        fixture.Offline = true;
        var reloaded = new DevelopmentImportAuthorityStore(fixture.Policy.Paths, fixture.Policy.Cache);
        var retained = await reloaded.ResolveAsync(source, default);
        Assert.Equal("example.authority", Assert.Single(retained.Inventories).Artifact.Inventory.PackageId);
        var assets = Path.Combine(fixture.Folder, "payload/shared/assets"); Directory.CreateDirectory(assets); File.WriteAllText(Path.Combine(assets, "iteration.txt"), "new local implementation data");
        CanonicalPackageTestBuilder.WriteContentIndex(fixture.Folder);
        var preparer = new PackageSessionPreparer(developmentImports: reloaded); var errors = new List<string>();
        var prepared = await preparer.PrepareDevPackageAsync(0, fixture.Folder, fixture.Temp.CreateDirectory("session"), errors, default);
        Assert.NotNull(prepared); Assert.Empty(errors);
        Assert.Null(prepared.Source.Compatibility); Assert.Null(prepared.Source.RegistryPolicyContentIdentity); Assert.Null(prepared.Source.InstalledPolicy);
        Assert.Equal(result.ManifestSha256, prepared.Source.DevelopmentImports!.ManifestSha256);
        if (prepared.PackageProjectionLease is not null) await prepared.PackageProjectionLease.DisposeAsync();
        Assert.Equal(2, fixture.Requests);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("origin")]
    [InlineData("redirect")]
    [InlineData("media")]
    [InlineData("source-edit")]
    [InlineData("oversize")]
    [InlineData("duplicate-json")]
    public async Task FailedPreparationPreservesPriorEvidenceAndDoesNotPublishPartialAuthority(string failure)
    {
        using var fixture = new Fixture(); var result = await fixture.Prepare(); var path = fixture.Store.PathFor(result.ManifestSha256); var prior = File.ReadAllBytes(path);
        fixture.Failure = failure;
        await Assert.ThrowsAnyAsync<Exception>(fixture.Prepare);
        Assert.Equal(prior, File.ReadAllBytes(path));
        Assert.Empty(await new InstalledPackageStore(fixture.Policy.Paths).ListAsync());
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("catalog-metadata")]
    [InlineData("catalog-etag")]
    [InlineData("catalog-length")]
    [InlineData("catalog-missing")]
    public async Task InvalidOriginalCatalogCannotReplaceRetainedDevelopmentEvidence(string failure)
    {
        using var fixture = new Fixture(platform: true); var result = await fixture.Prepare();
        var path = fixture.Store.PathFor(result.ManifestSha256); var prior = File.ReadAllBytes(path);
        fixture.Failure = failure;
        await Assert.ThrowsAnyAsync<Exception>(fixture.Prepare);
        Assert.Equal(prior, File.ReadAllBytes(path));
        Assert.Empty(await new InstalledPackageStore(fixture.Policy.Paths).ListAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreparationBoundsHeadersAndBodyAndPreservesCallerCancellation(bool body, bool cancel)
    {
        using var fixture = new Fixture(); var result = await fixture.Prepare();
        var path = fixture.Store.PathFor(result.ManifestSha256); var prior = File.ReadAllBytes(path);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SendOverride = async (request, token) =>
        {
            if (!body) { blocked.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(new BlockedReadStream(blocked))
                { Headers = { ContentType = new("application/json") } } };
        };
        using var cancellation = new CancellationTokenSource();
        var operation = fixture.Preparation.PrepareAsync(new(fixture.Folder, ActivePolicyTestContext.Origin), cancellation.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancel) cancellation.Cancel();
        else fixture.Policy.Time.Advance(TimeSpan.FromSeconds(RuntimePolicyTiming.RequestDeadlineSeconds));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        else Assert.Contains("Development Registry evidence exceeded", (await Assert.ThrowsAsync<TimeoutException>(
            () => operation.WaitAsync(TimeSpan.FromSeconds(10)))).Message);
        Assert.Equal(prior, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task RevokedPlatformCannotPrepareFreshUnpublishedCode()
    {
        using var fixture = new Fixture(platform: true);
        fixture.Policy.Response = request => ActivePolicyTestContext.Allowed(request) with
        {
            PlatformCatalogs = request.PlatformCatalogs.Select(value => new RegistryPlatformPolicyDecision(value, "Revoked", "Denied",
                PackagePolicyErrorCodes.ContractAuthorityRevoked, ActivePolicyTestContext.ObservedAt)).ToArray(),
        };
        var error = await Assert.ThrowsAsync<PackagePolicyDeniedException>(fixture.Prepare);
        Assert.Equal(PackagePolicyErrorCodes.ContractAuthorityRevoked, error.Code);
        Assert.False(File.Exists(fixture.Store.PathFor(RuntimeRpcPackageMetadata.GetManifestSha256(fixture.Source().ContentIndex!))));
    }

    [Fact]
    public async Task MissingChangedOrLinkedEvidenceCannotBeUsedAsDevelopmentAuthority()
    {
        using var fixture = new Fixture(); var source = fixture.Source();
        Assert.Contains(PackagePolicyErrorCodes.PolicyRequired, (await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ResolveAsync(source, default))).Message);
        var result = await fixture.Prepare(); var path = fixture.Store.PathFor(result.ManifestSha256); var bytes = File.ReadAllBytes(path);
        File.WriteAllText(path, "{}"); await Assert.ThrowsAnyAsync<Exception>(() => fixture.Store.ResolveAsync(source, default));
        File.WriteAllBytes(path, bytes);
        var changed = JsonNode.Parse(File.ReadAllBytes(fixture.ManifestPath))!; changed["name"] = "Changed source manifest"; File.WriteAllText(fixture.ManifestPath, changed.ToJsonString());
        CanonicalPackageTestBuilder.WriteContentIndex(fixture.Folder);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ResolveAsync(fixture.Source(), default));
        var outside = fixture.Temp.GetPath("outside.json"); File.WriteAllBytes(outside, bytes); File.Delete(path); File.CreateSymbolicLink(path, outside);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Store.ResolveAsync(source, default)); Assert.Equal(bytes, File.ReadAllBytes(outside));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PresenceAndRequiredReadinessRemainIndependentForUnpublishedConsumers(bool optional, bool required)
    {
        using var fixture = new Fixture(optional: optional, required: required); await fixture.Prepare();
        var source = fixture.Source(); source = source with { DevelopmentImports = await fixture.Store.ResolveAsync(source, default) };
        var resolved = Assert.Single(PackageCompatibilityAuthority.ResolveDevelopmentGraph([Prepared(source)]));
        if (!optional)
        {
            Assert.Throws<InvalidDataException>(() => PackageCompatibilityAuthority.ValidateGraph([resolved.Source]));
            Assert.Single(resolved.Dependencies);
        }
        else
        {
            PackageCompatibilityAuthority.ValidateGraph([resolved.Source]); Assert.Empty(resolved.Dependencies);
            Assert.Equal(["example.authority"], resolved.Source.DevelopmentOmittedPackages);
            Assert.False(PackageCompatibilityAuthority.DevelopmentUseEnabled(resolved.Source, "app", "example.api", "1.0.0"));
        }
        Assert.Equal(required, Assert.Single(resolved.Source.Manifest!.UsesContracts!)!.Required);
        var actual = fixture.Authority("1.0.9");
        var included = PackageCompatibilityAuthority.ResolveDevelopmentGraph([Prepared(source), Prepared(actual)]);
        PackageCompatibilityAuthority.ValidateGraph(included.Select(value => value.Source).ToArray());
        Assert.Empty(included[0].Source.DevelopmentOmittedPackages);
        Assert.True(PackageCompatibilityAuthority.DevelopmentUseEnabled(included[0].Source, "app", "example.api", "1.0.0"));
        Assert.NotEqual(fixture.Inventory.Inventory.PackageContentIdentity, actual.ContentIdentity);
    }

    [Fact]
    public async Task MetadataOnlyPlatformEvidenceNeedsOriginalSignatureAndACurrentStandalonePolicyLease()
    {
        using var fixture = new Fixture(platform: true); var result = await fixture.Prepare();
        Assert.Equal(1, result.PlatformCatalogs); var source = fixture.Source();
        var evidence = await fixture.Store.ResolveAsync(source, default);
        source = source with { DevelopmentImports = evidence };
        Assert.Single(evidence.Catalogs);
        var admission = new PackagePolicyAdmission(fixture.Policy.Cache); admission.Require(source);
        var subjects = Assert.Single(admission.Subjects(source)); Assert.Empty(subjects.Packages); Assert.Single(subjects.Platforms);
        fixture.Offline = true; await fixture.Store.ResolveAsync(source, default);
        fixture.Policy.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(PackagePolicyErrorCodes.PolicyExpired, Assert.Throws<PackagePolicyDeniedException>(() => admission.Require(source)).Code);
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Store.ResolveAsync(source, default));
    }

    [Fact]
    public async Task PlatformRevocationDeniesPreparedDevelopmentWithoutInventingAConsumerPublication()
    {
        using var fixture = new Fixture(platform: true); await fixture.Prepare(); var source = fixture.Source();
        source = source with { DevelopmentImports = await fixture.Store.ResolveAsync(source, default) };
        fixture.Policy.Response = request => ActivePolicyTestContext.Allowed(request, 2) with
        {
            PlatformCatalogs = request.PlatformCatalogs.Select(value => new RegistryPlatformPolicyDecision(value, "Revoked", "Denied",
                PackagePolicyErrorCodes.ContractAuthorityRevoked, ActivePolicyTestContext.ObservedAt)).ToArray(),
        };
        await fixture.Policy.RefreshAsync(packages: [], platforms: DevelopmentImportAuthorityStore.Platforms(source.Manifest!));
        Assert.Equal(PackagePolicyErrorCodes.ContractAuthorityRevoked, Assert.Throws<PackagePolicyDeniedException>(() => new PackagePolicyAdmission(fixture.Policy.Cache).Require(source)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentAutomaticGrantsRespectOmissionAndPermitIndependentExactProviders(bool omitted)
    {
        using var fixture = new Fixture(optional: true); await fixture.Prepare(); var source = fixture.Source();
        source = source with { DevelopmentImports = await fixture.Store.ResolveAsync(source, default), DevelopmentOmittedPackages = omitted ? ["example.authority"] : [] };
        var caller = new RuntimeRpcAppCallerSession(source, source.PackageId, source.Manifest!.Version!, RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex!),
            source.Manifest.UsesContracts!.Select(value => value!).ToArray(), new Dictionary<string, SunderRpcContractDescriptor>
            { [PackageSessionPreparer.ContractKey(fixture.Contract.ContractId, fixture.Contract.Version)] = fixture.Contract }, 1, Guid.NewGuid());
        var snapshot = new SunderRpcProviderSnapshot("example.independent", "1.0.0", "independent.provider", fixture.Contract.ContractId, fixture.Contract.Version,
            fixture.Contract.Sha256, fixture.Contract.AbiSha256, Guid.NewGuid(), 1, 1, new("endpoint"), 1, SunderRpcProviderState.Active);
        var provider = new RuntimeRpcProviderRegistration(snapshot.ProviderId, snapshot.ContractId, snapshot.ContractVersion, snapshot.DescriptorSha256, fixture.Contract, null!,
            new() { Role = "runtime", ProviderId = snapshot.ProviderId, ContractId = snapshot.ContractId, ContractVersion = snapshot.ContractVersion,
                DescriptorSha256 = snapshot.DescriptorSha256, AbiSha256 = snapshot.AbiSha256, Audience = "public" });
        var decision = RuntimeRpcCallAcquisitionPolicy.EvaluateCallerAction(caller, snapshot, provider, "invoke", requirePermission: true);
        Assert.Equal(!omitted, decision.Succeeded);
        Assert.Equal(omitted ? 0 : 1, caller.RpcContractUses.Count);
        if (!omitted) Assert.Equal(RuntimeRpcCallPermissionAcquisitionKind.CallerLifetime, decision.Permission!.Kind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreparedDevelopmentSessionPreservesAtomicPresenceAndReadinessGates(bool optional, bool required)
    {
        using var fixture = new Fixture(optional: optional, required: required); await fixture.Prepare(); fixture.Offline = true;
        var host = new RuntimePackageSessionTestHost(NullLogger<RuntimePackageSessionTestHost>.Instance,
            new(fixture.Policy.Paths), new(fixture.Policy.Paths), developmentImports: fixture.Store);
        try
        {
            if (!optional || required)
            {
                if (!optional)
                {
                    var rejection = await Assert.ThrowsAsync<RuntimePackageValidationException>(() =>
                        host.LoadDevPackageFromRuntimeInputAsync(fixture.Folder, watch: false));
                    Assert.Contains("compatibility graph", rejection.Message, StringComparison.OrdinalIgnoreCase);
                }
                else await Assert.ThrowsAsync<RuntimeRequiredProviderException>(() => host.LoadDevPackageFromRuntimeInputAsync(fixture.Folder, watch: false));
                Assert.Empty(host.GetActiveRuntimePackageSources()); return;
            }
            var result = await host.LoadDevPackageFromRuntimeInputAsync(fixture.Folder, watch: false);
            Assert.Equal(optional && !required, result.Success);
            if (result.Success)
            {
                var active = Assert.Single(host.GetActiveRuntimePackageSources());
                Assert.NotNull(active.DevelopmentImports); Assert.Null(active.Compatibility); Assert.Empty(active.PackageDependencies);
                Assert.False(PackageCompatibilityAuthority.DevelopmentUseEnabled(active, "app", "example.api", "1.0.0"));
                Assert.Equal(1, fixture.Requests);
                var priorGeneration = host.SessionGeneration;
                var metadata = JsonNode.Parse(File.ReadAllBytes(fixture.ManifestPath))!; metadata["name"] = "Manifest needs new evidence";
                File.WriteAllText(fixture.ManifestPath, metadata.ToJsonString()); CanonicalPackageTestBuilder.WriteContentIndex(fixture.Folder);
                Assert.False((await host.LoadDevPackageFromRuntimeInputAsync(fixture.Folder, watch: false)).Success);
                Assert.Equal(priorGeneration, host.SessionGeneration); Assert.Same(active, Assert.Single(host.GetActiveRuntimePackageSources()));
            }
            else Assert.Empty(host.GetActiveRuntimePackageSources());
        }
        finally { await host.ShutdownAsync(); }
    }

    private static PreparedRuntimePackage Prepared(RuntimePackageSource source) => new(source.SourceFolder, source, source.SourceFolder,
        source.PackageId, source.Manifest!.Version!, PackageHostRoles.App, new(source.PackageId, source.PackageId, source.Manifest.Version!, PackageHostRoles.App, null),
        null, null, null, (source.Manifest.DependsOn ?? []).Select(value => new PackageDependencyDescriptor(value.PackageId!, value.VersionRange!)).ToArray());

    internal sealed class Fixture : IDisposable
    {
        internal TemporaryDirectoryScope Temp { get; } = TemporaryDirectoryScope.Create("development-imports");
        internal ActivePolicyTestContext Policy { get; } = new();
        private readonly RegistryPackagePolicySynchronizer _synchronizer;
        private readonly HttpClient _http;
        internal readonly SunderRpcContractDescriptor Contract = SunderRpcContractDescriptor.Parse("""
            {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"example.api","version":"1.0.0","errors":{},
             "services":{"service":{"methods":{"call":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Request"}}}}},
             "$defs":{"Request":{"type":"object","properties":{},"required":[],"additionalProperties":false}}}
            """u8);
        internal RegistryPackagePublicApiInventoryResponseV1 Inventory { get; }
        internal string Folder { get; }
        internal string ManifestPath => Path.Combine(Folder, SunderPackageFormat.ManifestPath);
        internal DevelopmentImportAuthorityStore Store { get; }
        internal DevelopmentImportAuthorityPreparation Preparation { get; }
        internal bool Offline { get; set; }
        internal string? Failure { get; set; }
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendOverride { get; set; }
        internal int Requests { get; private set; }
        private readonly byte[]? _envelope;
        private readonly SunderVerifiedPlatformContractCatalog? _catalog;
        internal Fixture(bool optional = false, bool required = false, bool platform = false)
        {
            RuntimeLocalState.EnsureInitialized(Policy.Paths.RootPath);
            if (platform)
            {
                var fixtures = Path.Combine(Root(), "specs/rpc/v1/authoring/conformance/valid");
                using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "platform-catalog.json")));
                _envelope = SunderRpcAuthoringJson.WriteCompact(document.RootElement, 67_108_864); _catalog = SunderPlatformContractCatalog.Verify(_envelope);
            }
            var content = new string('e', 64);
            var contracts = new List<RegistryPackagePublicApiContractV1> { new(Contract.ContractId, Contract.Version, Contract.GetCanonicalUtf8().Length,
                Contract.Sha256, Contract.AbiSha256, "owned", "package", "example.authority", "1.0.0", content) };
            var constraints = new List<SunderPackageAuthorityConstraintManifest> { new() { AuthorityKind = "package", AuthorityId = "example.authority", SelectedAuthorityRelease = "1.0.0", SelectedAuthorityContentIdentity = content } };
            if (_catalog is not null)
            {
                var descriptor = _catalog.Contracts.Single();
                contracts.Add(new(descriptor.ContractId, descriptor.Version, descriptor.GetCanonicalUtf8().Length, descriptor.Sha256, descriptor.AbiSha256, "imported", "platform", "sunder.core",
                    _catalog.ReleaseVersion, _catalog.ContentIdentity));
                constraints.Add(new() { AuthorityKind = "platform", AuthorityId = "sunder.core", SelectedAuthorityRelease = _catalog.ReleaseVersion,
                    SelectedAuthorityContentIdentity = _catalog.ContentIdentity });
            }
            var inventory = new RegistryPackagePublicApiInventoryV1(1, "example.authority", "1.0.0", content, contracts.Count, contracts);
            Inventory = new(inventory, SunderPackagePublicApiInventory.Parse(JsonSerializer.SerializeToUtf8Bytes(inventory,
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).PublicInventorySha256);
            Folder = Temp.GetPath("local");
            CanonicalPackageTestBuilder.WriteExplodedPackage(Folder, "example.local", "1.0.0", typeof(DevelopmentImportAuthorityTests).Assembly.Location,
                roles: ["app"], dependencies: [new("example.authority", ">=1.0.0 <2.0.0")], contractBundles: [Bundle(imported: true)],
                usesContracts: [new() { Role = "app", ContractId = Contract.ContractId, ContractVersion = Contract.Version, AbiSha256 = Contract.AbiSha256,
                    Required = required, Actions = ["discover", "invoke"] }]);
            var manifest = JsonNode.Parse(File.ReadAllBytes(ManifestPath))!; var presence = optional ? "optional" : "required";
            manifest["dependsOn"]![0]!["presence"] = presence;
            var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            manifest["packageArtifactImports"] = JsonSerializer.SerializeToNode(new[] { new SunderPackageArtifactImportManifest { PackageId = "example.authority",
                RequestedVersionRange = ">=1.0.0 <2.0.0", IncludePrerelease = false, SelectedPackageVersion = "1.0.0", SelectedPackageContentIdentity = content,
                PublicContractCount = contracts.Count, PublicInventorySha256 = Inventory.PublicInventorySha256, AuthorityConstraints = constraints } }, options);
            manifest["packageApiImports"] = JsonSerializer.SerializeToNode(new[] { new SunderPackageApiImportManifest { PackageId = "example.authority", Presence = presence,
                BundleRefs = [new() { ContractId = Contract.ContractId, ContractVersion = Contract.Version }],
                UseRefs = [new() { Role = "app", ContractId = Contract.ContractId, ContractVersion = Contract.Version }] } }, options);
            File.WriteAllText(ManifestPath, manifest.ToJsonString()); Directory.CreateDirectory(Path.Combine(Folder, "payload/shared/contracts"));
            File.WriteAllBytes(Path.Combine(Folder, "payload/shared/contracts/api.json"), Contract.GetCanonicalUtf8()); CanonicalPackageTestBuilder.WriteContentIndex(Folder);
            _http = new(new Handler((request, token) => SendOverride?.Invoke(request, token) ?? Task.FromResult(Send(request)))) { Timeout = Timeout.InfiniteTimeSpan };
            _synchronizer = new(Policy.Client, Policy.Cache);
            Store = new(Policy.Paths, Policy.Cache); Preparation = new(new(new Factory(_http), new(), Policy.Time), Store, _synchronizer, Policy.Cache, Policy.Time);
        }
        internal Task<RuntimeDevPackageImportsPrepared> Prepare() => Preparation.PrepareAsync(new(Folder, ActivePolicyTestContext.Origin), default);
        internal async Task<RuntimePrepareDevPackageImportsRequest> LocalInputs()
        {
            var owner = Temp.GetPath("local-authority");
            var bundles = new List<SunderPackageContractBundleManifest?> { Bundle(imported: false) };
            var descriptor = _catalog?.Contracts.Single();
            if (descriptor is not null)
                bundles.Add(new() { Source = "imported", AuthorityKind = "platform", AuthorityId = "sunder.core", SelectedAuthorityRelease = _catalog!.ReleaseVersion,
                    SelectedAuthorityContentIdentity = _catalog.ContentIdentity, ContractId = descriptor.ContractId, Version = descriptor.Version,
                    DescriptorPath = "contracts/stack.json", DescriptorSha256 = descriptor.Sha256, AbiSha256 = descriptor.AbiSha256, Exposure = "public" });
            CanonicalPackageTestBuilder.WriteExplodedPackage(owner, "example.authority", "1.0.0", typeof(DevelopmentImportAuthorityTests).Assembly.Location,
                roles: descriptor is null ? ["app"] : ["runtime"], contractBundles: bundles);
            var manifestPath = Path.Combine(owner, SunderPackageFormat.ManifestPath);
            var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!;
            Directory.CreateDirectory(Path.Combine(owner, "payload/shared/contracts"));
            File.WriteAllBytes(Path.Combine(owner, "payload/shared/contracts/api.json"), Contract.GetCanonicalUtf8());
            string? catalogPath = null;
            if (descriptor is not null)
            {
                const string provider = "example.authority.stack";
                manifest["provides"] = JsonSerializer.SerializeToNode(new[] { new { role = "runtime", providerId = provider,
                    contractId = descriptor.ContractId, contractVersion = descriptor.Version, descriptorSha256 = descriptor.Sha256, abiSha256 = descriptor.AbiSha256, audience = "public" } });
                manifest["platformImports"] = JsonSerializer.SerializeToNode(new[] { new SunderPackagePlatformImportManifest
                {
                    Key = "stackContributor", AuthorityKind = "platform", AuthorityId = "sunder.core", AuthorityReleaseRange = ">=1.1.0 <1.2.0", IncludePrerelease = false,
                    SelectedAuthorityRelease = _catalog!.ReleaseVersion, SelectedAuthorityContentIdentity = _catalog.ContentIdentity,
                    ContractId = descriptor.ContractId, ContractVersion = descriptor.Version,
                    BundleRef = new() { ContractId = descriptor.ContractId, ContractVersion = descriptor.Version },
                    ProviderRefs = [new() { Role = "runtime", ProviderId = provider, ContractId = descriptor.ContractId, ContractVersion = descriptor.Version }],
                } });
                File.WriteAllBytes(Path.Combine(owner, "payload/shared/contracts/stack.json"), descriptor.GetCanonicalUtf8());
                catalogPath = Temp.GetPath("local-core-catalog.json"); File.WriteAllBytes(catalogPath, _envelope!);
            }
            File.WriteAllText(manifestPath, manifest.ToJsonString()); CanonicalPackageTestBuilder.WriteContentIndex(owner);
            var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(owner);
            Assert.True(validation.Success, string.Join(" | ", validation.Errors));
            var inventory = SunderPackagePublicApiInspection.ReadValidated(owner, validation.Manifest!).Inventory;
            var content = inventory.Payload.GetProperty("packageContentIdentity").GetString()!;
            var local = JsonNode.Parse(File.ReadAllBytes(ManifestPath))!;
            local["contractBundles"]![0]!["selectedAuthorityContentIdentity"] = content;
            var imported = local["packageArtifactImports"]![0]!;
            imported["selectedPackageContentIdentity"] = content; imported["publicInventorySha256"] = inventory.PublicInventorySha256;
            foreach (var authority in imported["authorityConstraints"]!.AsArray())
                if (authority!["authorityKind"]!.GetValue<string>() == "package") authority["selectedAuthorityContentIdentity"] = content;
            File.WriteAllText(ManifestPath, local.ToJsonString()); CanonicalPackageTestBuilder.WriteContentIndex(Folder);
            var archive = Temp.GetPath("local-authority.sunderpkg"); await SunderArchive.WriteDeterministicAsync(owner, archive);
            return new(Folder) { CandidateArchives = [archive], PlatformCatalogPath = catalogPath };
        }
        internal RuntimePackageSource Source() => CanonicalPackageTestBuilder.CreateTargetSource(Folder, PackageSourceKind.Dev).Source;
        internal RuntimePackageSource Authority(string version) => new("example.authority", PackageSourceKind.Installed, "/fixture/authority", ContentIdentity: new string('a', 64),
            Manifest: new() { Id = "example.authority", Version = version, DependsOn = [], ContractBundles = [Bundle(false)], UsesContracts = [], Provides = [], Targets = [] });
        private SunderPackageContractBundleManifest Bundle(bool imported) => new() { Source = imported ? "imported" : "owned", AuthorityKind = "package", AuthorityId = "example.authority",
            SelectedAuthorityRelease = imported ? "1.0.0" : null, SelectedAuthorityContentIdentity = imported ? new string('e', 64) : null,
            ContractId = Contract.ContractId, Version = Contract.Version, DescriptorPath = "contracts/api.json", DescriptorSha256 = Contract.Sha256, AbiSha256 = Contract.AbiSha256, Exposure = "public" };
        private HttpResponseMessage Send(HttpRequestMessage request)
        {
            if (Offline) throw new InvalidOperationException("Offline load must not use HTTP."); Requests++;
            object body = Inventory; byte[]? bytes = null;
            if (request.RequestUri!.AbsolutePath.EndsWith("/catalog", StringComparison.Ordinal)) bytes = _envelope!;
            else if (request.RequestUri.AbsolutePath.Contains("/platform-contracts/", StringComparison.Ordinal)) body = new RegistryPlatformContractReleaseV1("sunder.core", _catalog!.ReleaseVersion,
                _catalog.ContentIdentity, _envelope!.Length, request.RequestUri.AbsoluteUri + "/catalog",
                "Supported", ActivePolicyTestContext.ObservedAt, _catalog.Contracts.Select(value => new RegistryPlatformContractDescriptorV1(value.ContractId, value.Version,
                    value.GetCanonicalUtf8().Length, value.Sha256, value.AbiSha256)).ToArray());
            if (Failure == "hash") body = Inventory with { PublicInventorySha256 = new string('0', 64) };
            if (Failure == "source-edit") File.AppendAllText(ManifestPath, " ");
            if (Failure == "catalog-metadata" && body is RegistryPlatformContractReleaseV1 metadata) body = metadata with { Contracts = [] };
            if (Failure == "signature" && bytes is not null)
            {
                var changed = JsonNode.Parse(bytes)!; changed["signature"] = Convert.ToBase64String(new byte[64]);
                bytes = SunderRpcAuthoringJson.WriteCompact(JsonSerializer.SerializeToElement(changed), 67_108_864);
            }
            if (Failure == "duplicate-json") bytes = "{\"inventory\":{},\"inventory\":{},\"publicInventorySha256\":\"\"}"u8.ToArray();
            var response = new HttpResponseMessage(Failure == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK)
            { RequestMessage = Failure == "origin" ? new(HttpMethod.Get, "https://other.example/") : request,
                Content = new ByteArrayContent(bytes ?? JsonSerializer.SerializeToUtf8Bytes(body, DevelopmentImportAuthorityStore.Json)) };
            response.Content.Headers.ContentType = new(Failure == "media" ? "text/plain" : "application/json");
            if (bytes is not null && _catalog is not null) response.Headers.ETag = new EntityTagHeaderValue("\"" + (Failure == "catalog-etag" ? new string('0', 64) : _catalog.ContentIdentity) + "\"");
            if (Failure == "catalog-missing" && bytes is not null) response.StatusCode = HttpStatusCode.NotFound;
            if (Failure == "catalog-length" && bytes is not null) response.Content.Headers.ContentLength = bytes.Length + 1;
            if (Failure == "oversize") response.Content.Headers.ContentLength = 1_048_577;
            return response;
        }
        public void Dispose() { _http.Dispose(); _synchronizer.Dispose(); Policy.Dispose(); Temp.Dispose(); }
        private static string Root() { for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) return directory.FullName; throw new InvalidOperationException(); }
        private sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    }

    private sealed class BlockedReadStream(TaskCompletionSource blocked) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            blocked.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
