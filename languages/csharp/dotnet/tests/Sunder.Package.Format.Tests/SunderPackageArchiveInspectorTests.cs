using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderPackageArchiveInspectorTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    public async Task ExtractAndValidateAsync_AcceptsEachExactRid(string rid)
    {
        var root = CreateTempDirectory();
        var executableName = rid.StartsWith("win-", StringComparison.Ordinal)
            ? "Test.Worker.exe"
            : "Test.Worker";
        var manifest = CreateManifest(
            [CreateTarget(
                SunderPackageFormat.RuntimeHostRole,
                rid,
                SunderPackageFormat.WorkerTargetKind,
                $"lib/{executableName}",
                targetFramework: "net10.0")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                [$"payload/runtime/{rid}/lib/{executableName}"] = [1, 2, 3, 4],
                ["payload/shared/config/defaults.json"] = "{}"u8.ToArray(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(rid, Assert.IsType<SunderPackageTargetManifest>(Assert.Single(result.Manifest!.Targets!)).Rid);
        Assert.NotNull(result.ContentIndex);
        Assert.Equal(
            ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"],
            SunderPackageFormat.SupportedRuntimeIdentifiers);
    }

    [Theory]
    [InlineData("win-x64", "lib/Test.Worker", "Windows RID")]
    [InlineData("linux-x64", "lib/Test.Worker.exe", "extensionless apphost")]
    [InlineData("osx-arm64", "lib/Test.Worker.dll", "extensionless apphost")]
    [InlineData("linux-arm64", "bin/Test.Worker", "directly under 'lib/'")]
    public async Task ExtractAndValidateAsync_RejectsInvalidDotNetWorkerAppHostNameForRid(
        string rid,
        string entryPoint,
        string expectedError)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("runtime", rid, "worker", entryPoint, targetFramework: "net10.0")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                [$"payload/runtime/{rid}/{entryPoint}"] = [1, 2, 3],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsDotNetWorkerAppHostOutsideExactRidLayer()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "lib/Test.Worker", targetFramework: "net10.0")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/lib/Test.Worker"] = [1, 2, 3],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Contains("exact Runtime/RID payload layer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Win-x64")]
    [InlineData("freebsd-x64")]
    public async Task ExtractAndValidateAsync_RejectsWrongCaseAndUnknownRid(string rid)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget(SunderPackageFormat.RuntimeHostRole, rid, SunderPackageFormat.WorkerTargetKind, "worker")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/host.dll"] = [1, 2, 3] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("unknown RID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsWrongCaseRole()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("Runtime", "win-x64", SunderPackageFormat.WorkerTargetKind, "worker")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/host.dll"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("unknown role 'Runtime'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Win-x64")]
    [InlineData("freebsd-x64")]
    public async Task ExtractAndValidateAsync_RejectsWrongCaseAndUnknownPhysicalRid(string rid)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest([CreateTarget("runtime", "win-x64", "worker", "host")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/host"] = [1],
                [$"payload/runtime/{rid}/other"] = [2],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains($"payload/runtime/{rid}/other", StringComparison.Ordinal)
                                                && error.Contains("canonical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsDuplicateTargetKey()
    {
        var root = CreateTempDirectory();
        var target = CreateTarget("app", "win-x64", "avalonia", "app.dll");
        var manifest = CreateManifest([target, target]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/app.dll"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("target 'app/win-x64' more than once", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("app", "dotnet")]
    [InlineData("app", "worker")]
    [InlineData("app", "process")]
    [InlineData("runtime", "avalonia")]
    [InlineData("runtime", "web")]
    [InlineData("runtime", "dotnet")]
    [InlineData("runtime", "process")]
    public async Task ExtractAndValidateAsync_RejectsKindOutsideRole(string role, string kind)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest([CreateTarget(role, "linux-x64", kind, "entry.bin")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/entry.bin"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("invalid for role", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_AcceptsCanonicalWorkerKind()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest([CreateTarget("runtime", "linux-x64", "worker", "bin/worker")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/runtime/linux-x64/bin/worker"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_AcceptsExtendedDeadlineWorkerCapability()
    {
        var root = CreateTempDirectory();
        var target = CreateTarget(
            "runtime",
            "linux-x64",
            "worker",
            "bin/worker",
            requiredHostCapabilities:
            [
                "core.v1",
                "rpc.extended-deadline.v1",
                "rpc.v2",
                "sdk-baseline-1-1.v1",
                "worker-protocol.v3",
            ]);
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([target]),
            new Dictionary<string, byte[]> { ["payload/runtime/linux-x64/bin/worker"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("rpc.v2")]
    [InlineData("worker-protocol.v3")]
    [InlineData("sdk-baseline-1-1.v1")]
    public async Task ExtractAndValidateAsync_RejectsWorkerMissingMandatoryCapability(string capability)
    {
        var root = CreateTempDirectory();
        var capabilities = new[] { "core.v1", "rpc.v2", "sdk-baseline-1-1.v1", "worker-protocol.v3" }
            .Where(value => !string.Equals(value, capability, StringComparison.Ordinal))
            .ToArray();
        var target = CreateTarget(
            "runtime",
            "linux-x64",
            "worker",
            "bin/worker",
            requiredHostCapabilities: capabilities);
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([target]),
            new Dictionary<string, byte[]> { ["payload/runtime/linux-x64/bin/worker"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(
            result.Errors,
            error => error.Contains($"must require Host capability '{capability}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsWorkerBaselineMismatchedToSdkVersion()
    {
        var root = CreateTempDirectory();
        var target = CreateTarget("runtime", "linux-x64", "worker", "bin/worker", sdkVersion: "1.2.0");
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([target]),
            new Dictionary<string, byte[]> { ["payload/runtime/linux-x64/bin/worker"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(
            result.Errors,
            error => error.Contains("SDK baseline capability 'sdk-baseline-1-1.v1' does not match sdkVersion '1.2.0'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsWorkerWithoutSdkVersion()
    {
        var root = CreateTempDirectory();
        var target = CreateTarget(
            "runtime",
            "linux-x64",
            "worker",
            "bin/worker",
            includeSdkVersion: false);
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([target]),
            new Dictionary<string, byte[]> { ["payload/runtime/linux-x64/bin/worker"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(
            result.Errors,
            error => error.Contains("must declare sdkVersion", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("rpc.v1")]
    [InlineData("worker-protocol.v1")]
    public async Task ExtractAndValidateAsync_RejectsLegacyWorkerCapability(string capability)
    {
        var root = CreateTempDirectory();
        var target = CreateTarget(
            "runtime",
            "linux-x64",
            "worker",
            "bin/worker",
            requiredHostCapabilities:
            [
                "core.v1",
                "rpc.v2",
                "sdk-baseline-1-1.v1",
                "worker-protocol.v3",
                capability,
            ]);
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([target]),
            new Dictionary<string, byte[]> { ["payload/runtime/linux-x64/bin/worker"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(
            result.Errors,
            error => error.Contains("cannot require unsupported", StringComparison.Ordinal)
                     && error.Contains($"'{capability}'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("process")]
    public async Task ExtractAndValidateAsync_RejectsLegacyRuntimeKinds(string kind)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest([CreateTarget("runtime", "linux-x64", kind, "bin/worker")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/runtime/linux-x64/bin/worker"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("invalid for role 'runtime'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsOldPayloadLibAndAssetsLayout()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/host"] = [1],
                ["payload/lib/legacy.dll"] = [2],
                ["payload/assets/legacy.png"] = TinyPng(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("payload/lib/legacy.dll", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("payload/assets/legacy.png", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("payload/shared/tool.dll", "payload/runtime/shared/tool.dll", "duplicate")]
    [InlineData("payload/shared/Tool.dll", "payload/runtime/shared/tool.dll", "case")]
    [InlineData("payload/shared/lib", "payload/runtime/shared/lib/tool.dll", "collides")]
    public async Task ExtractAndValidateAsync_RejectsTargetUnionCollisions(
        string firstPath,
        string secondPath,
        string expectedError)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "tool.dll")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                [firstPath] = [1],
                [secondPath] = [2],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("invalid payload union", StringComparison.Ordinal)
                                                && error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_ResolvesEntryPointsFromAllSelectedLayersOnly()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [
                CreateTarget("app", "win-x64", "avalonia", "shared-app.dll"),
                CreateTarget("runtime", "win-x64", "worker", "runtime.dll"),
                CreateTarget("runtime", "linux-x64", "worker", "bin/worker"),
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/shared-app.dll"] = [1],
                ["payload/runtime/shared/runtime.dll"] = [2],
                ["payload/runtime/linux-x64/bin/worker"] = [3],
                ["payload/runtime/win-x64/other.dll"] = [4],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("missing", "does not resolve")]
    public async Task ExtractAndValidateAsync_RejectsMissingWorkerEntryPoint(
        string entryPoint,
        string expectedError)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", entryPoint)]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/runtime/linux-x64/actual.exe"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_AcceptsSharedOnlyContractPackage()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.messaging", "1.0.0");
        var manifest = CreateManifest(
            [],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "example.messaging",
                    Version = "1.0.0",
                    DescriptorPath = "contracts/messaging.json",
                    DescriptorSha256 = DescriptorHash(descriptor),
                    AbiSha256 = AbiHash(descriptor),
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/contracts/messaging.json"] = descriptor });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Manifest!.Targets!);
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsV1DescriptorInV2ContractBundle()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateV1ContractDescriptor("example.contract", "1.0.0");
        var parsedDescriptor = SunderRpcContractDescriptor.Parse(descriptor);
        var manifest = CreateManifest(
            [],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "example.contract",
                    Version = "1.0.0",
                    DescriptorPath = "contracts/example.json",
                    DescriptorSha256 = parsedDescriptor.Sha256,
                    AbiSha256 = new string('0', 64),
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/contracts/example.json"] = descriptor });

        Assert.Equal(SunderRpcProtocol.DescriptorVersion, parsedDescriptor.DescriptorVersion);
        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Contains("descriptorVersion 1", StringComparison.Ordinal)
            && error.Contains("package manifest V3 supports only descriptorVersion 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsEmptyTargetsWithoutContractBundle()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, CreateManifest([]), new Dictionary<string, byte[]>());

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("at least one target or contract bundle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_ValidatesContractUsesAndProviders()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.events", "1.2.0");
        var descriptorHash = DescriptorHash(descriptor);
        var abiHash = AbiHash(descriptor);
        var manifest = CreateManifest(
            [
                CreateTarget("app", "osx-arm64", "web", "index.html"),
                CreateTarget("runtime", "osx-arm64", "worker", "host"),
            ],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "example.events",
                    Version = "1.2.0",
                    DescriptorPath = "contracts/events.json",
                    DescriptorSha256 = descriptorHash,
                    AbiSha256 = abiHash,
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    Role = "app",
                    ContractId = "example.events",
                    ContractVersion = "1.2.0",
                    AbiSha256 = abiHash,
                    Required = true,
                    Actions = ["discover", "invoke", "subscribe"],
                },
            ],
            provides:
            [
                new SunderPackageProviderManifest
                {
                    ProviderId = "example.events.web",
                    Role = "runtime",
                    ContractId = "example.events",
                    ContractVersion = "1.2.0",
                    DescriptorSha256 = descriptorHash,
                    AbiSha256 = abiHash,
                    Audience = SunderPackageFormat.PublicProviderAudience,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/events.json"] = descriptor,
                ["payload/app/osx-arm64/index.html"] = "<html></html>"u8.ToArray(),
                ["payload/runtime/osx-arm64/host"] = [1],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsUnsupportedAppProvider()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.app.provider", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var manifest = CreateManifest(
            [CreateTarget("app", "osx-arm64", "web", "index.html")],
            contractBundles: [CreateContractBundle(descriptor, "contracts/app-provider.json")],
            provides:
            [
                new SunderPackageProviderManifest
                {
                    ProviderId = "example.app.provider",
                    Role = "app",
                    ContractId = parsed.ContractId,
                    ContractVersion = parsed.Version,
                    DescriptorSha256 = parsed.Sha256,
                    AbiSha256 = parsed.AbiSha256,
                    Audience = SunderPackageFormat.OwnerProviderAudience,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/app-provider.json"] = descriptor,
                ["payload/app/osx-arm64/index.html"] = "<html></html>"u8.ToArray(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "app-provider"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Contains("App-role providers are not supported", StringComparison.Ordinal)
            && error.Contains("registration and serving are not available", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Discover")]
    [InlineData("publish")]
    public async Task ExtractAndValidateAsync_RejectsUnknownOrWrongCaseContractAction(string action)
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.contract", "1.0.0");
        var manifest = CreateManifest(
            [CreateTarget("runtime", "win-x64", "worker", "host")],
            contractBundles: [CreateContractBundle(descriptor, "contract.json")],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    Role = "runtime",
                    ContractId = "example.contract",
                    ContractVersion = "1.0.0",
                    AbiSha256 = AbiHash(descriptor),
                    Required = false,
                    Actions = [action],
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contract.json"] = descriptor,
                ["payload/shared/host"] = [1],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("unknown action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RequiresContractDescriptorPhysicallyInSharedLayer()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.contract", "1.0.0");
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "example.contract",
                    Version = "1.0.0",
                    DescriptorPath = "contract.json",
                    DescriptorSha256 = DescriptorHash(descriptor),
                    AbiSha256 = AbiHash(descriptor),
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/host"] = [1],
                ["payload/runtime/shared/contract.json"] = descriptor,
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("payload/shared/contract.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsContractDescriptorHashMismatch()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.contract", "1.0.0");
        var manifest = CreateManifest(
            [],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "example.contract",
                    Version = "1.0.0",
                    DescriptorPath = "contract.json",
                    DescriptorSha256 = new string('0', 64),
                    AbiSha256 = AbiHash(descriptor),
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/contract.json"] = descriptor });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Contract descriptor 'contract.json' descriptorSha256 mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsInvalidDescriptorAndIdentityMismatch()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("other.contract", "1.0.0");
        var manifest = CreateManifest(
            [],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "example.contract",
                    Version = "1.0.0",
                    DescriptorPath = "contract.json",
                    DescriptorSha256 = DescriptorHash(descriptor),
                    AbiSha256 = AbiHash(descriptor),
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/contract.json"] = descriptor });

        var identityResult = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "identity-staging"));
        Assert.False(identityResult.Success);
        Assert.Contains(identityResult.Errors, error => error.Contains("does not match bundle contractId", StringComparison.Ordinal));

        var invalid = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(descriptor).Replace(
            "\"required\": [], \"additionalProperties\": false",
            "\"required\": [], \"pattern\": \".*\", \"additionalProperties\": false",
            StringComparison.Ordinal));
        manifest = CreateManifest(
            [],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "other.contract",
                    Version = "1.0.0",
                    DescriptorPath = "invalid.json",
                    DescriptorSha256 = new string('0', 64),
                    AbiSha256 = new string('0', 64),
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ]);
        archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/invalid.json"] = invalid });
        var invalidResult = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "invalid-staging"));
        Assert.False(invalidResult.Success);
        Assert.Contains(invalidResult.Errors, error => error.Contains("unsupported property 'pattern'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RequiresLocalBundleForEveryImportAndProvider()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    Role = "runtime",
                    ContractId = "example.missing",
                    ContractVersion = "1.0.0",
                    AbiSha256 = new string('0', 64),
                    Required = false,
                    Actions = ["discover"],
                },
            ],
            provides:
            [
                new SunderPackageProviderManifest
                {
                    ProviderId = "example.provider",
                    Role = "runtime",
                    ContractId = "example.missing",
                    ContractVersion = "1.0.0",
                    DescriptorSha256 = new string('0', 64),
                    AbiSha256 = new string('0', 64),
                    Audience = SunderPackageFormat.OwnerProviderAudience,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/runtime/linux-x64/host"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Contract use 'example.missing'", StringComparison.Ordinal)
                                                       && error.Contains("local contract bundle", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Provider 'example.provider'", StringComparison.Ordinal)
                                                       && error.Contains("local contract bundle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_ValidatesDescriptorAndContentIndexHashesIndependently()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.hashes", "1.0.0");
        var bundle = CreateContractBundle(descriptor, "contracts/hashes.json");
        var manifest = CreateManifest([], contractBundles: [bundle]);

        Assert.NotEqual(Sha256(descriptor), bundle.DescriptorSha256);
        var validArchive = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/contracts/hashes.json"] = descriptor });
        var validResult = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            validArchive,
            Path.Combine(root, "canonical-descriptor-hash"));
        Assert.True(validResult.Success, string.Join(Environment.NewLine, validResult.Errors));

        var invalidArchive = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/contracts/hashes.json"] = descriptor },
            index => index with
            {
                Files = index.Files!
                    .Select(static entry => entry.Path == "payload/shared/contracts/hashes.json"
                        ? entry with { Sha256 = new string('0', 64) }
                        : entry)
                    .ToArray(),
            });
        var invalidResult = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            invalidArchive,
            Path.Combine(root, "content-index-hash"));

        Assert.False(invalidResult.Success);
        Assert.Contains(invalidResult.Errors, error =>
            error.Contains("Package file 'payload/shared/contracts/hashes.json' SHA-256 mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(invalidResult.Errors, error =>
            error.Contains("descriptorSha256 mismatch", StringComparison.Ordinal)
            || error.Contains("abiSha256 mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsAbiHashWithoutReportingDescriptorHashMismatch()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.hashes", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var manifest = CreateManifest(
            [],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = parsed.ContractId,
                    Version = parsed.Version,
                    DescriptorPath = "contracts/hashes.json",
                    DescriptorSha256 = parsed.Sha256,
                    AbiSha256 = new string('0', 64),
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/contracts/hashes.json"] = descriptor });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "abi-hash"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("abiSha256 mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains("descriptorSha256 mismatch", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("descriptorSha256")]
    [InlineData("abiSha256")]
    public async Task ExtractAndValidateAsync_RejectsProviderHashesIndependently(string hashField)
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.provider.hashes", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")],
            contractBundles: [CreateContractBundle(descriptor, "contracts/provider.json")],
            provides:
            [
                new SunderPackageProviderManifest
                {
                    ProviderId = "example.provider",
                    Role = "runtime",
                    ContractId = parsed.ContractId,
                    ContractVersion = parsed.Version,
                    DescriptorSha256 = hashField == "descriptorSha256" ? new string('0', 64) : parsed.Sha256,
                    AbiSha256 = hashField == "abiSha256" ? new string('0', 64) : parsed.AbiSha256,
                    Audience = SunderPackageFormat.OwnerProviderAudience,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/provider.json"] = descriptor,
                ["payload/runtime/linux-x64/host"] = [1],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "provider-" + hashField));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains($"Provider 'example.provider' {hashField}", StringComparison.Ordinal));
        var otherHash = hashField == "descriptorSha256" ? "abiSha256" : "descriptorSha256";
        Assert.DoesNotContain(result.Errors, error => error.Contains($"Provider 'example.provider' {otherHash}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_UsesCompleteSemVerIdentityIncludingBuildMetadata()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.metadata", "1.0.0+linux");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var mismatchedBundle = new SunderPackageContractBundleManifest
        {
            ContractId = parsed.ContractId,
            Version = "1.0.0+windows",
            DescriptorPath = "contracts/identity.json",
            DescriptorSha256 = parsed.Sha256,
            AbiSha256 = parsed.AbiSha256,
            Exposure = SunderPackageFormat.PublicContractExposure,
        };
        var identityArchive = CreatePackageArchive(
            root,
            CreateManifest([], contractBundles: [mismatchedBundle]),
            new Dictionary<string, byte[]> { ["payload/shared/contracts/identity.json"] = descriptor });
        var identityResult = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            identityArchive,
            Path.Combine(root, "metadata-identity"));
        Assert.Contains(identityResult.Errors, error =>
            error.Contains("version '1.0.0+linux' does not match bundle version '1.0.0+windows'", StringComparison.Ordinal));

        var exactBundle = CreateContractBundle(descriptor, "contracts/lock.json");
        var lockManifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")],
            contractBundles: [exactBundle],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    Role = "runtime",
                    ContractId = parsed.ContractId,
                    ContractVersion = "1.0.0+windows",
                    AbiSha256 = parsed.AbiSha256,
                    Required = true,
                    Actions = ["invoke"],
                },
            ],
            provides:
            [
                new SunderPackageProviderManifest
                {
                    ProviderId = "example.metadata.provider",
                    Role = "runtime",
                    ContractId = parsed.ContractId,
                    ContractVersion = "1.0.0+windows",
                    DescriptorSha256 = parsed.Sha256,
                    AbiSha256 = parsed.AbiSha256,
                    Audience = SunderPackageFormat.OwnerProviderAudience,
                },
            ]);
        var lockArchive = CreatePackageArchive(
            root,
            lockManifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/lock.json"] = descriptor,
                ["payload/runtime/linux-x64/host"] = [1],
            });
        var lockResult = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            lockArchive,
            Path.Combine(root, "metadata-lock"));

        Assert.False(lockResult.Success);
        Assert.Contains(lockResult.Errors, error =>
            error.Contains("Contract use 'example.metadata' requires an exact matching", StringComparison.Ordinal));
        Assert.Contains(lockResult.Errors, error =>
            error.Contains("Provider 'example.metadata.provider' requires an exact matching", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bundle-contract-id", "Contract id")]
    [InlineData("bundle-version", "must be strict SemVer")]
    [InlineData("bundle-descriptor-hash", "lowercase descriptorSha256")]
    [InlineData("bundle-abi-hash", "lowercase abiSha256")]
    [InlineData("bundle-exposure", "invalid exposure")]
    [InlineData("use-role", "declares unknown role")]
    [InlineData("use-contract-id", "Used contract id")]
    [InlineData("use-version", "contractVersion '>=1.0.0'")]
    [InlineData("use-abi-hash", "must declare a lowercase abiSha256")]
    [InlineData("provider-id", "Provider id")]
    [InlineData("provider-role", "declares unknown role")]
    [InlineData("provider-contract-id", "Provider contract id")]
    [InlineData("provider-version", "contractVersion 'latest'")]
    [InlineData("provider-descriptor-hash", "lowercase descriptorSha256")]
    [InlineData("provider-abi-hash", "lowercase abiSha256")]
    [InlineData("provider-audience", "invalid audience")]
    public async Task ExtractAndValidateAsync_RejectsNonCanonicalRpcMetadata(
        string invalidField,
        string expectedError)
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.lexical", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var invalidHash = "A" + parsed.Sha256[1..];
        var bundle = new SunderPackageContractBundleManifest
        {
            ContractId = invalidField == "bundle-contract-id" ? "Example.lexical" : parsed.ContractId,
            Version = invalidField == "bundle-version" ? "01.0.0" : parsed.Version,
            DescriptorPath = "contracts/lexical.json",
            DescriptorSha256 = invalidField == "bundle-descriptor-hash" ? invalidHash : parsed.Sha256,
            AbiSha256 = invalidField == "bundle-abi-hash" ? invalidHash : parsed.AbiSha256,
            Exposure = invalidField == "bundle-exposure" ? "Public" : SunderPackageFormat.PublicContractExposure,
        };
        var use = new SunderPackageContractUseManifest
        {
            Role = invalidField == "use-role" ? "Runtime" : "runtime",
            ContractId = invalidField == "use-contract-id" ? "example-contract" : parsed.ContractId,
            ContractVersion = invalidField == "use-version" ? ">=1.0.0" : parsed.Version,
            AbiSha256 = invalidField == "use-abi-hash" ? invalidHash : parsed.AbiSha256,
            Required = true,
            Actions = ["invoke"],
        };
        var provider = new SunderPackageProviderManifest
        {
            ProviderId = invalidField == "provider-id" ? "example-provider" : "example.provider",
            Role = invalidField == "provider-role" ? "Runtime" : "runtime",
            ContractId = invalidField == "provider-contract-id" ? "Example.lexical" : parsed.ContractId,
            ContractVersion = invalidField == "provider-version" ? "latest" : parsed.Version,
            DescriptorSha256 = invalidField == "provider-descriptor-hash" ? invalidHash : parsed.Sha256,
            AbiSha256 = invalidField == "provider-abi-hash" ? invalidHash : parsed.AbiSha256,
            Audience = invalidField == "provider-audience" ? "Public" : SunderPackageFormat.OwnerProviderAudience,
        };
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")],
            contractBundles: [bundle],
            usesContracts: [use],
            provides: [provider]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/lexical.json"] = descriptor,
                ["payload/runtime/linux-x64/host"] = [1],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "lexical-" + invalidField));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    public async Task ExtractAndValidateAsync_RequiresNonemptyDistinctContractActions(string actionCase)
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.actions", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        IReadOnlyList<string?> actions = actionCase == "empty" ? [] : ["invoke", "invoke"];
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")],
            contractBundles: [CreateContractBundle(descriptor, "contracts/actions.json")],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    Role = "runtime",
                    ContractId = parsed.ContractId,
                    ContractVersion = parsed.Version,
                    AbiSha256 = parsed.AbiSha256,
                    Required = false,
                    Actions = actions,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/actions.json"] = descriptor,
                ["payload/runtime/linux-x64/host"] = [1],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "actions-" + actionCase));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(
            actionCase == "empty" ? "at least one action" : "action 'invoke' more than once",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("use")]
    [InlineData("provider")]
    public async Task ExtractAndValidateAsync_RequiresRoleTargetForUsesAndProviders(string declaration)
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.target", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var use = new SunderPackageContractUseManifest
        {
            Role = "app",
            ContractId = parsed.ContractId,
            ContractVersion = parsed.Version,
            AbiSha256 = parsed.AbiSha256,
            Required = false,
            Actions = ["discover"],
        };
        var provider = new SunderPackageProviderManifest
        {
            ProviderId = "example.target.provider",
            Role = "runtime",
            ContractId = parsed.ContractId,
            ContractVersion = parsed.Version,
            DescriptorSha256 = parsed.Sha256,
            AbiSha256 = parsed.AbiSha256,
            Audience = SunderPackageFormat.OwnerProviderAudience,
        };
        var missingRole = declaration == "use" ? "app" : "runtime";
        var target = declaration == "use"
            ? CreateTarget("runtime", "linux-x64", "worker", "host")
            : CreateTarget("app", "linux-x64", "web", "index.html");
        var manifest = CreateManifest(
            [target],
            contractBundles: [CreateContractBundle(descriptor, "contracts/target.json")],
            usesContracts: declaration == "use" ? [use] : null,
            provides: declaration == "provider" ? [provider] : null);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/target.json"] = descriptor,
                [declaration == "use"
                    ? "payload/runtime/linux-x64/host"
                    : "payload/app/linux-x64/index.html"] = [1],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "target-" + declaration));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Contains($"role '{missingRole}' has no package target", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("package", "owner", true)]
    [InlineData("package", "public", false)]
    [InlineData("public", "owner", true)]
    [InlineData("public", "public", true)]
    public async Task ExtractAndValidateAsync_EnforcesExposureAudiencePolicy(
        string exposure,
        string audience,
        bool expectedSuccess)
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.policy", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")],
            contractBundles: [CreateContractBundle(descriptor, "contracts/policy.json", exposure)],
            provides:
            [
                new SunderPackageProviderManifest
                {
                    ProviderId = "example.policy.provider",
                    Role = "runtime",
                    ContractId = parsed.ContractId,
                    ContractVersion = parsed.Version,
                    DescriptorSha256 = parsed.Sha256,
                    AbiSha256 = parsed.AbiSha256,
                    Audience = audience,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/policy.json"] = descriptor,
                ["payload/runtime/linux-x64/host"] = [1],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, $"policy-{exposure}-{audience}"));

        Assert.Equal(expectedSuccess, result.Success);
        if (!expectedSuccess)
        {
            Assert.Contains(result.Errors, error =>
                error.Contains("public audience requires public contract exposure", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsDuplicateBundleUseAndProviderKeys()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.duplicates", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var firstBundle = CreateContractBundle(descriptor, "contracts/first.json");
        var secondBundle = CreateContractBundle(descriptor, "contracts/second.json");
        var use = new SunderPackageContractUseManifest
        {
            Role = "runtime",
            ContractId = parsed.ContractId,
            ContractVersion = parsed.Version,
            AbiSha256 = parsed.AbiSha256,
            Required = false,
            Actions = ["invoke"],
        };
        var provider = new SunderPackageProviderManifest
        {
            ProviderId = "example.duplicate.provider",
            Role = "runtime",
            ContractId = parsed.ContractId,
            ContractVersion = parsed.Version,
            DescriptorSha256 = parsed.Sha256,
            AbiSha256 = parsed.AbiSha256,
            Audience = SunderPackageFormat.OwnerProviderAudience,
        };
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")],
            contractBundles: [firstBundle, secondBundle],
            usesContracts: [use, use],
            provides: [provider, provider]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/first.json"] = descriptor,
                ["payload/shared/contracts/second.json"] = descriptor,
                ["payload/runtime/linux-x64/host"] = [1],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "duplicate-keys"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Contract bundle 'example.duplicates' version '1.0.0' is declared more than once", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Contract use 'example.duplicates' is declared more than once for role 'runtime'", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Provider id 'example.duplicate.provider' is declared more than once for role 'runtime'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsNonPortableDuplicateDescriptorPaths()
    {
        var root = CreateTempDirectory();
        var firstDescriptor = CreateContractDescriptor("example.first", "1.0.0");
        var secondDescriptor = CreateContractDescriptor("example.second", "1.0.0");
        var manifest = CreateManifest(
            [],
            contractBundles:
            [
                CreateContractBundle(firstDescriptor, "contracts/example.json"),
                CreateContractBundle(secondDescriptor, "contracts/Example.json"),
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/contracts/example.json"] = firstDescriptor });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "duplicate-paths"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error =>
            error.Contains("descriptorPath", StringComparison.Ordinal)
            && error.Contains("differ only by case", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_AllowsRoleScopedUsesAndRuntimeProvider()
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.roles", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var uses = new[] { "app", "runtime" }
            .Select(role => new SunderPackageContractUseManifest
            {
                Role = role,
                ContractId = parsed.ContractId,
                ContractVersion = parsed.Version,
                AbiSha256 = parsed.AbiSha256,
                Required = false,
                Actions = ["discover"],
            })
            .ToArray();
        var provider = new SunderPackageProviderManifest
        {
            ProviderId = "example.shared.provider",
            Role = "runtime",
            ContractId = parsed.ContractId,
            ContractVersion = parsed.Version,
            DescriptorSha256 = parsed.Sha256,
            AbiSha256 = parsed.AbiSha256,
            Audience = SunderPackageFormat.OwnerProviderAudience,
        };
        var manifest = CreateManifest(
            [
                CreateTarget("app", "linux-x64", "web", "index.html"),
                CreateTarget("runtime", "linux-x64", "worker", "host"),
            ],
            contractBundles: [CreateContractBundle(descriptor, "contracts/roles.json")],
            usesContracts: uses,
            provides: [provider]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/roles.json"] = descriptor,
                ["payload/app/linux-x64/index.html"] = "<html></html>"u8.ToArray(),
                ["payload/runtime/linux-x64/host"] = [1],
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "role-scoped-keys"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("versionRange")]
    [InlineData("contractSha256")]
    public async Task ValidateExtractedPackageAsync_RejectsV1RpcFieldNames(string oldField)
    {
        var root = CreateTempDirectory();
        var descriptor = CreateContractDescriptor("example.oldfields", "1.0.0");
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "worker", "host")],
            contractBundles: [CreateContractBundle(descriptor, "contracts/old-fields.json")],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    Role = "runtime",
                    ContractId = parsed.ContractId,
                    ContractVersion = parsed.Version,
                    AbiSha256 = parsed.AbiSha256,
                    Required = false,
                    Actions = ["invoke"],
                },
            ],
            provides:
            [
                new SunderPackageProviderManifest
                {
                    ProviderId = "example.oldfields.provider",
                    Role = "runtime",
                    ContractId = parsed.ContractId,
                    ContractVersion = parsed.Version,
                    DescriptorSha256 = parsed.Sha256,
                    AbiSha256 = parsed.AbiSha256,
                    Audience = SunderPackageFormat.OwnerProviderAudience,
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/old-fields.json"] = descriptor,
                ["payload/runtime/linux-x64/host"] = [1],
            });
        var staging = Path.Combine(root, "old-field-" + oldField);
        SunderPackageArchiveInspector.ExtractArchive(archivePath, staging);
        var manifestPath = Path.Combine(staging, "manifest", "sunder-package.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var declaration = oldField switch
        {
            "sha256" => json["contractBundles"]![0]!.AsObject(),
            "versionRange" => json["usesContracts"]![0]!.AsObject(),
            _ => json["provides"]![0]!.AsObject(),
        };
        declaration[oldField] = oldField == "versionRange" ? "1.0.0" : new string('0', 64);
        await File.WriteAllTextAsync(manifestPath, json.ToJsonString());

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Failed to parse package metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RequiresIconPhysicallyInSharedLayer()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("app", "win-x64", "web", "index.html")],
            icon: "icon.png");
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/app/shared/index.html"] = "<html></html>"u8.ToArray(),
                ["payload/app/shared/icon.png"] = TinyPng(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("payload/shared/icon.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_AcceptsBoundedSharedIcon()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("app", "win-x64", "web", "index.html")],
            icon: "images/icon.png");
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/images/icon.png"] = TinyPng(),
                ["payload/app/win-x64/index.html"] = "<html></html>"u8.ToArray(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("icon.png", false)]
    [InlineData("icon.jpg", true)]
    public async Task ExtractAndValidateAsync_RejectsInvalidIconSignatureOrExtension(
        string iconPath,
        bool usePngBytes)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("app", "win-x64", "web", "index.html")],
            icon: iconPath);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                [$"payload/shared/{iconPath}"] = usePngBytes ? TinyPng() : "not-an-image"u8.ToArray(),
                ["payload/app/win-x64/index.html"] = "<html></html>"u8.ToArray(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(usePngBytes ? "extension" : "signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsOversizedIconBeforeInspection()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("app", "win-x64", "web", "index.html")],
            icon: "icon.png");
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/icon.png"] = RandomNumberGenerator.GetBytes(checked((int)SunderPackageFormat.MaxIconBytes + 1)),
                ["payload/app/win-x64/index.html"] = "<html></html>"u8.ToArray(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("1 MiB", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsOverflowingBmpDimensionsWithoutThrowing()
    {
        var bytes = new byte[26];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(1).CopyTo(bytes, 18);
        BitConverter.GetBytes(int.MinValue).CopyTo(bytes, 22);
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("app", "win-x64", "web", "index.html")],
            icon: "icon.bmp");
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/icon.bmp"] = bytes,
                ["payload/app/win-x64/index.html"] = "<html></html>"u8.ToArray(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_ContentIndexHasExactCoverageWithoutRole()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest([CreateTarget("runtime", "win-x64", "worker", "host")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/host"] = [1, 2, 3] });
        var staging = Path.Combine(root, "staging");

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, staging);
        var indexJson = await File.ReadAllTextAsync(Path.Combine(staging, "manifest", "content-index.json"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.DoesNotContain("\"role\"", indexJson, StringComparison.Ordinal);
        var index = JsonSerializer.Deserialize<SunderPackageContentIndex>(indexJson)!;
        Assert.Equal(
            [SunderPackageFormat.ManifestPath, "payload/shared/host"],
            index.Files!.Select(static entry => entry.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ValidateExtractedPackageAsync_RequiresExactContentIndexPathCase()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([CreateTarget("runtime", "win-x64", "worker", "host")]),
            new Dictionary<string, byte[]> { ["payload/shared/host"] = [1] });
        var staging = Path.Combine(root, "staging");
        SunderPackageArchiveInspector.ExtractArchive(archivePath, staging);
        var indexPath = Path.Combine(staging, "manifest", "content-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        var hostEntry = index["files"]!.AsArray().Single(node => node!["path"]!.GetValue<string>() == "payload/shared/host")!;
        hostEntry["path"] = "payload/shared/Host";
        await File.WriteAllTextAsync(indexPath, index.ToJsonString());

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("missing file 'payload/shared/Host'", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("unindexed file 'payload/shared/host'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateExtractedPackageAsync_RejectsLegacyContentIndexRoleMember()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([CreateTarget("runtime", "win-x64", "worker", "host")]),
            new Dictionary<string, byte[]> { ["payload/shared/host"] = [1] });
        var staging = Path.Combine(root, "staging");
        SunderPackageArchiveInspector.ExtractArchive(archivePath, staging);
        var indexPath = Path.Combine(staging, "manifest", "content-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["files"]![0]!.AsObject()["role"] = "manifest";
        await File.WriteAllTextAsync(indexPath, index.ToJsonString());

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Failed to parse package metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateExtractedPackageAsync_RequiresContentIndexSizeMember()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([CreateTarget("runtime", "win-x64", "worker", "host")]),
            new Dictionary<string, byte[]> { ["payload/shared/host"] = [] });
        var staging = Path.Combine(root, "staging");
        SunderPackageArchiveInspector.ExtractArchive(archivePath, staging);
        var indexPath = Path.Combine(staging, "manifest", "content-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        var hostEntry = index["files"]!.AsArray().Single(node => node!["path"]!.GetValue<string>() == "payload/shared/host")!.AsObject();
        hostEntry.Remove("size");
        await File.WriteAllTextAsync(indexPath, index.ToJsonString());

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Failed to parse package metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_RejectsContentHashMismatchAndUnindexedFile()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([CreateTarget("runtime", "win-x64", "worker", "host")]),
            new Dictionary<string, byte[]>
            {
                ["payload/shared/host"] = [1],
                ["payload/shared/unindexed.txt"] = [2],
            },
            index => index with
            {
                Files = index.Files!
                    .Where(static entry => entry.Path != "payload/shared/unindexed.txt")
                    .Select(static entry => entry.Path == "payload/shared/host"
                        ? entry with { Sha256 = new string('0', 64) }
                        : entry)
                    .ToArray(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("unindexed file 'payload/shared/unindexed.txt'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("entryAssembly")]
    [InlineData("hostRoles")]
    [InlineData("sdkApiVersion")]
    [InlineData("sdkPackageVersion")]
    [InlineData("requiredSdkCapabilities")]
    [InlineData("targetFramework")]
    public async Task ValidateExtractedPackageAsync_RejectsRemovedManifestMembers(string propertyName)
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([CreateTarget("runtime", "win-x64", "worker", "host")]),
            new Dictionary<string, byte[]> { ["payload/shared/host"] = [1] });
        var staging = Path.Combine(root, "staging");
        SunderPackageArchiveInspector.ExtractArchive(archivePath, staging);
        var manifestPath = Path.Combine(staging, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest[propertyName] = propertyName == "sdkApiVersion" ? 1 : "legacy";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Failed to parse package metadata", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("archiveFormatVersion", 2, 1)]
    [InlineData("manifestVersion", 1, 3)]
    [InlineData("manifestVersion", 2, 3)]
    [InlineData("manifestVersion", 4, 3)]
    public async Task ExtractAndValidateAsync_RequiresArchiveOneAndManifestThree(
        string propertyName,
        int value,
        int expected)
    {
        var root = CreateTempDirectory();
        var source = CreateManifest([CreateTarget("runtime", "win-x64", "worker", "host")]);
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = propertyName == "archiveFormatVersion" ? value : source.ArchiveFormatVersion,
            ManifestVersion = propertyName == "manifestVersion" ? value : source.ManifestVersion,
            Id = source.Id,
            Name = source.Name,
            Version = source.Version,
            Targets = source.Targets,
        };
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/host"] = [1] });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains($"{propertyName} {expected}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateExtractedPackageAsync_RejectsDuplicateJsonProperties()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([CreateTarget("runtime", "win-x64", "worker", "host")]),
            new Dictionary<string, byte[]> { ["payload/shared/host"] = [1] });
        var staging = Path.Combine(root, "staging");
        SunderPackageArchiveInspector.ExtractArchive(archivePath, staging);
        var manifestPath = Path.Combine(staging, "manifest", "sunder-package.json");
        var json = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, json.Insert(1, "\"id\":\"duplicate.package\","));

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Duplicate JSON property 'id'", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetResolver_EnumeratesExactTargetsAndMapsSelectedLayers()
    {
        var manifest = CreateManifest(
            [
                CreateTarget("runtime", "win-x64", "worker", "bin/host"),
                CreateTarget("app", "linux-x64", "web", "index.html"),
            ]);
        var index = new SunderPackageContentIndex(
            1,
            [
                IndexEntry("manifest/sunder-package.json"),
                IndexEntry("payload/shared/config.json"),
                IndexEntry("payload/runtime/shared/common.txt"),
                IndexEntry("payload/runtime/win-x64/bin/host"),
                IndexEntry("payload/runtime/linux-x64/other"),
                IndexEntry("payload/app/win-x64/not-selected"),
            ]);
        var key = new SunderPackageTargetKey("runtime", "win-x64");

        var keys = SunderPackageTargetResolver.EnumerateTargets(manifest);
        var plan = SunderPackageTargetResolver.CreateProjectionPlan(manifest, index, key);

        Assert.Equal([key, new SunderPackageTargetKey("app", "linux-x64")], keys);
        Assert.Equal(
            ["config.json", "common.txt", "bin/host"],
            plan.Files.Select(static file => file.LogicalPath.ToString()));
        Assert.Equal(
            [SunderPackagePayloadLayer.Shared, SunderPackagePayloadLayer.RoleShared, SunderPackagePayloadLayer.Target],
            plan.Files.Select(static file => file.Layer));
        Assert.True(plan.TryResolveLogicalPath(ArchiveRelativePath.Parse("bin/host"), out var physical));
        Assert.Equal("payload/runtime/win-x64/bin/host", physical.ToString());
        Assert.False(SunderPackageTargetResolver.TryResolveTarget(
            manifest,
            new SunderPackageTargetKey("runtime", "linux-x64"),
            out _));
    }

    [Fact]
    public void TargetResolver_HasNoOverlayPrecedence()
    {
        var index = new SunderPackageContentIndex(
            1,
            [
                IndexEntry("payload/shared/file.txt"),
                IndexEntry("payload/runtime/win-x64/file.txt"),
            ]);

        var error = Assert.Throws<InvalidDataException>(() =>
            SunderPackageTargetResolver.CreateProjectionPlan(
                index,
                new SunderPackageTargetKey("runtime", "win-x64")));

        Assert.Contains("duplicate path 'file.txt'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetResolver_RejectsDefaultKey()
    {
        var index = new SunderPackageContentIndex(1, [IndexEntry("payload/shared/file.txt")]);

        Assert.Throws<ArgumentException>(() =>
            SunderPackageTargetResolver.CreateProjectionPlan(index, default));
    }

    [Fact]
    public async Task PackageArchiveWriter_ProducesByteIdenticalCanonicalArchives()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        var manifest = CreateManifest([CreateTarget("runtime", "linux-arm64", "worker", "host")]);
        WritePackageSource(
            source,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/z.txt"] = "z"u8.ToArray(),
                ["payload/runtime/linux-arm64/host"] = [1, 2, 3],
            });
        var first = Path.Combine(root, "first.sunderpkg");
        var second = Path.Combine(root, "second.sunderpkg");

        await SunderArchive.WriteDeterministicAsync(source, first);
        File.SetLastWriteTimeUtc(Path.Combine(source, "payload", "shared", "z.txt"), DateTime.UtcNow.AddDays(1));
        await SunderArchive.WriteDeterministicAsync(source, second);

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        using var archive = ZipFile.OpenRead(first);
        Assert.Equal(
            archive.Entries.Select(static entry => entry.FullName).Order(StringComparer.Ordinal),
            archive.Entries.Select(static entry => entry.FullName));
    }

    [Fact]
    public void ManifestModel_DoesNotExposeRemovedV1Properties()
    {
        var properties = typeof(SunderPackageManifest).GetProperties().Select(static property => property.Name).ToHashSet();

        Assert.DoesNotContain("EntryAssembly", properties);
        Assert.DoesNotContain("HostRoles", properties);
        Assert.DoesNotContain("SdkApiVersion", properties);
        Assert.DoesNotContain("SdkPackageVersion", properties);
        Assert.DoesNotContain("RequiredSdkCapabilities", properties);
        Assert.DoesNotContain("TargetFramework", properties);
        Assert.Equal(3, typeof(SunderPackageContentIndexEntry).GetProperties().Length);
        Assert.Equal(
            ["AbiSha256", "ContractId", "DescriptorPath", "DescriptorSha256", "Exposure", "Version"],
            typeof(SunderPackageContractBundleManifest).GetProperties()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["AbiSha256", "Actions", "ContractId", "ContractVersion", "Required", "Role"],
            typeof(SunderPackageContractUseManifest).GetProperties()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["AbiSha256", "Audience", "ContractId", "ContractVersion", "DescriptorSha256", "ProviderId", "Role"],
            typeof(SunderPackageProviderManifest).GetProperties()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PackageFormat_DeclaresManifestV3ArchiveV1RpcConstants()
    {
        Assert.Equal(1, SunderPackageFormat.CurrentArchiveFormatVersion);
        Assert.Equal(3, SunderPackageFormat.CurrentManifestVersion);
        Assert.True(SunderPackageFormat.IsContractExposure(SunderPackageFormat.PackageContractExposure));
        Assert.True(SunderPackageFormat.IsContractExposure(SunderPackageFormat.PublicContractExposure));
        Assert.False(SunderPackageFormat.IsContractExposure("Public"));
        Assert.True(SunderPackageFormat.IsProviderAudience(SunderPackageFormat.OwnerProviderAudience));
        Assert.True(SunderPackageFormat.IsProviderAudience(SunderPackageFormat.PublicProviderAudience));
        Assert.False(SunderPackageFormat.IsProviderAudience("Owner"));
    }

    private static SunderPackageManifest CreateManifest(
        IReadOnlyList<SunderPackageTargetManifest?> targets,
        IReadOnlyList<SunderPackageContractBundleManifest?>? contractBundles = null,
        IReadOnlyList<SunderPackageContractUseManifest?>? usesContracts = null,
        IReadOnlyList<SunderPackageProviderManifest?>? provides = null,
        string? icon = null)
        => new()
        {
            ArchiveFormatVersion = 1,
            ManifestVersion = 3,
            Id = "test.package",
            Name = "Test Package",
            Summary = "Canonical V3 test package.",
            Version = "1.0.0",
            Icon = icon,
            DependsOn = [new SunderPackageDependencyManifest { PackageId = "base.package", VersionRange = ">=1.0.0 <2.0.0" }],
            Targets = targets,
            ContractBundles = contractBundles,
            UsesContracts = usesContracts,
            Provides = provides,
        };

    private static SunderPackageTargetManifest CreateTarget(
        string role,
        string rid,
        string kind,
        string entryPoint,
        string? sdkVersion = null,
        IReadOnlyList<string?>? requiredHostCapabilities = null,
        bool includeSdkVersion = true,
        string? targetFramework = null)
        => new()
        {
            Role = role,
            Rid = rid,
            Kind = kind,
            EntryPoint = entryPoint,
            TargetFramework = targetFramework ?? (kind == "worker"
                ? "node24"
                : kind == "avalonia" ? "net10.0" : null),
            SdkVersion = includeSdkVersion
                ? sdkVersion ?? (role == SunderPackageFormat.RuntimeHostRole || kind == "avalonia" ? "1.1.0" : null)
                : null,
            RequiredHostCapabilities = requiredHostCapabilities ?? (role == SunderPackageFormat.RuntimeHostRole
                ? ["core.v1", "rpc.v2", "sdk-baseline-1-1.v1", "worker-protocol.v3"]
                : ["core.v1"]),
            Views = kind == SunderPackageFormat.WebTargetKind
                ?
                [
                    new SunderPackageWebViewManifest
                    {
                        ViewId = "test.package.main",
                        DisplayName = "Test Package",
                        Route = "/",
                        DefaultPlacement = "middle",
                        ShowInHotbar = true,
                    },
                ]
                : null,
        };

    private static string CreatePackageArchive(
        string root,
        SunderPackageManifest manifest,
        IReadOnlyDictionary<string, byte[]> files,
        Func<SunderPackageContentIndex, SunderPackageContentIndex>? indexTransform = null)
    {
        var source = Path.Combine(root, "package-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        WritePackageSource(source, manifest, files, indexTransform);
        var archivePath = Path.Combine(root, $"package-{Guid.NewGuid():N}.sunderpkg");
        SunderArchive.WriteDeterministic(source, archivePath);
        return archivePath;
    }

    private static void WritePackageSource(
        string source,
        SunderPackageManifest manifest,
        IReadOnlyDictionary<string, byte[]> files,
        Func<SunderPackageContentIndex, SunderPackageContentIndex>? indexTransform = null)
    {
        foreach (var file in files)
        {
            WriteFile(source, file.Key, file.Value);
        }
        WriteFile(source, SunderPackageFormat.ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest));

        var entries = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => CreateIndexEntry(source, path))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var index = indexTransform?.Invoke(new SunderPackageContentIndex(1, entries))
                    ?? new SunderPackageContentIndex(1, entries);
        WriteFile(source, SunderPackageFormat.ContentIndexPath, JsonSerializer.SerializeToUtf8Bytes(index));
    }

    private static SunderPackageContentIndexEntry CreateIndexEntry(string source, string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new SunderPackageContentIndexEntry(
            Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/'),
            Sha256(bytes),
            bytes.LongLength);
    }

    private static SunderPackageContentIndexEntry IndexEntry(string path)
        => new(path, new string('0', 64), 0);

    private static void WriteFile(string root, string relativePath, byte[] bytes)
    {
        var path = Path.Combine([root, .. relativePath.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string DescriptorHash(byte[] bytes)
        => SunderRpcContractDescriptor.Parse(bytes).Sha256;

    private static string AbiHash(byte[] bytes)
        => SunderRpcContractDescriptor.Parse(bytes).AbiSha256!;

    private static SunderPackageContractBundleManifest CreateContractBundle(
        byte[] descriptor,
        string descriptorPath,
        string exposure = SunderPackageFormat.PublicContractExposure)
    {
        var parsed = SunderRpcContractDescriptor.Parse(descriptor);
        return new SunderPackageContractBundleManifest
        {
            ContractId = parsed.ContractId,
            Version = parsed.Version,
            DescriptorPath = descriptorPath,
            DescriptorSha256 = parsed.Sha256,
            AbiSha256 = parsed.AbiSha256,
            Exposure = exposure,
        };
    }

    private static byte[] CreateV1ContractDescriptor(string contractId, string version)
        => System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "descriptorVersion": 1,
              "contractId": "{{contractId}}",
              "version": "{{version}}",
              "services": [
                {
                  "serviceId": "events",
                  "methods": [
                    {
                      "methodId": "publish",
                      "kind": "unary",
                      "requestSchema": { "$ref": "#/$defs/Request" },
                      "responseSchema": { "$ref": "#/$defs/Response" }
                    }
                  ]
                }
              ],
              "$defs": {
                "Request": {
                  "type": "object",
                  "properties": {
                    "value": { "type": "string", "minLength": 0, "maxLength": 256 }
                  },
                  "required": ["value"],
                  "additionalProperties": false
                },
                "Response": {
                  "type": "object",
                  "properties": {
                    "accepted": { "type": "boolean" }
                  },
                  "required": ["accepted"],
                  "additionalProperties": false
                }
              }
            }
            """);

    private static byte[] CreateContractDescriptor(string contractId, string version)
        => Encoding.UTF8.GetBytes($$"""
            {
              "descriptorVersion": 2,
              "schemaProfile": "sunder.rpc.values.v1",
              "contractId": "{{contractId}}",
              "version": "{{version}}",
              "errors": {},
              "services": {
                "example": {
                  "methods": {
                    "invoke": {
                      "kind": "unary",
                      "idempotency": "none",
                      "errors": [],
                      "requestSchema": { "$ref": "#/$defs/Empty" },
                      "responseSchema": { "$ref": "#/$defs/Empty" }
                    }
                  }
                }
              },
              "$defs": {
                "Empty": { "type": "object", "properties": {}, "required": [], "additionalProperties": false }
              }
            }
            """);

    private static byte[] TinyPng()
        => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-package-format-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
