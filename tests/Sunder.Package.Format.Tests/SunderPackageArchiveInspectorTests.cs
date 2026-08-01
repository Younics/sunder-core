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
        var manifest = CreateManifest(
            [CreateTarget(SunderPackageFormat.RuntimeHostRole, rid, SunderPackageFormat.DotnetTargetKind, "bin/host.dll")]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                [$"payload/runtime/{rid}/bin/host.dll"] = [1, 2, 3, 4],
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
    [InlineData("Win-x64")]
    [InlineData("freebsd-x64")]
    public async Task ExtractAndValidateAsync_RejectsWrongCaseAndUnknownRid(string rid)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget(SunderPackageFormat.RuntimeHostRole, rid, SunderPackageFormat.DotnetTargetKind, "host.dll")]);
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
            [CreateTarget("Runtime", "win-x64", SunderPackageFormat.DotnetTargetKind, "host.dll")]);
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
        var manifest = CreateManifest([CreateTarget("runtime", "win-x64", "process", "host")]);
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
    [InlineData("app", "process")]
    [InlineData("runtime", "avalonia")]
    [InlineData("runtime", "web")]
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
    public async Task ExtractAndValidateAsync_RejectsOldPayloadLibAndAssetsLayout()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "process", "host")]);
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
            [CreateTarget("runtime", "linux-x64", "dotnet", "tool.dll")]);
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
                CreateTarget("runtime", "win-x64", "dotnet", "runtime.dll"),
                CreateTarget("runtime", "linux-x64", "process", "bin/worker"),
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
    [InlineData("missing.dll", "does not resolve")]
    [InlineData("actual.exe", "must end with '.dll'")]
    public async Task ExtractAndValidateAsync_RejectsMissingOrWrongExtensionEntryPoint(
        string entryPoint,
        string expectedError)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("runtime", "linux-x64", "dotnet", entryPoint)]);
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
                    Sha256 = DescriptorHash(descriptor),
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
        var manifest = CreateManifest(
            [CreateTarget("app", "osx-arm64", "web", "index.html")],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "example.events",
                    Version = "1.2.0",
                    DescriptorPath = "contracts/events.json",
                    Sha256 = descriptorHash,
                },
            ],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    ContractId = "example.events",
                    VersionRange = ">=1.0.0 <2.0.0",
                    Required = true,
                    Actions = ["discover", "invoke", "subscribe"],
                },
            ],
            provides:
            [
                new SunderPackageProviderManifest
                {
                    ProviderId = "example.events.web",
                    ContractId = "example.events",
                    ContractVersion = "1.2.0",
                    ContractSha256 = descriptorHash,
                    Role = "app",
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/contracts/events.json"] = descriptor,
                ["payload/app/osx-arm64/index.html"] = "<html></html>"u8.ToArray(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("Discover")]
    [InlineData("publish")]
    public async Task ExtractAndValidateAsync_RejectsUnknownOrWrongCaseContractAction(string action)
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(
            [CreateTarget("runtime", "win-x64", "process", "host")],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    ContractId = "example.contract",
                    VersionRange = "1.0.0",
                    Required = false,
                    Actions = [action],
                },
            ]);
        var archivePath = CreatePackageArchive(
            root,
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/host"] = [1] });

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
            [CreateTarget("runtime", "linux-x64", "process", "host")],
            contractBundles:
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "example.contract",
                    Version = "1.0.0",
                    DescriptorPath = "contract.json",
                    Sha256 = DescriptorHash(descriptor),
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
                    Sha256 = new string('0', 64),
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
        Assert.Contains(result.Errors, error => error.Contains("Contract descriptor 'contract.json' SHA-256 mismatch", StringComparison.Ordinal));
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
                    Sha256 = DescriptorHash(descriptor),
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
            "\"maxLength\": 256",
            "\"maxLength\": 256, \"pattern\": \".*\"",
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
                    Sha256 = new string('0', 64),
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
            [CreateTarget("runtime", "linux-x64", "process", "host")],
            usesContracts:
            [
                new SunderPackageContractUseManifest
                {
                    ContractId = "example.missing",
                    VersionRange = "1.0.0",
                    Required = false,
                    Actions = ["discover"],
                },
            ],
            provides:
            [
                new SunderPackageProviderManifest
                {
                    ProviderId = "example.provider",
                    ContractId = "example.missing",
                    ContractVersion = "1.0.0",
                    ContractSha256 = new string('0', 64),
                    Role = "runtime",
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
        var manifest = CreateManifest([CreateTarget("runtime", "win-x64", "process", "host")]);
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
            CreateManifest([CreateTarget("runtime", "win-x64", "process", "host")]),
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
            CreateManifest([CreateTarget("runtime", "win-x64", "process", "host")]),
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
            CreateManifest([CreateTarget("runtime", "win-x64", "process", "host")]),
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
            CreateManifest([CreateTarget("runtime", "win-x64", "process", "host")]),
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
            CreateManifest([CreateTarget("runtime", "win-x64", "process", "host")]),
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
    [InlineData("archiveFormatVersion", 2)]
    [InlineData("manifestVersion", 2)]
    public async Task ExtractAndValidateAsync_RequiresExactVersionOne(string propertyName, int value)
    {
        var root = CreateTempDirectory();
        var source = CreateManifest([CreateTarget("runtime", "win-x64", "process", "host")]);
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
        Assert.Contains(result.Errors, error => error.Contains($"{propertyName} 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateExtractedPackageAsync_RejectsDuplicateJsonProperties()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            CreateManifest([CreateTarget("runtime", "win-x64", "process", "host")]),
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
                CreateTarget("runtime", "win-x64", "process", "bin/host"),
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
        var manifest = CreateManifest([CreateTarget("runtime", "linux-arm64", "process", "host")]);
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
            ManifestVersion = 1,
            Id = "test.package",
            Name = "Test Package",
            Summary = "Canonical V1 test package.",
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
        string entryPoint)
        => new()
        {
            Role = role,
            Rid = rid,
            Kind = kind,
            EntryPoint = entryPoint,
            TargetFramework = kind is "dotnet" or "avalonia" ? "net10.0" : null,
            SdkVersion = kind is "dotnet" or "avalonia" ? "1.1.0" : null,
            RequiredHostCapabilities = ["core.v1"],
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

    private static byte[] CreateContractDescriptor(string contractId, string version)
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

    private static byte[] TinyPng()
        => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-package-format-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
