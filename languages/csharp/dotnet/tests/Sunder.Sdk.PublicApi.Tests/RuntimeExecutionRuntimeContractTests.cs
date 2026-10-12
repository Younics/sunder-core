using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class RuntimeExecutionRuntimeContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
    private static string Hash(char value) => new(value, 64);

    public static TheoryData<string, object, Type> Cases => new()
    {
        { "installed-runtime.json", Summary(), typeof(InstalledExecutionRuntimeSummary) },
        {
            "runtime-rebind-result.json",
            new ExecutionRuntimeRebindResult(true, null, "The package now uses the resolved execution runtime.",
                "sunder.package.sample", "1.2.3", "package-content-identity-123", Binding(Identity("10.0.0", 'f')),
                Binding(Identity()), true, Impact()),
            typeof(ExecutionRuntimeRebindResult)
        },
        {
            "runtime-ensure-result.json",
            new RuntimeExecutionRuntimeOperationResult(true, null, "The execution runtime is ready.",
                RuntimeExecutionRuntimeOperationActions.Ensure, Summary(), false),
            typeof(RuntimeExecutionRuntimeOperationResult)
        },
        {
            "runtime-store-fault.json",
            new RuntimeExecutionRuntimeStoreFault(ExecutionRuntimeErrorCodes.IntegrityFailed,
                "Execution runtime integrity verification failed.", false, RuntimeExecutionRuntimeOperationActions.Verify,
                Identity(), "sunder.package.sample", "runtime", Observed),
            typeof(RuntimeExecutionRuntimeStoreFault)
        },
        { "runtime-resolution.json", NodeResolution(), typeof(RuntimeExecutionRuntimeResolution) },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExecutionRuntimePayload_MatchesGoldenJson(string fixtureName, object payload, Type payloadType)
    {
        var actual = JsonSerializer.Serialize(payload, payloadType, JsonOptions).ReplaceLineEndings("\n");
        var updateRoot = Environment.GetEnvironmentVariable("SUNDER_UPDATE_RUNTIME_CONTRACT_GOLDENS");
        if (!string.IsNullOrWhiteSpace(updateRoot))
        {
            Directory.CreateDirectory(updateRoot);
            File.WriteAllText(Path.Combine(updateRoot, fixtureName), actual + Environment.NewLine);
            return;
        }

        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RuntimeContracts", fixtureName)).ReplaceLineEndings("\n").TrimEnd();
        Assert.Equal(expected, actual);
        Assert.Equal(actual, JsonSerializer.Serialize(JsonSerializer.Deserialize(expected, payloadType, JsonOptions), payloadType, JsonOptions));
    }

    [Fact]
    public void RuntimeApiRoutes_BindEveryVendorIdentityComponentAndEscapeSegments()
    {
        var identity = Identity();
        var path = "/api/v1/execution-runtimes/sources/dotnet/sunder.runtime.dotnet/versions/10.0.1/win-x64/abis/sunder.dotnet-worker-module.v1/checksums/sha512/" + new string('a', 128);
        Assert.Equal("/api/v1", RuntimeApiRoutes.Root);
        Assert.Equal("/api/v1/execution-runtimes", RuntimeApiRoutes.ExecutionRuntimes);
        Assert.Equal("/api/v1/execution-runtimes/ensure", RuntimeApiRoutes.ExecutionRuntimeEnsure);
        Assert.Equal("/api/v1/execution-runtimes/gc", RuntimeApiRoutes.ExecutionRuntimeGarbageCollection);
        Assert.Equal(path, RuntimeApiRoutes.ExecutionRuntime(identity));
        Assert.Equal(path + "/references", RuntimeApiRoutes.ExecutionRuntimeReferences(identity));
        Assert.Equal(path + "/impact", RuntimeApiRoutes.ExecutionRuntimeImpact(identity));
        Assert.Equal(path + "/verify", RuntimeApiRoutes.ExecutionRuntimeVerify(identity));
        Assert.Equal(path + "/repair", RuntimeApiRoutes.ExecutionRuntimeRepair(identity));
        Assert.Equal("/api/v1/execution-runtimes/sources/node/sunder.runtime.node/versions/24.18.1/linux-arm64/abis/sunder.node-worker-module.v1/checksums/sha256/" + Hash('b'),
            RuntimeApiRoutes.ExecutionRuntime(NodeIdentity()));
        foreach (var changed in new[]
        {
            identity with { SourceId = ConfiguredOfficialSourceId.Node },
            identity with { RuntimeId = "other/runtime" }, identity with { VendorVersion = "10.0.2" },
            identity with { Rid = "linux-x64" }, identity with { ModuleAbi = "different/abi" },
            identity with { VendorChecksumAlgorithm = RuntimeExecutionRuntimeChecksumAlgorithm.Sha256 },
            identity with { VendorChecksumDigest = new string('f', 128) },
        }) Assert.NotEqual(path, RuntimeApiRoutes.ExecutionRuntime(changed));
        Assert.Contains("/other%2Fruntime/", RuntimeApiRoutes.ExecutionRuntime(identity with { RuntimeId = "other/runtime" }));
        Assert.Equal("/api/v1/packages/sample%2Fpackage/execution-runtime-bindings", RuntimeApiRoutes.PackageExecutionRuntimeBindings("sample/package"));
        Assert.Throws<ArgumentException>(() => RuntimeApiRoutes.PackageExecutionRuntimeBindings(".."));
        Assert.Throws<ArgumentException>(() => RuntimeApiRoutes.ExecutionRuntime(identity with { VendorVersion = ".." }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeApiRoutes.ExecutionRuntime(identity with { SourceId = (ConfiguredOfficialSourceId)99 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeApiRoutes.ExecutionRuntime(identity with { VendorChecksumAlgorithm = (RuntimeExecutionRuntimeChecksumAlgorithm)99 }));
    }

    [Fact]
    public void IdentityProvenanceAndSizeNullabilityHaveDistinctWireMeaning()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Summary(), JsonOptions));
        var identity = json.RootElement.GetProperty("identity");
        Assert.Equal(new[] { "sourceId", "runtimeId", "vendorVersion", "rid", "moduleAbi", "vendorChecksumAlgorithm", "vendorChecksumDigest" },
            identity.EnumerateObject().Select(property => property.Name));
        Assert.Equal("dotnet", identity.GetProperty("sourceId").GetString());
        Assert.Equal("sha512", identity.GetProperty("vendorChecksumAlgorithm").GetString());
        Assert.Equal(128, identity.GetProperty("vendorChecksumDigest").GetString()!.Length);
        var installed = json.RootElement.GetProperty("installedArtifact");
        Assert.Equal(64, installed.GetProperty("archiveSha256").GetString()!.Length);
        Assert.Equal(104857600, installed.GetProperty("archiveSize").GetInt64());
        Assert.Equal(JsonValueKind.Null, installed.GetProperty("provenance").GetProperty("distribution").GetProperty("downloadSize").ValueKind);
        var context = new NullabilityInfoContext();
        Assert.Equal(NullabilityState.NotNull, context.Create(typeof(InstalledExecutionRuntimeSummary).GetProperty(nameof(InstalledExecutionRuntimeSummary.Identity))!).ReadState);
        Assert.Equal(NullabilityState.NotNull, context.Create(typeof(InstalledPackageDescriptor).GetProperty(nameof(InstalledPackageDescriptor.ExecutionRuntimeBindings))!).ReadState);
        Assert.Equal(typeof(long?), typeof(RuntimeExecutionRuntimeDistribution).GetProperty(nameof(RuntimeExecutionRuntimeDistribution.DownloadSize))!.PropertyType);
        Assert.Equal(typeof(long?), typeof(RuntimeExecutionRuntimeResolution).GetProperty(nameof(RuntimeExecutionRuntimeResolution.DownloadSize))!.PropertyType);
        Assert.Equal(typeof(long), typeof(RuntimeExecutionRuntimeInstalledArtifact).GetProperty(nameof(RuntimeExecutionRuntimeInstalledArtifact.ArchiveSize))!.PropertyType);
        Assert.Equal(NullabilityState.Nullable, context.Create(typeof(RuntimeExecutionRuntimeOperationResult).GetProperty(nameof(RuntimeExecutionRuntimeOperationResult.ErrorCode))!).ReadState);
        Assert.Equal(NullabilityState.Nullable, context.Create(typeof(ExecutionRuntimeRebindResult).GetProperty(nameof(ExecutionRuntimeRebindResult.PreviousBinding))!).ReadState);
        var unknown = NodeResolution();
        var cached = unknown with { AlreadyInstalled = true, DownloadSize = 0 };
        Assert.Null(JsonSerializer.Deserialize<RuntimeExecutionRuntimeResolution>(JsonSerializer.Serialize(unknown, JsonOptions), JsonOptions)!.DownloadSize);
        Assert.Equal(0L, JsonSerializer.Deserialize<RuntimeExecutionRuntimeResolution>(JsonSerializer.Serialize(cached, JsonOptions), JsonOptions)!.DownloadSize);
    }

    public static TheoryData<object> ClosedShapes => new()
    {
        Identity(), Distribution(Identity()), Support(), Summary().InstalledArtifact.Provenance,
        Summary().InstalledArtifact, Summary(), Binding(Identity()), NodeResolution(), NodeResolution().Artifact,
        new RuntimeExecutionRuntimeEnsureRequest(Requirement()),
        new RuntimeExecutionRuntimeInstallPreviewRequest(Requirement()),
        new RuntimeExecutionRuntimeCatalogRequest(),
        new RuntimeExecutionRuntimePlanConfirmation("plan", Hash('f')),
        new RuntimeExecutionRuntimeUpdatePreviewRequest(),
        new RuntimeExecutionRuntimeUninstallPreviewRequest(Identity()),
        new RuntimePackageRuntimePrerequisiteRequest(),
    };

    [Theory]
    [MemberData(nameof(ClosedShapes))]
    public void OfficialRuntimeShapesRejectUnknownAndMissingRequiredMembersWithDefaultWebOptions(object value)
    {
        var type = value.GetType();
        var json = JsonSerializer.SerializeToNode(value, type, JsonOptions)!.AsObject();
        Assert.NotNull(JsonSerializer.Deserialize(json.ToJsonString(), type, JsonOptions));
        foreach (var property in type.GetProperties().Where(property => property.IsDefined(typeof(JsonRequiredAttribute))))
        {
            var missing = json.DeepClone().AsObject();
            Assert.True(missing.Remove(JsonNamingPolicy.CamelCase.ConvertName(property.Name)));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(missing.ToJsonString(), type, JsonOptions));
        }
        foreach (var name in new[] { "registryOrigin", "runtimeRegistryOrigin", "packagingRevision", "signingKeyId", "trustGeneration", "manifestSha256" })
        {
            var extra = json.DeepClone().AsObject(); extra[name] = null;
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(extra.ToJsonString(), type, JsonOptions));
        }
    }

    [Theory]
    [InlineData(typeof(ConfiguredOfficialSourceId))]
    [InlineData(typeof(RuntimeExecutionRuntimeChecksumAlgorithm))]
    [InlineData(typeof(RuntimeExecutionRuntimeArchiveFormat))]
    [InlineData(typeof(RuntimeExecutionRuntimeSupportState))]
    [InlineData(typeof(RuntimeExecutionRuntimeUpdateState))]
    [InlineData(typeof(RuntimeExecutionRuntimeApplyMode))]
    public void OfficialRuntimeEnumsAreClosedStringsEvenWithoutSerializerConfiguration(Type type)
    {
        foreach (var value in Enum.GetValues(type))
        {
            var json = JsonSerializer.Serialize(value, type);
            Assert.StartsWith("\"", json);
            Assert.Equal(value, JsonSerializer.Deserialize(json, type));
        }
        foreach (var invalid in new[] { "0", "1", "999", "\"999\"", "\"untrusted-vendor\"" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(invalid, type));
    }

    [Fact]
    public void RuntimeImpact_FreezesExactImpactedPackageIds()
    {
        string[] packageIds = ["sunder.package.sample", "sunder.package.tools"];
        var impact = new RuntimeExecutionRuntimeImpact(2, packageIds, Hash('f'));
        packageIds[0] = "mutated";
        Assert.Equal(["sunder.package.sample", "sunder.package.tools"], impact.ImpactedPackageIds);
        Assert.Throws<ArgumentException>(() => impact with { ImpactedPackageIds = [null!] });
    }

    [Fact]
    public void RuntimeDtos_DoNotExposeStorePathsOrTokensAndErrorCatalogIsHostOwned()
    {
        var types = typeof(InstalledExecutionRuntimeSummary).Assembly.GetExportedTypes()
            .Where(type => type.Name.Contains("ExecutionRuntime", StringComparison.Ordinal)).ToArray();
        var names = types.SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)).Select(property => property.Name);
        Assert.DoesNotContain(names, name => name.Contains("Path", StringComparison.OrdinalIgnoreCase) || name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        var codes = new[] { typeof(ExecutionRuntimeErrorCodes), typeof(ExecutionRuntimeResolutionConflictCodes) }
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static)).Select(field => (string)field.GetRawConstantValue()!).ToArray();
        Assert.Equal(codes.Order(StringComparer.Ordinal), ExecutionRuntimeErrorCatalog.All.Select(error => error.Code).Order(StringComparer.Ordinal));
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ExecutionRuntimeErrorCatalog.All, error =>
        {
            Assert.Equal("runtime-host", error.Owner);
            Assert.InRange(error.HttpStatus, 400, 599);
            Assert.NotEmpty(error.SafeMessage);
            Assert.DoesNotContain(error.DiagnosticFields, field => field.Contains("path", StringComparison.OrdinalIgnoreCase) || field.Contains("token", StringComparison.OrdinalIgnoreCase));
        });
    }

    private static RuntimeExecutionRuntimeRequirement Requirement()
        => new("sunder.runtime.dotnet", ">=10.0.0 <11.0.0", "sunder.dotnet-worker-module.v1");

    private static RuntimeExecutionRuntimeIdentity Identity(string version = "10.0.1", char digest = 'a')
        => new(ConfiguredOfficialSourceId.DotNet, "sunder.runtime.dotnet", version, "win-x64",
            "sunder.dotnet-worker-module.v1", RuntimeExecutionRuntimeChecksumAlgorithm.Sha512, new string(digest, 128));

    private static RuntimeExecutionRuntimeIdentity NodeIdentity()
        => new(ConfiguredOfficialSourceId.Node, "sunder.runtime.node", "24.18.1", "linux-arm64",
            "sunder.node-worker-module.v1", RuntimeExecutionRuntimeChecksumAlgorithm.Sha256, Hash('b'));

    private static RuntimeExecutionRuntimeDistribution Distribution(RuntimeExecutionRuntimeIdentity identity)
        => identity.SourceId == ConfiguredOfficialSourceId.DotNet
            ? new($"https://builds.dotnet.microsoft.com/dotnet/Runtime/{identity.VendorVersion}/dotnet-runtime-{identity.VendorVersion}-win-x64.zip",
                "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json", Hash('d'), null, null,
                RuntimeExecutionRuntimeArchiveFormat.Zip, "dotnet-runtime.v1", null)
            : new($"https://nodejs.org/dist/v{identity.VendorVersion}/node-v{identity.VendorVersion}-linux-arm64.tar.gz",
                "https://nodejs.org/dist/index.json", Hash('d'), $"https://nodejs.org/dist/v{identity.VendorVersion}/SHASUMS256.txt", Hash('e'),
                RuntimeExecutionRuntimeArchiveFormat.TarGzip, "node-runtime.v1", null);

    private static RuntimeExecutionRuntimeSupportStatus Support(string line = "10.0")
        => new(RuntimeExecutionRuntimeSupportState.Supported, line, Observed, null, null);

    private static RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact(RuntimeExecutionRuntimeIdentity identity)
        => new(Hash('c'), 104857600, Hash('b'), new(Distribution(identity), Distribution(identity).DownloadUrl, Observed));

    private static InstalledExecutionRuntimeSummary Summary()
        => new(Identity(), RuntimeExecutionRuntimeStatuses.Ready, 209715200, 3, InstalledArtifact(Identity()),
            new(RuntimeExecutionRuntimeStatuses.Ready, Observed, false, null), Support(), Observed, true);

    private static PackageTargetExecutionRuntimeBinding Binding(RuntimeExecutionRuntimeIdentity identity)
        => new("runtime", identity.Rid, Requirement(), identity, InstalledArtifact(identity), Observed);

    private static RuntimeExecutionRuntimeImpact Impact()
        => new(3, ["sunder.package.sample", "sunder.package.tools", "sunder.package.workflow"], Hash('f'));

    private static RuntimeExecutionRuntimeResolution NodeResolution()
        => new(new("sunder.runtime.node", ">=24.18.1 <25.0.0", "sunder.node-worker-module.v1"),
            new(NodeIdentity(), Distribution(NodeIdentity()), Support("24")), Hash('f'), Observed.AddMinutes(5), false, null);
}
