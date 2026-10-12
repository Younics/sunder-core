using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sunder.Core.TestSupport;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class PolicyContractTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
    };
    private static readonly DateTimeOffset Observed = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static string Hash(char value) => new(value, 64);
    private static RegistryPackageVersionIdentity Package => new("example.agent", "2.0.0", Hash('a'));
    private static RegistryPlatformCatalogIdentity Platform => new("sunder.core", "1.1.0", Hash('b'));
    private static RegistryPolicyIdentity Policy => new(42, Hash('c'));
    private static RegistryPolicyCheckpointIdentity Checkpoint => new(99, Hash('d'), 1, Hash('e'));
    private static RegistryExactRpcRequirement Rpc => new("example.agent.tools", "1.0.0", Hash('f'), Hash('1'));
    private static RegistryPackageAvailabilityChange Change => new(Package, "Listed", "Yanked");
    private static RegistryPackageCompatibilityRequirement Requirement => new(Package.PackageId, ">=2.0.0 <3.0.0", false, "required", [Rpc], "example.consumer");
    private static RuntimePackagePolicySubject LocalPackage => new(Package.PackageId, Package.Version, Package.PackageContentIdentity);
    private static Guid AppGeneration => Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static Guid HostInstance => Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static RuntimeAppProcessIdentity Process => new(42, "boot-session:process-birth-42", Hash('5'), "1.0.0+fixture", AppGeneration, HostInstance);
    private static RuntimeAppPolicySessionRequest Session => new(Nonce, AppGeneration, HostInstance);
    private static RuntimePolicyLeaseStatus Lease => new("valid", null,
        new("https://registry.example", 42, Hash('c'), 99, Hash('d'), 1, Hash('e')), Nonce, Hash('6'), Observed, Observed.AddHours(24),
        new("boot-session", "continuous-test-clock", 1000, 10_000, 86_410_000, 10_001, null));
    private static RuntimeRequiredProviderPredicate Predicate => new("example.consumer", "runtime", new(Rpc.ContractId, Rpc.ContractVersion, Rpc.DescriptorSha256, Rpc.AbiSha256), ["discover", "invoke"]);

    private static RuntimePolicyLeaseStatus RequiredLease => new("required", PackagePolicyErrorCodes.PolicyRequired, null, null, null, null, null, null);
    private static RuntimePolicyLeaseStatus ExpiredLease => Lease with
    { State = "expired", ErrorCode = PackagePolicyErrorCodes.PolicyExpired, Clock = Lease.Clock! with { HighWaterTicks = 86_410_001 } };

    public static TheoryData<string, object, string> Cases => new()
    {
        { "sync-request.json", new RegistryPolicySynchronizationRequest(Nonce, [Package], [Platform], Policy, Checkpoint), "synchronizationRequest" },
        { "sync-response.json", new RegistryPolicySynchronizationResponse(Nonce, "https://registry.example", Policy, Checkpoint, Observed, Observed.AddHours(24),
            [new(Package, "Active", "Listed", "Allowed", null, null, [Platform])], [new(Platform, "Supported", "Allowed", null, null)]), "synchronizationResponse" },
        { "package-denial.json", new RegistryPackagePolicyDecision(Package, "SecurityBlocked", "Listed", "Denied", PackagePolicyErrorCodes.SecurityBlocked, "Security policy blocks this package identity.", [Platform]), "packageDecision" },
        { "package-publication.json", new RegistryPackagePublicationEvidence(Package, Nonce, Hash('9'), "Yanked"), "publicationEvidence" },
        { "platform-denial.json", new RegistryPlatformPolicyDecision(Platform, "Revoked", "Denied", PackagePolicyErrorCodes.ContractAuthorityRevoked, Observed), "platformDecision" },
        { "cohort-request.json", new PackageVersionCohortAvailabilityTransitionV1("release-2026-09", 41, [Change], [new(Package.PackageId, "latest", null)]), "cohortRequest" },
        { "cohort-result.json", new PackageVersionCohortAvailabilityResultV1("release-2026-09", Hash('8'), Policy, Checkpoint, Observed, [Change],
            [new(Package.PackageId, [new(Package.PackageId, "latest", Package)])], [new(Package.PackageId, [])]), "cohortResult" },
        { "package-block.json", new RegistryPackageSecurityBlockRequest("block-package", 41, "Security policy blocks this package identity."), "packageBlockRequest" },
        { "version-block.json", new RegistryPackageVersionSecurityBlockRequest("block-version", 41, Hash('a'), "Security policy blocks this immutable version."), "versionBlockRequest" },
        { "policy-mutation.json", new RegistryPolicyMutationResult("block-package", Hash('8'), Policy, Checkpoint, Observed), "policyMutation" },
        { "compatibility-requirement.json", Requirement, "packageRequirement" },
        { "compatibility-conflict.json", new RegistryPackageCompatibilityConflict(Package.PackageId, "package-api.contract-conflict", [Requirement]), "compatibilityConflict" },
        { "resolution-policy.json", new RegistryPackageResolutionPolicy(Policy, Checkpoint, [Requirement],
            [new("https://registry.example", new(new(1, Package.PackageId, Package.Version, Package.PackageContentIdentity, 1,
                [new(Rpc.ContractId, Rpc.ContractVersion, 420, Rpc.DescriptorSha256, Rpc.AbiSha256, "owned", "package", Package.PackageId, Package.Version, Package.PackageContentIdentity)]), Hash('9')),
                ">=2.0.0 <3.0.0", false, [new(Package.PackageId, ">=2.0.0 <3.0.0", false)])],
            [new("example.consumer", "example.optional", "omitted", null, "package-api.package-unavailable")], [new(Platform, "Supported", "Allowed", null, null)]), "resolutionPolicy" },
        { "registry-status.json", new RegistryPolicyStatusV1("registry.ready", true, true, Checkpoint, Checkpoint, Checkpoint, Policy, new(8, Hash('a')), new(2, Hash('b')), 2), "registryStatus" },
        { "registry-bootstrap-required.json", new RegistryPolicyStatusV1(RegistryV1ErrorCodes.BootstrapRequired, false, false, null, null, null, null, null, null, null), "registryStatus" },
        { "stack-revision.json", new RegistryStackRevisionDetails(new("example.stack", 7, Hash('a')), Nonce, Nonce, Observed, "Example Stack", "Immutable revision.", null, Hash('b'), 512,
            "https://registry.example/api/v1/stacks/example.stack/revisions/7/download", [new(Package.PackageId, "latest", Package.Version, "2.0.0", true)], [], [],
            [new(AppGeneration, "cover.png", "image/png", 128, Hash('c'), "https://registry.example/media/cover.png", 0, null)]), "stackRevision" },
        { "stack-policy.json", new RegistryStackPolicySnapshot("example.stack", Nonce, new("example.stack", 7, Hash('a')), "Listed", null, Policy, Checkpoint), "stackPolicy" },
        { "stack-visibility.json", new RegistryStackVisibilityRequest("unlist-stack", 41, "Listed", "Unlisted"), "stackVisibilityRequest" },
        { "stack-block.json", new RegistryStackSecurityBlockRequest("block-stack", 41, "Security policy blocks this Stack."), "stackBlockRequest" },
        { "runtime-package-policy.json", new RuntimePackagePolicyStatus(LocalPackage, "Active", "Listed", "Allowed", null, null, null, Lease, "active"), "runtimePackagePolicy" },
        { "runtime-package-blocked.json", new RuntimePackagePolicyStatus(LocalPackage, "SecurityBlocked", "Listed", "Denied", "package", PackagePolicyErrorCodes.SecurityBlocked, "Security policy blocks this package identity.", Lease, "termination-required"), "runtimePackagePolicy" },
        { "runtime-platform-revoked.json", new RuntimePlatformPolicyStatus(new(Platform.AuthorityId, Platform.ReleaseVersion, Platform.CatalogSha256), "Revoked", "Denied", PackagePolicyErrorCodes.ContractAuthorityRevoked, Observed, Lease), "runtimePlatformPolicy" },
        { "lease-required.json", RequiredLease, "lease" },
        { "lease-expired.json", ExpiredLease, "lease" },
        { "lease-clock-invalid.json", Lease with { State = "clock-invalid", ErrorCode = PackagePolicyErrorCodes.PolicyClockInvalid, Clock = null }, "lease" },
        { "lease-stale.json", Lease with { State = "required", ErrorCode = PackagePolicyErrorCodes.PolicyStale }, "lease" },
        { "lease-conflict.json", Lease with { State = "required", ErrorCode = PackagePolicyErrorCodes.PolicyConflict }, "lease" },
        { "optional-included.json", new RegistryOptionalPackageDecision("example.consumer", Package.PackageId, "included", Package, null), "optionalPackage" },
        { "package-unknown.json", new RegistryPackagePolicyDecision(Package, "Unknown", "Unknown", "PolicyRequired", PackagePolicyErrorCodes.PolicyRequired, null, []), "packageDecision" },
        { "app-open.json", new RuntimeAppPolicySessionOpenRequest(Process), "appOpenRequest" },
        { "app-session.json", new RuntimeAppPolicySessionDescriptor(Nonce, Process, "active"), "appSession" },
        { "app-heartbeat.json", Session, "appSessionRequest" },
        { "app-session-status.json", new RuntimeAppPolicySessionStatus(Nonce, "termination-required", PackagePolicyErrorCodes.AppProcessTerminationRequired, true, [LocalPackage]), "appSessionStatus" },
        { "app-exit-proven.json", new RuntimeAppPolicySessionStatus(Nonce, "exit-proven", null, false, [LocalPackage]), "appSessionStatus" },
        { "app-permit-request.json", new RuntimeAppPackageLoadPermitRequest(Session, LocalPackage), "appPermitRequest" },
        { "app-permit-result.json", new RuntimeAppPackageLoadPermitResult(true, null, Nonce, Session, LocalPackage, Lease), "appPermitResult" },
        { "app-permit-denied.json", new RuntimeAppPackageLoadPermitResult(false, PackagePolicyErrorCodes.PolicyExpired, null, Session, LocalPackage, ExpiredLease), "appPermitResult" },
        { "provider-missing.json", new RuntimeRpcProviderSelectionDetails(new(Rpc.ContractId, Rpc.ContractVersion, Rpc.DescriptorSha256, Rpc.AbiSha256), Package.PackageId, 0), "providerSelection" },
        { "provider-unsatisfied.json", new RuntimeRequiredProviderUnsatisfiedDetails([Predicate]), "providerUnsatisfied" },
        { "provider-cycle.json", new RuntimeRequiredProviderCycleDetails(["example.agent", "example.consumer"], [Predicate]), "providerCycle" },
        { "provider-missing-problem.json", Problem(RpcReadinessErrorCodes.ProviderNotFound, new RuntimeRpcProviderSelectionDetails(Predicate.Contract, Package.PackageId, 0)), "providerProblem" },
        { "provider-ambiguous-problem.json", Problem(RpcReadinessErrorCodes.ProviderAmbiguous, new RuntimeRpcProviderSelectionDetails(Predicate.Contract, Package.PackageId, 2)), "providerProblem" },
        { "provider-unsatisfied-problem.json", Problem(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, new RuntimeRequiredProviderUnsatisfiedDetails([Predicate])), "providerProblem" },
        { "provider-cycle-problem.json", Problem(RpcReadinessErrorCodes.RequiredProviderCycle, new RuntimeRequiredProviderCycleDetails(["example.agent", "example.consumer"], [Predicate])), "providerProblem" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void PolicyPayload_HasExactReviewedJsonAndClosedRequiredShape(string file, object value, string schemaDefinition)
    {
        var type = value.GetType(); var actual = JsonSerializer.Serialize(value, type, Json).ReplaceLineEndings("\n");
        var update = Environment.GetEnvironmentVariable("SUNDER_UPDATE_POLICY_CONTRACT_GOLDENS");
        if (!string.IsNullOrEmpty(update))
        {
            Directory.CreateDirectory(update); File.WriteAllText(Path.Combine(update, file), actual + "\n");
            return;
        }
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PolicyContracts", file)).ReplaceLineEndings("\n").TrimEnd();
        Assert.Equal(expected, actual);
        var decoded = JsonSerializer.Deserialize(expected, type, Json)!;
        Assert.Equal(actual, JsonSerializer.Serialize(decoded, type, Json));
        var json = JsonNode.Parse(actual)!.AsObject();
        Assert.True(ValidateSchema(schemaDefinition, json), file);
        foreach (var key in json.Select(item => item.Key).ToArray())
        {
            var missing = json.DeepClone().AsObject(); missing.Remove(key);
            var rejected = Record.Exception(() => JsonSerializer.Deserialize(missing.ToJsonString(), type, Json));
            Assert.True(rejected is JsonException or ArgumentException, $"{file}: missing '{key}' did not produce a closed-contract error.");
            Assert.False(ValidateSchema(schemaDefinition, missing), $"{file}: schema accepted missing '{key}'.");
        }
        json["unrecognizedAuthority"] = true;
        Assert.False(ValidateSchema(schemaDefinition, json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.ToJsonString(), type, Json));
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("limit")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("extra")]
    public void SynchronizationWireSchema_RejectsInvalidOrIncompleteSubjectSets(string mutation)
    {
        var request = JsonSerializer.SerializeToNode(new RegistryPolicySynchronizationRequest(Nonce, [Package], [], null, null), Json)!.AsObject();
        switch (mutation)
        {
            case "nonce": request["nonce"] = "replayed-or-unbounded"; break;
            case "limit": request["packages"] = new JsonArray(Enumerable.Range(0, 129).Select(index => JsonSerializer.SerializeToNode(Package with { PackageId = $"example.p{index}" }, Json)).ToArray()); break;
            case "duplicate": request["packages"]!.AsArray().Add(request["packages"]![0]!.DeepClone()); break;
            case "empty": request["packages"] = new JsonArray(); break;
            case "null": request["platformCatalogs"] = null; break;
            case "extra": request["trusted"] = true; break;
        }
        Assert.False(ValidateSchema("synchronizationRequest", request));
    }

    [Theory]
    [InlineData("same-state")]
    [InlineData("terminal")]
    [InlineData("empty")]
    [InlineData("partial-result")]
    public void CohortWireSchema_RejectsNonAtomicOrTerminalTransitions(string mutation)
    {
        var value = JsonSerializer.SerializeToNode(new PackageVersionCohortAvailabilityTransitionV1("cohort", 1, [Change], []), Json)!.AsObject();
        switch (mutation)
        {
            case "same-state": value["versions"]![0]!["availability"] = "Listed"; break;
            case "terminal": value["versions"]![0]!["availability"] = "SecurityBlocked"; break;
            case "empty": value["versions"] = new JsonArray(); break;
            case "partial-result": value["successfulVersions"] = new JsonArray(); break;
        }
        Assert.False(ValidateSchema("cohortRequest", value));
    }

    [Fact]
    public void ConstraintsDoNotUseRestoreHashesAsInstallationPins_AndCollectionsAreDetachedOnConstructionAndWith()
    {
        Assert.DoesNotContain(typeof(RegistryPackageCompatibilityRequirement).GetProperties(), property => property.Name.Contains("ContentIdentity", StringComparison.Ordinal));
        Assert.Contains(typeof(RegistryPackageRestoreProvenance).GetProperties(), property => property.PropertyType == typeof(RegistryPackagePublicApiInventoryResponseV1));
        RegistryExactRpcRequirement[] tuples = [Rpc];
        var original = Requirement with { Contracts = tuples }; tuples[0] = Rpc with { ContractVersion = "2.0.0" };
        Assert.Equal("1.0.0", Assert.Single(original.Contracts).ContractVersion);
        RegistryPackageVersionIdentity[] packages = [Package];
        var request = new RegistryPolicySynchronizationRequest(Nonce, packages, [], null, null);
        packages[0] = Package with { Version = "3.0.0" }; Assert.Equal("2.0.0", Assert.Single(request.Packages).Version);
        string[] actions = ["discover"];
        var predicate = Predicate with { Actions = actions }; actions[0] = "invoke"; Assert.Equal("discover", Assert.Single(predicate.Actions));
        Assert.Throws<ArgumentException>(() => request with { Packages = [null!] });
        Assert.Throws<ArgumentException>(() => Predicate with { Actions = [null!] });
    }

    [Theory]
    [InlineData("1.0.0-01")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0-a..b")]
    public void PolicyVersionSchemaRejectsNoncanonicalSemanticIdentities(string version)
        => Assert.False(ValidateSchema("package", JsonSerializer.SerializeToNode(Package with { Version = version }, Json)!));

    [Theory]
    [InlineData("package-denial.json", "packageDecision", "disposition", "Allowed")]
    [InlineData("package-unknown.json", "packageDecision", "disposition", "Allowed")]
    [InlineData("platform-denial.json", "platformDecision", "disposition", "Allowed")]
    [InlineData("app-permit-denied.json", "appPermitResult", "allowed", true)]
    [InlineData("app-session-status.json", "appSessionStatus", "shutdownRequired", false)]
    [InlineData("lease-expired.json", "lease", "errorCode", null)]
    [InlineData("lease-clock-invalid.json", "lease", "state", "valid")]
    [InlineData("optional-included.json", "optionalPackage", "selectedPackage", null)]
    [InlineData("registry-bootstrap-required.json", "registryStatus", "publicReady", true)]
    [InlineData("provider-missing-problem.json", "providerProblem", "status", 409)]
    [InlineData("provider-cycle-problem.json", "providerProblem", "status", 404)]
    public void PolicySchemaRejectsBroadenedOrContradictoryDecisions(string file, string definition, string property, object? value)
    {
        var data = ReadGolden(file);
        data[property] = JsonSerializer.SerializeToNode(value, Json);
        Assert.False(ValidateSchema(definition, data));
    }

    [Fact]
    public void PolicySchemaChecksNestedEvidenceRatherThanOnlyItsEnvelope()
    {
        var data = ReadGolden("resolution-policy.json");
        data["restoreProvenance"]![0]!["artifact"]!["inventory"]!["contracts"]![0]!["authorityKind"] = "unverified";
        Assert.False(ValidateSchema("resolutionPolicy", data));
        data = ReadGolden("resolution-policy.json");
        data["optionalPackages"]![0]!["selectedPackage"] = JsonSerializer.SerializeToNode(Package, Json);
        Assert.False(ValidateSchema("resolutionPolicy", data));
        data = ReadGolden("stack-revision.json");
        data["media"]![0]!["storageKey"] = "internal/key";
        Assert.False(ValidateSchema("stackRevision", data));
        data = ReadGolden("app-permit-result.json");
        data["lease"]!["clock"] = null;
        Assert.False(ValidateSchema("appPermitResult", data));
        Assert.False(ValidateSchema("package", JsonSerializer.SerializeToNode(Package with { PackageId = "example.invalid-id" }, Json)!));
    }

    [Fact]
    public void PolicyBoundarySerializerRejectsDuplicateCaseNullAndCoercedMembersAndPreservesInt64()
    {
        var canonical = JsonSerializer.Serialize(Policy, Json);
        foreach (var invalid in new[]
        {
            canonical.Replace("\"revision\": 42", "\"revision\": 42, \"revision\": 43", StringComparison.Ordinal),
            canonical.Replace("\"revision\": 42", "\"Revision\": 42", StringComparison.Ordinal),
            canonical.Replace("\"revision\": 42", "\"revision\": \"42\"", StringComparison.Ordinal),
            canonical.Replace($"\"{Hash('c')}\"", "null", StringComparison.Ordinal),
        }) Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistryPolicyIdentity>(invalid, Json));
        var largest = Policy with { Revision = long.MaxValue };
        Assert.Equal(largest, JsonSerializer.Deserialize<RegistryPolicyIdentity>(JsonSerializer.Serialize(largest, Json), Json));
        Assert.True(ValidateSchema("policyIdentity", JsonSerializer.SerializeToNode(largest, Json)!));
        using var overflow = JsonDocument.Parse("{\"revision\":9223372036854775808,\"fingerprint\":\"" + Hash('c') + "\"}");
        Assert.False(ValidateSchema("policyIdentity", JsonNode.Parse(overflow.RootElement.GetRawText())!));
        Assert.Throws<JsonException>(() => overflow.RootElement.Deserialize<RegistryPolicyIdentity>(Json));
    }

    [Fact]
    public void EveryPolicyGoldenIsAssignedARequiredSchema()
    {
        Assert.Equal(Cases.Select(data => (string)data[0]).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "PolicyContracts"), "*.json")
                .Select(Path.GetFileName).Order(StringComparer.Ordinal));
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "PolicySpecs", "conformance.json")));
        Assert.Equal(1, corpus.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Cases.Select(data => ((string)data[0], (string)data[2])).OrderBy(item => item.Item1, StringComparer.Ordinal),
            corpus.RootElement.GetProperty("fixtures").EnumerateObject().Select(item => (item.Name, item.Value.GetString()!)).OrderBy(item => item.Item1, StringComparer.Ordinal));
    }

    [Fact]
    public void PolicyWireContractAssembliesStayDependencyFreeAndExposeNoInternalProcessOrStorageCapabilities()
    {
        foreach (var assembly in new[] { typeof(RegistryPolicySynchronizationRequest).Assembly, typeof(RuntimeAppProcessIdentity).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name!.StartsWith("Sunder.", StringComparison.Ordinal)
                || name.Name.Contains("EntityFramework", StringComparison.Ordinal));
        foreach (var data in Cases)
            Assert.DoesNotContain(data[1].GetType().GetProperties(), property => property.Name.Contains("StorageKey", StringComparison.Ordinal)
                || property.Name.Contains("FilePath", StringComparison.Ordinal) || property.Name.Contains("Token", StringComparison.Ordinal)
                || typeof(Stream).IsAssignableFrom(property.PropertyType));
    }

    [Theory]
    [InlineData("sync-request.json", "runtimes")]
    [InlineData("sync-request.json", "minimumRuntimePolicyRevision")]
    [InlineData("sync-response.json", "runtimes")]
    [InlineData("sync-response.json", "runtimePolicyRevision")]
    [InlineData("registry-status.json", "runtimePolicyRevision")]
    public void PackagePlatformPolicyRejectsRemovedRuntimeAuthorityEvenWhenNull(string file, string property)
    {
        var entry = Cases.Single(data => (string)data[0] == file);
        var value = entry[1];
        var type = value.GetType();
        var json = JsonSerializer.SerializeToNode(value, type, Json)!.AsObject();
        Assert.NotNull(JsonSerializer.Deserialize(json.ToJsonString(), type, Json));
        Assert.True(ValidateSchema((string)entry[2], json));
        json[property] = null;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.ToJsonString(), type, Json));
        Assert.False(ValidateSchema((string)entry[2], json));
    }

    [Fact]
    public void PackagePlatformSubjectBoundsRejectOverflowAndKeepBothSubjectKindsIndependent()
    {
        var platforms = new[] { Platform };
        var request = new RegistryPolicySynchronizationRequest(Nonce, [], platforms, Policy, Checkpoint);
        platforms[0] = Platform with { ReleaseVersion = "99.0.0" };
        Assert.Equal(Platform, Assert.Single(request.PlatformCatalogs));
        Assert.True(ValidateSchema("synchronizationRequest", JsonSerializer.SerializeToNode(request, Json)!));
        var replacement = new[] { Platform };
        var copied = request with { PlatformCatalogs = replacement };
        replacement[0] = platforms[0];
        Assert.Equal(Platform, Assert.Single(copied.PlatformCatalogs));
        Assert.Throws<NotSupportedException>(() => ((IList<RegistryPlatformCatalogIdentity>)copied.PlatformCatalogs).Clear());
        Assert.Throws<ArgumentException>(() => request with
        { Packages = Enumerable.Repeat(Package, RegistryPolicyLimits.MaximumPackageSubjects + 1).ToArray() });
        Assert.Throws<ArgumentException>(() => new RegistryPolicySynchronizationRequest(Nonce, [],
            Enumerable.Repeat(Platform, RegistryPolicyLimits.MaximumPlatformSubjects + 1).ToArray(), Policy, Checkpoint));
        var watermark = JsonSerializer.SerializeToNode(Lease.Watermark, Json)!.AsObject();
        watermark["runtimePolicyRevision"] = 1;
        Assert.Throws<JsonException>(() => watermark.Deserialize<RuntimePolicyWatermark>(Json));
        Assert.False(ValidateSchema("watermark", watermark));
    }

    [Fact]
    public void PolicyTimingsAndErrorTaxonomyMatchTheReviewedEnforcementBoundary()
    {
        Assert.Equal(86_400, RegistryPolicyLimits.LeaseSeconds); Assert.Equal(16, RegistryPolicyLimits.NonceBytes);
        Assert.Equal(RegistryPolicyLimits.LeaseSeconds, RuntimePolicyTiming.LeaseSeconds);
        Assert.Equal((300, 30, 15, 900), (RuntimePolicyTiming.RefreshSeconds, RuntimePolicyTiming.MaximumRefreshJitterSeconds, RuntimePolicyTiming.RequestDeadlineSeconds, RuntimePolicyTiming.MaximumRetrySeconds));
        Assert.Equal((5, 5, 10, 5), (RuntimePolicyTiming.AppHeartbeatGraceSeconds, RuntimePolicyTiming.AppGracefulShutdownSeconds, RuntimePolicyTiming.AppForcedExitVerificationSeconds, RuntimePolicyTiming.AppForcedExitRetrySeconds));
        Assert.Equal(Observed.AddHours(24), Lease.ValidUntilUtc);
        Assert.Equal(RuntimePolicyTiming.LeaseSeconds * Lease.Clock!.TicksPerSecond, Lease.Clock.DeadlineTicks - Lease.Clock.RequestStartedTicks);
        var codes = new[] { typeof(PackagePolicyErrorCodes), typeof(RpcReadinessErrorCodes) }.SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static)).Select(field => (string)field.GetRawConstantValue()!).Order(StringComparer.Ordinal);
        Assert.Equal(codes, PolicyErrorCatalog.All.Select(item => item.Code).Order(StringComparer.Ordinal));
        Assert.Equal(PolicyErrorCatalog.All.Count, PolicyErrorCatalog.All.Select(item => item.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(PolicyErrorCatalog.All, item => { Assert.InRange(item.HttpStatus, 400, 599); Assert.NotEmpty(item.SafeMessage); Assert.NotEmpty(item.Owner); Assert.NotEmpty(item.DiagnosticFields); });
        Assert.Equal(404, PolicyErrorCatalog.All.Single(item => item.Code == RpcReadinessErrorCodes.ProviderNotFound).HttpStatus);
        Assert.Equal(409, PolicyErrorCatalog.All.Single(item => item.Code == RpcReadinessErrorCodes.RequiredProviderCycle).HttpStatus);
        Assert.False(PolicyErrorCatalog.All.Single(item => item.Code == PackagePolicyErrorCodes.SecurityBlocked).Retryable);
    }

    [Fact]
    public void PolicyRoutesAreStableEscapedAndSeparateFromArtifactAndClaimOperations()
    {
        Assert.Equal("/api/v1/policy/synchronize", RegistryApiRoutes.PolicySynchronization);
        Assert.Equal("/api/v1/me/package-cohorts/availability", RegistryApiRoutes.PackageCohortAvailability);
        Assert.Equal("/api/v1/packages/example%2Fid/security-block", RegistryApiRoutes.PackageSecurityBlock("example/id"));
        Assert.Equal("/api/v1/stacks/example.stack/revisions/7/download", RegistryApiRoutes.StackRevisionDownload("example.stack", 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryApiRoutes.StackRevision("example.stack", 0));
        Assert.Equal("/api/v1/app-policy-sessions/session%2Fid/load-permit", RuntimeApiRoutes.AppPolicyLoadPermit("session/id"));
        Assert.Equal("/api/v1/app-policy-sessions/session%2Fid/shutdown", RuntimeApiRoutes.AppPolicyShutdown("session/id"));
        Assert.Throws<ArgumentException>(() => RuntimeApiRoutes.AppPolicySession(".."));
    }

    private static bool ValidateSchema(string definition, JsonNode data)
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "PolicySpecs", "contracts.schema.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "RpcV1Authoring", "package-public-inventory.schema.json")));
        using var instance = JsonDocument.Parse(data.ToJsonString());
        return JsonSchemaTestValidator.IsValid(schema.RootElement.GetProperty("$defs").GetProperty(definition), instance.RootElement, schema.RootElement,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            { [inventory.RootElement.GetProperty("$id").GetString()!] = inventory.RootElement });
    }

    [Fact]
    public void PackagePlansCarryVerifiedPolicyIdentityWithoutInventingItForFailures()
    {
        var plan = new RegistryResolveInstallPlanResponse(true, [], [], [], [], []) { PackagePolicy = Policy, PolicyCheckpoint = Checkpoint };
        var json = JsonSerializer.SerializeToNode(plan, Json)!.AsObject();
        Assert.Equal(42, json["packagePolicy"]!["revision"]!.GetValue<long>());
        Assert.Equal(99, json["policyCheckpoint"]!["sequence"]!.GetValue<long>());
        var decoded = JsonSerializer.Deserialize<RegistryResolveInstallPlanResponse>(json.ToJsonString(), Json)!;
        Assert.Equal(Policy, decoded.PackagePolicy); Assert.Equal(Checkpoint, decoded.PolicyCheckpoint);
        var failure = JsonSerializer.SerializeToNode(new RegistryResolveInstallPlanResponse(false, [], [], ["Unavailable."], [], []), Json)!.AsObject();
        Assert.False(failure.ContainsKey("packagePolicy")); Assert.False(failure.ContainsKey("policyCheckpoint"));
        var updates = new RegistryResolveUpdatesResponse([], [], []) { PackagePolicy = Policy, PolicyCheckpoint = Checkpoint };
        Assert.Equal(Policy, JsonSerializer.Deserialize<RegistryResolveUpdatesResponse>(JsonSerializer.Serialize(updates, Json), Json)!.PackagePolicy);
    }

    [Fact]
    public void CompatiblePlansExposeRequirementsAndDetachedConflictsSeparatelyFromRestoreProvenance()
    {
        var policy = new RegistryPackageResolutionPolicy(Policy, Checkpoint, [Requirement], [], [], []);
        RegistryPackageCompatibilityConflict[] conflicts = [new(Package.PackageId, PackagePolicyErrorCodes.ContractAuthorityReleaseConflict, [Requirement])];
        var response = new RegistryResolveInstallPlanResponse(false, [], [], [], [], []) { ResolutionPolicy = policy, CompatibilityConflicts = conflicts };
        conflicts[0] = conflicts[0] with { PackageId = "changed" };
        Assert.Equal(Package.PackageId, Assert.Single(response.CompatibilityConflicts).PackageId);
        var parsed = JsonSerializer.Deserialize<RegistryResolveInstallPlanResponse>(JsonSerializer.Serialize(response, Json), Json)!;
        Assert.Equal(Rpc, Assert.Single(Assert.Single(parsed.ResolutionPolicy!.Requirements).Contracts));
        Assert.Equal(PackagePolicyErrorCodes.ContractAuthorityReleaseConflict, Assert.Single(parsed.CompatibilityConflicts).ErrorCode);
        Assert.Throws<ArgumentException>(() => response with { CompatibilityConflicts = [null!] });
        Assert.Equal(409, PolicyErrorCatalog.All.Single(value => value.Code == PackagePolicyErrorCodes.CandidateAuthorityUnconfirmed).HttpStatus);
    }

    [Fact]
    public void ManagementPreconditionsKeepAuthorityAndPackageRevisionsSeparate()
    {
        Assert.Equal("X-Sunder-Idempotency-Key", RegistryPolicyMutationHeaders.IdempotencyKey);
        Assert.Equal("X-Sunder-Package-Policy-Revision", RegistryPolicyMutationHeaders.PackagePolicyRevision);
        Assert.Equal("X-Sunder-Publication-Authority-Revision", RegistryPolicyMutationHeaders.PublicationAuthorityRevision);
        var tags = new RegistryPackageDistTagsResponse("example.package", []) { PackagePolicy = Policy };
        var members = new RegistryPackageMaintainersResponse("example.package", []) { PublicationAuthorityPolicy = Policy };
        Assert.Equal(Policy, JsonSerializer.Deserialize<RegistryPackageDistTagsResponse>(JsonSerializer.Serialize(tags, Json), Json)!.PackagePolicy);
        Assert.Equal(Policy, JsonSerializer.Deserialize<RegistryPackageMaintainersResponse>(JsonSerializer.Serialize(members, Json), Json)!.PublicationAuthorityPolicy);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RegistrySetPackageVersionYankRequest>("{}", Json));
        Assert.False(JsonSerializer.Deserialize<RegistrySetPackageVersionYankRequest>("{\"isYanked\":false}", Json)!.IsYanked);
    }

    private static JsonObject ReadGolden(string file)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PolicyContracts", file)))!.AsObject();

    private static RuntimePolicyProblemDetails<TDetails> Problem<TDetails>(string code, TDetails details) where TDetails : class
    {
        var error = PolicyErrorCatalog.All.Single(item => item.Code == code);
        return new("about:blank", error.SafeMessage, error.HttpStatus, error.SafeMessage,
            "/api/v1/packages/example.consumer/activate", code, "trace-policy-1", "correlation-policy-1", details);
    }
}
