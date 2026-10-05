using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class StackContributorRpcMigrationBaselineTests
{
    private const string UpdateEnvironmentVariable = "SUNDER_UPDATE_CORE_RPC_MIGRATION_BASELINE";
    private const string BaselineRelativePath = "eng/compatibility/stack-contributor-rpc-migration-baseline.json";
    private const string DotNetRootRelativePath = "languages/csharp/dotnet";
    private const string StackPackageRootRelativePath =
        "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks";
    private const string StackProjectRelativePath =
        "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Sunder.Sdk.Stacks.csproj";
    private const string DescriptorRelativePath =
        "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json";
    private const string BuildTransitivePropsRelativePath =
        "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/buildTransitive/Sunder.Sdk.Stacks.props";
    private const string FrozenContractId = "sunder.stack.contributor";
    private const int FrozenDescriptorVersion = 1;
    private const string FrozenContractVersion = "2.0.0";
    private const string FrozenSchemaProfile = "sunder.rpc.values.v1";
    private const string FrozenDescriptorSha256 =
        "38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898";
    private const string FrozenAbiSha256 =
        "6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8";
    private static readonly HashSet<string> RpcDeclarationItemTypes = new(StringComparer.Ordinal)
    {
        "SunderRpcBinding",
        "SunderRpcProvider",
        "SunderUsesContract",
        "SunderContractBundle",
    };
    private static readonly HashSet<string> DescriptorDistributionItemTypes = new(StringComparer.Ordinal)
    {
        "Content",
        "EmbeddedResource",
        "None",
    };
    private static readonly InventoryScopeSnapshot InventoryScope = new(
        DiscoveryRoot: DotNetRootRelativePath,
        IncludedRoots: [StackPackageRootRelativePath],
        ExcludedRoots:
        [
            new ScopeExclusionSnapshot(
                "languages/csharp/dotnet/packages/Sunder.Package.Templates/templates",
                "Scaffold declarations are template inputs, not shipping Sunder.Sdk.Stacks production inputs."),
            new ScopeExclusionSnapshot(
                "languages/csharp/dotnet/samples",
                "Sample declarations are documentation and parity inputs, not production migration inputs."),
            new ScopeExclusionSnapshot(
                "languages/csharp/dotnet/tests",
                "Test and fixture declarations verify behavior but are not production migration inputs."),
        ],
        ExcludedDirectoryNames: ["bin", "custom-audit-bin", "obj"],
        ExcludedDirectoryReason: "Generated build and audit outputs are not source declarations.");
    private static readonly SourceMigrationPolicySnapshot SourceMigrationPolicy = new(
        SourceClassification: "platform-owned-composite-semantic-adapter",
        CurrentTransportSourcePath:
            "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/StackContributorRpc.cs",
        FinalSourceLocation:
            "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/StackContributorRpcServiceAdapter.cs",
        AuthorityKind: "platform",
        AuthorityId: "sunder.core",
        Exposure: "public",
        ProviderPublicationPolicy: "open",
        ProviderPackageAllowlist: [],
        DescriptorSourceDisposition:
            "derive-from-annotated-composite-service-and-retain-frozen-bytes-as-migration-evidence-only",
        EmbeddedDescriptorDisposition: "remove-after-source-parity",
        PackedDescriptorDisposition: "remove-after-source-parity",
        BuildTransitiveDisposition: "remove-after-source-parity",
        HandwrittenTransportDisposition:
            "replace-with-role-local-generated-client-dispatch-codec-and-registration",
        FinalDescriptorDistribution: "signed-core-sunder-platform-contracts.v1.json-catalog-release-only",
        RetainedSemanticResponsibilities:
        [
            "six-method composition and frozen wire names",
            "optional capability flags and stack.method.unsupported behavior",
            "content-reference registration and opening",
            "Host-stamped owner and contributor identity",
        ]);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [Fact]
    public void MigrationBaseline_MatchesPlatformOwnedStackContractAndSourceInventory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var baselinePath = Path.Combine(repositoryRoot, NormalizePath(BaselineRelativePath));
        var generated = Generate(repositoryRoot);
        if (string.Equals(Environment.GetEnvironmentVariable(UpdateEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            File.WriteAllBytes(baselinePath, generated);
        }

        Assert.True(File.Exists(baselinePath), $"Stack RPC migration baseline was not found: {baselinePath}");
        Assert.Equal(Encoding.UTF8.GetString(File.ReadAllBytes(baselinePath)), Encoding.UTF8.GetString(generated));
    }

    [Fact]
    public void DescriptorDistributionDiscovery_RecognizesWildcardsAndUpdates()
    {
        var packedWildcard = new MsBuildItemSnapshot(
            StackProjectRelativePath,
            "None",
            null,
            "Contracts/*",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Pack"] = "true" },
            "Include");
        var metadataUpdate = packedWildcard with
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
            Operation = "Update",
        };

        Assert.True(IsRpcDescriptorDistributionItem(
            FindRepositoryRoot(),
            packedWildcard,
            [DescriptorRelativePath]));
        Assert.True(IsRpcDescriptorDistributionItem(
            FindRepositoryRoot(),
            metadataUpdate,
            [DescriptorRelativePath]));

        var releaseContent = new MsBuildItemSnapshot(
            StackProjectRelativePath,
            "Content",
            "'$(Configuration)' == 'Release'",
            "Contracts/*.rpc.json",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PackagePath"] = "shadow",
            },
            "Include");
        Assert.True(IsRpcDescriptorDistributionItem(
            FindRepositoryRoot(),
            releaseContent,
            [DescriptorRelativePath]));
    }

    private static byte[] Generate(string repositoryRoot)
    {
        var msbuildSources = EnumerateMsBuildSources(repositoryRoot);
        var allMsBuildItems = ReadMsBuildItems(repositoryRoot, msbuildSources);
        var allDescriptorSources = EnumerateDescriptorSources(repositoryRoot);
        var allRpcDeclarations = allMsBuildItems
            .Where(static item => RpcDeclarationItemTypes.Contains(item.ItemType))
            .ToArray();
        var allDescriptorDistributionItems = allMsBuildItems
            .Where(item => IsRpcDescriptorDistributionItem(repositoryRoot, item, allDescriptorSources))
            .ToArray();
        AssertItemScopeIsExhaustive(allRpcDeclarations.Concat(allDescriptorDistributionItems).Distinct().ToArray());

        var inScopeItems = allMsBuildItems
            .Where(item => IsUnderAnyRoot(item.SourcePath, InventoryScope.IncludedRoots))
            .ToArray();
        var inScopeDeclarations = allRpcDeclarations
            .Where(item => IsUnderAnyRoot(item.SourcePath, InventoryScope.IncludedRoots))
            .ToArray();
        var inScopeDescriptorDistributionItems = allDescriptorDistributionItems
            .Where(item => IsUnderAnyRoot(item.SourcePath, InventoryScope.IncludedRoots))
            .ToArray();
        Assert.All(inScopeDeclarations, static item => Assert.Equal("Include", item.Operation));
        Assert.All(
            inScopeDescriptorDistributionItems,
            static item => Assert.Equal("Include", item.Operation));
        var declarations = new RpcDeclarationInventorySnapshot(
            SunderRpcBindings: FilterDeclarations(inScopeDeclarations, "SunderRpcBinding"),
            SunderRpcProviders: FilterDeclarations(inScopeDeclarations, "SunderRpcProvider"),
            SunderUsesContracts: FilterDeclarations(inScopeDeclarations, "SunderUsesContract"),
            SunderContractBundles: FilterDeclarations(inScopeDeclarations, "SunderContractBundle"));

        AssertDescriptorSourceScopeIsExhaustive(allDescriptorSources);
        var descriptorSources = allDescriptorSources
            .Where(path => IsUnderAnyRoot(path, InventoryScope.IncludedRoots))
            .ToArray();
        var sourcePath = Assert.Single(descriptorSources);
        Assert.Equal(DescriptorRelativePath, sourcePath);
        var descriptorFilePath = Path.Combine(repositoryRoot, NormalizePath(sourcePath));
        Assert.True(File.Exists(Path.Combine(
            repositoryRoot,
            NormalizePath(SourceMigrationPolicy.CurrentTransportSourcePath))));
        Assert.True(File.Exists(Path.Combine(
            repositoryRoot,
            NormalizePath(SourceMigrationPolicy.FinalSourceLocation))));
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(descriptorFilePath));
        AssertFrozenIdentity(descriptor);
        AssertFrozenIdentity(SunderStackContributorRpc.Descriptor);
        Assert.Equal(
            Convert.ToBase64String(descriptor.GetCanonicalUtf8()),
            Convert.ToBase64String(SunderStackContributorRpc.Descriptor.GetCanonicalUtf8()));
        Assert.Equal(
            Convert.ToBase64String(descriptor.GetCanonicalAbiProjectionUtf8()),
            Convert.ToBase64String(SunderStackContributorRpc.Descriptor.GetCanonicalAbiProjectionUtf8()));

        var distribution = ReadShippingDistribution(inScopeItems, inScopeDescriptorDistributionItems);
        AssertShippingDistribution(repositoryRoot, sourcePath, distribution);
        AssertEvaluatedShippingDistribution(repositoryRoot);
        AssertRpcDeclarations(repositoryRoot, sourcePath, descriptor, declarations, inScopeDeclarations);

        var baseline = new StackRpcMigrationBaseline(
            SchemaVersion: 2,
            Purpose: "Pre-implementation-first platform contract migration oracle; not a build input or release authorization.",
            SourceHashPolicy: "SHA-256 of UTF-8 text after CRLF/CR normalization to LF.",
            InventoryScope: InventoryScope,
            AuthorityKind: "platform",
            AuthorityId: "sunder.core",
            Exposure: "public",
            MigrationPolicy: SourceMigrationPolicy,
            SourcePath: sourcePath,
            SourceSha256: HashSourceFile(descriptorFilePath),
            ContractId: descriptor.ContractId,
            DescriptorVersion: descriptor.DescriptorVersion,
            Version: descriptor.Version,
            SchemaProfile: descriptor.SchemaProfile,
            CanonicalDescriptorBase64: Convert.ToBase64String(descriptor.GetCanonicalUtf8()),
            DescriptorSha256: descriptor.Sha256,
            CanonicalAbiProjectionBase64: Convert.ToBase64String(descriptor.GetCanonicalAbiProjectionUtf8()),
            AbiSha256: descriptor.AbiSha256,
            Services: descriptor.Services.Select(service => new ServiceSnapshot(
                service.ServiceId,
                service.Methods.Select(method => new MethodSnapshot(
                    method.MethodId,
                    method.Kind == SunderRpcMethodKind.Unary ? "unary" : "server-stream",
                    method.Idempotency == SunderRpcMethodIdempotency.Idempotent ? "idempotent" : "none",
                    method.RequestSchemaReference,
                    method.OutputSchemaReference,
                    method.Errors)).ToArray())).ToArray(),
            Errors: descriptor.Errors.Values
                .OrderBy(static error => error.Code, StringComparer.Ordinal)
                .Select(static error => new ErrorSnapshot(error.Code, error.Description, error.DetailsSchema))
                .ToArray(),
            ShippingDistribution: distribution,
            RpcDeclarations: declarations);
        return JsonSerializer.SerializeToUtf8Bytes(baseline, JsonOptions);
    }

    private static IReadOnlyList<string> EnumerateMsBuildSources(string repositoryRoot)
    {
        var discoveryRoot = Path.Combine(repositoryRoot, NormalizePath(InventoryScope.DiscoveryRoot));
        Assert.True(Directory.Exists(discoveryRoot), $"RPC inventory root was not found: {discoveryRoot}");
        return Directory.EnumerateFiles(discoveryRoot, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets")
            .Where(path => !ContainsExcludedDirectory(RelativePath(repositoryRoot, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> EnumerateDescriptorSources(string repositoryRoot)
        => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, NormalizePath(InventoryScope.DiscoveryRoot)),
                "*.rpc.json",
                SearchOption.AllDirectories)
            .Select(path => RelativePath(repositoryRoot, path))
            .Where(static path => !ContainsExcludedDirectory(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<MsBuildItemSnapshot> ReadMsBuildItems(
        string repositoryRoot,
        IReadOnlyList<string> sources)
        => sources
            .SelectMany(source =>
            {
                var document = XDocument.Load(source);
                return document.Descendants()
                    .Where(static element => element.Parent?.Name.LocalName == "ItemGroup")
                    .Where(static element =>
                        element.Attributes().Any(static attribute =>
                            attribute.Name.LocalName is "Include" or "Update" or "Remove")
                        || RpcDeclarationItemTypes.Contains(element.Name.LocalName))
                    .Select(element => ReadMsBuildItem(repositoryRoot, source, element));
            })
            .OrderBy(static item => item.SourcePath, StringComparer.Ordinal)
            .ThenBy(static item => item.ItemType, StringComparer.Ordinal)
            .ThenBy(static item => item.Include, StringComparer.Ordinal)
            .ThenBy(static item => item.Condition, StringComparer.Ordinal)
            .ThenBy(
                static item => string.Join('\0', item.Metadata.Select(static pair => $"{pair.Key}={pair.Value}")),
                StringComparer.Ordinal)
            .ToArray();

    private static MsBuildItemSnapshot ReadMsBuildItem(
        string repositoryRoot,
        string source,
        XElement item)
    {
        var operations = item.Attributes()
            .Where(static attribute => attribute.Name.LocalName is "Include" or "Update" or "Remove")
            .ToArray();
        if (operations.Length != 1)
        {
            throw new InvalidDataException(
                $"RPC MSBuild item '{RelativePath(repositoryRoot, source)}:{item.Name.LocalName}' requires exactly one Include, Update, or Remove operation.");
        }
        var include = operations[0].Value.Trim();
        if (string.IsNullOrWhiteSpace(include))
        {
            throw new InvalidDataException(
                $"RPC MSBuild declaration '{RelativePath(repositoryRoot, source)}:{item.Name.LocalName}' requires Include metadata.");
        }

        return new MsBuildItemSnapshot(
            RelativePath(repositoryRoot, source),
            item.Name.LocalName,
            ReadCondition(item),
            NormalizeRelativePath(include),
            ReadMetadata(item),
            operations[0].Name.LocalName);
    }

    private static string? ReadCondition(XElement item)
    {
        var conditions = item.AncestorsAndSelf()
            .Reverse()
            .Select(static element => element.Attribute("Condition")?.Value.Trim())
            .Where(static condition => !string.IsNullOrWhiteSpace(condition))
            .Select(static condition => $"({condition})")
            .ToArray();
        return conditions.Length == 0 ? null : string.Join(" and ", conditions);
    }

    private static IReadOnlyDictionary<string, string> ReadMetadata(XElement item)
    {
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in item.Attributes().Where(static attribute =>
                     attribute.Name.LocalName is not "Include"
                         and not "Update"
                         and not "Remove"
                         and not "Condition"))
        {
            metadata[attribute.Name.LocalName] = NormalizeRelativePath(attribute.Value.Trim());
        }
        foreach (var child in item.Elements())
        {
            metadata[child.Name.LocalName] = NormalizeRelativePath(child.Value.Trim());
        }
        return metadata;
    }

    private static void AssertItemScopeIsExhaustive(IReadOnlyList<MsBuildItemSnapshot> declarations)
    {
        var unclassified = declarations
            .Where(declaration =>
                !IsUnderAnyRoot(declaration.SourcePath, InventoryScope.IncludedRoots)
                && !IsUnderAnyRoot(
                    declaration.SourcePath,
                    InventoryScope.ExcludedRoots.Select(static exclusion => exclusion.Path)))
            .ToArray();
        Assert.True(
            unclassified.Length == 0,
            $"Core RPC declarations outside the reviewed production scope or explicit exclusions: {Describe(unclassified)}");

        foreach (var exclusion in InventoryScope.ExcludedRoots)
        {
            if (exclusion.Path is "languages/csharp/dotnet/samples" or "languages/csharp/dotnet/packages/Sunder.Package.Templates/templates")
            {
                // Quickstart and templates now derive owned/provider metadata from source.
                // Keep the frozen Stack oracle intact and reject reintroduced declarations.
                Assert.DoesNotContain(declarations, declaration => IsUnderRoot(declaration.SourcePath, exclusion.Path));
                continue;
            }
            Assert.Contains(
                declarations,
                declaration => IsUnderRoot(declaration.SourcePath, exclusion.Path));
        }
    }

    private static void AssertDescriptorSourceScopeIsExhaustive(IReadOnlyList<string> descriptorSources)
    {
        var unclassified = descriptorSources
            .Where(path =>
                !IsUnderAnyRoot(path, InventoryScope.IncludedRoots)
                && !IsUnderAnyRoot(path, InventoryScope.ExcludedRoots.Select(static exclusion => exclusion.Path)))
            .ToArray();
        Assert.True(
            unclassified.Length == 0,
            $"Core RPC descriptor sources outside the reviewed production scope or explicit exclusions: {string.Join(", ", unclassified)}");
    }

    private static ShippingDistributionSnapshot ReadShippingDistribution(
        IReadOnlyList<MsBuildItemSnapshot> inScopeItems,
        IReadOnlyList<MsBuildItemSnapshot> descriptorDistributionItems)
    {
        var descriptors = descriptorDistributionItems.ToHashSet();
        return new(
            EmbeddedDescriptors: inScopeItems
                .Where(item => item.ItemType == "EmbeddedResource" && descriptors.Contains(item))
                .ToArray(),
            PackedDescriptors: inScopeItems
                .Where(item => IsPacked(item) && descriptors.Contains(item))
                .ToArray(),
            PackedBuildTransitiveAssets: inScopeItems
                .Where(static item => IsPacked(item) && IsBuildTransitiveAsset(item))
                .ToArray());
    }

    private static void AssertShippingDistribution(
        string repositoryRoot,
        string sourcePath,
        ShippingDistributionSnapshot distribution)
    {
        var embedded = Assert.Single(distribution.EmbeddedDescriptors);
        Assert.Equal(StackProjectRelativePath, embedded.SourcePath);
        Assert.Equal(sourcePath, ResolveStaticInclude(repositoryRoot, embedded));
        Assert.Equal(
            "Sunder.Sdk.Stacks.Contracts.sunder.stack.contributor.rpc.json",
            embedded.Metadata.GetValueOrDefault("LogicalName"));

        var packedDescriptor = Assert.Single(distribution.PackedDescriptors);
        Assert.Equal(StackProjectRelativePath, packedDescriptor.SourcePath);
        Assert.Equal(sourcePath, ResolveStaticInclude(repositoryRoot, packedDescriptor));
        Assert.Equal("true", packedDescriptor.Metadata.GetValueOrDefault("Pack"));
        Assert.Equal("contracts", packedDescriptor.Metadata.GetValueOrDefault("PackagePath"));

        var packedBuildTransitive = Assert.Single(distribution.PackedBuildTransitiveAssets);
        Assert.Equal(StackProjectRelativePath, packedBuildTransitive.SourcePath);
        Assert.Equal(
            BuildTransitivePropsRelativePath,
            ResolveStaticInclude(repositoryRoot, packedBuildTransitive));
        Assert.Equal("true", packedBuildTransitive.Metadata.GetValueOrDefault("Pack"));
        Assert.Equal("buildTransitive", packedBuildTransitive.Metadata.GetValueOrDefault("PackagePath"));
    }

    private static void AssertEvaluatedShippingDistribution(string repositoryRoot)
    {
        var projectPath = Path.Combine(repositoryRoot, NormalizePath(StackProjectRelativePath));
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-getItem:None,EmbeddedResource,Content");
        startInfo.ArgumentList.Add("-p:Configuration=Release");
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start MSBuild item evaluation.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"MSBuild item evaluation failed: {error}");

        using var document = JsonDocument.Parse(output);
        var evaluated = document.RootElement.GetProperty("Items")
            .EnumerateObject()
            .SelectMany(static group => group.Value.EnumerateArray().Select(item => (ItemType: group.Name, Item: item)))
            .ToArray();
        var descriptorItems = evaluated
            .Where(static item => IsEvaluatedDescriptorDistribution(item.ItemType, item.Item))
            .ToArray();
        var embedded = Assert.Single(descriptorItems, static item => item.ItemType == "EmbeddedResource").Item;
        var packed = Assert.Single(descriptorItems, static item => item.ItemType is "None" or "Content").Item;
        var expectedDescriptorPath = Path.GetFullPath(Path.Combine(repositoryRoot, NormalizePath(DescriptorRelativePath)));
        Assert.Equal(expectedDescriptorPath, Path.GetFullPath(embedded.GetProperty("FullPath").GetString()!));
        Assert.Equal(expectedDescriptorPath, Path.GetFullPath(packed.GetProperty("FullPath").GetString()!));
        Assert.Equal("Sunder.Sdk.Stacks.Contracts.sunder.stack.contributor.rpc.json", embedded.GetProperty("LogicalName").GetString());
        Assert.Equal("true", packed.GetProperty("Pack").GetString(), ignoreCase: true);
        Assert.Equal("contracts", packed.GetProperty("PackagePath").GetString());

        var expectedBuildTransitivePath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            NormalizePath(BuildTransitivePropsRelativePath)));
        var buildTransitive = Assert.Single(evaluated, item =>
            item.Item.TryGetProperty("Pack", out var pack)
            && string.Equals(pack.GetString(), "true", StringComparison.OrdinalIgnoreCase)
            && item.Item.TryGetProperty("FullPath", out var fullPath)
            && string.Equals(
                Path.GetFullPath(fullPath.GetString()!),
                expectedBuildTransitivePath,
                StringComparison.Ordinal));
        Assert.Equal("buildTransitive", buildTransitive.Item.GetProperty("PackagePath").GetString());
    }

    private static bool IsEvaluatedDescriptorDistribution(string itemType, JsonElement item)
    {
        var namesDescriptor = item.TryGetProperty("FullPath", out var fullPath)
                              && IsRpcDescriptor(NormalizeRelativePath(fullPath.GetString()!))
                              || item.TryGetProperty("Link", out var link)
                              && IsRpcDescriptor(NormalizeRelativePath(link.GetString()!))
                              || item.TryGetProperty("PackagePath", out var packagePath)
                              && IsRpcDescriptor(NormalizeRelativePath(packagePath.GetString()!));
        if (!namesDescriptor) return false;
        if (itemType == "EmbeddedResource") return true;
        if (itemType == "Content")
        {
            return !item.TryGetProperty("Pack", out var contentPack)
                   || !string.Equals(contentPack.GetString(), "false", StringComparison.OrdinalIgnoreCase);
        }
        return item.TryGetProperty("Pack", out var pack)
               && string.Equals(pack.GetString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRpcDeclarations(
        string repositoryRoot,
        string sourcePath,
        SunderRpcContractDescriptor descriptor,
        RpcDeclarationInventorySnapshot declarations,
        IReadOnlyList<MsBuildItemSnapshot> allInScopeDeclarations)
    {
        Assert.Empty(declarations.SunderRpcBindings);
        Assert.Empty(declarations.SunderRpcProviders);
        Assert.Empty(declarations.SunderUsesContracts);
        var bundle = Assert.Single(declarations.SunderContractBundles);
        Assert.Equal(BuildTransitivePropsRelativePath, bundle.SourcePath);
        Assert.Equal("public", bundle.Metadata.GetValueOrDefault("Exposure"));

        var recorded = declarations.SunderRpcBindings
            .Concat(declarations.SunderRpcProviders)
            .Concat(declarations.SunderUsesContracts)
            .Concat(declarations.SunderContractBundles)
            .ToHashSet();
        Assert.Equal(allInScopeDeclarations.Count, recorded.Count);
        Assert.All(allInScopeDeclarations, declaration => Assert.Contains(declaration, recorded));

        var unrelated = allInScopeDeclarations
            .Where(declaration => !MatchesDescriptor(repositoryRoot, declaration, sourcePath, descriptor))
            .ToArray();
        Assert.True(
            unrelated.Length == 0,
            $"In-scope RPC declarations not recorded against the frozen Stack descriptor: {Describe(unrelated)}");
    }

    private static bool MatchesDescriptor(
        string repositoryRoot,
        MsBuildItemSnapshot declaration,
        string sourcePath,
        SunderRpcContractDescriptor descriptor)
        => declaration.ItemType switch
        {
            "SunderRpcBinding" => string.Equals(
                ResolveStaticInclude(repositoryRoot, declaration),
                sourcePath,
                StringComparison.Ordinal),
            "SunderRpcProvider" => MetadataEquals(declaration, "ContractId", descriptor.ContractId)
                                   && MetadataEquals(declaration, "ContractVersion", descriptor.Version),
            "SunderUsesContract" => string.Equals(declaration.Include, descriptor.ContractId, StringComparison.Ordinal)
                                    && MetadataEquals(declaration, "ContractVersion", descriptor.Version),
            "SunderContractBundle" => MetadataEquals(declaration, "ContractId", descriptor.ContractId)
                                      && MetadataEquals(declaration, "Version", descriptor.Version),
            _ => false,
        };

    private static IReadOnlyList<MsBuildItemSnapshot> FilterDeclarations(
        IReadOnlyList<MsBuildItemSnapshot> declarations,
        string itemType)
        => declarations.Where(declaration => declaration.ItemType == itemType).ToArray();

    private static void AssertFrozenIdentity(SunderRpcContractDescriptor descriptor)
    {
        Assert.Equal(FrozenContractId, descriptor.ContractId);
        Assert.Equal(FrozenDescriptorVersion, descriptor.DescriptorVersion);
        Assert.Equal(FrozenContractVersion, descriptor.Version);
        Assert.Equal(FrozenSchemaProfile, descriptor.SchemaProfile);
        Assert.Equal(FrozenDescriptorSha256, descriptor.Sha256);
        Assert.Equal(FrozenAbiSha256, descriptor.AbiSha256);
    }

    private static bool IsPacked(MsBuildItemSnapshot item)
        => item.ItemType == "Content"
            ? !item.Metadata.TryGetValue("Pack", out var contentPack)
              || !string.Equals(contentPack, "false", StringComparison.OrdinalIgnoreCase)
            : item.Metadata.TryGetValue("Pack", out var pack)
              && string.Equals(pack, "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsRpcDescriptor(string include)
        => include.EndsWith(".rpc.json", StringComparison.Ordinal);

    private static bool IsRpcDescriptorDistributionItem(
        string repositoryRoot,
        MsBuildItemSnapshot item,
        IReadOnlyList<string> descriptorSources)
    {
        if (!DescriptorDistributionItemTypes.Contains(item.ItemType)) return false;
        var namesRpcDestination = IsRpcDescriptor(item.Include)
                                  || item.Metadata.Values.Any(IsRpcDescriptor);
        var matchesRpcSource = descriptorSources.Any(source =>
            (IsUnderAnyRoot(source, InventoryScope.IncludedRoots)
             || IsUnderAnyRoot(item.SourcePath, InventoryScope.IncludedRoots)
             || !IsUnderAnyRoot(source, InventoryScope.ExcludedRoots.Select(static exclusion => exclusion.Path)))
            && MatchesMsBuildInclude(repositoryRoot, item.SourcePath, item.Include, source));
        if (!namesRpcDestination && !matchesRpcSource) return false;
        return item.ItemType == "EmbeddedResource"
               || IsPacked(item)
               || item.Operation is "Update" or "Remove";
    }

    private static bool MatchesMsBuildInclude(
        string repositoryRoot,
        string itemSourcePath,
        string include,
        string candidatePath)
    {
        if (include.Contains("$(", StringComparison.Ordinal)) return false;
        var itemSource = Path.Combine(repositoryRoot, NormalizePath(itemSourcePath));
        var itemDirectory = Path.GetDirectoryName(itemSource) ?? repositoryRoot;
        var candidate = Path.Combine(repositoryRoot, NormalizePath(candidatePath));
        var relativeCandidate = NormalizeRelativePath(Path.GetRelativePath(itemDirectory, candidate));
        var pattern = NormalizeRelativePath(include);
        var expression = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                expression.Append(".*");
                index++;
            }
            else if (character == '*')
            {
                expression.Append("[^/]*");
            }
            else if (character == '?')
            {
                expression.Append("[^/]");
            }
            else
            {
                expression.Append(Regex.Escape(character.ToString()));
            }
        }
        expression.Append('$');
        return Regex.IsMatch(relativeCandidate, expression.ToString(), RegexOptions.CultureInvariant);
    }

    private static bool IsBuildTransitiveAsset(MsBuildItemSnapshot item)
        => item.Metadata.TryGetValue("PackagePath", out var packagePath)
           && (string.Equals(packagePath.TrimEnd('/'), "buildTransitive", StringComparison.Ordinal)
               || packagePath.StartsWith("buildTransitive/", StringComparison.Ordinal));

    private static bool MetadataEquals(MsBuildItemSnapshot declaration, string name, string expected)
        => declaration.Metadata.TryGetValue(name, out var actual)
           && string.Equals(actual, expected, StringComparison.Ordinal);

    private static string? ResolveStaticInclude(string repositoryRoot, MsBuildItemSnapshot item)
    {
        if (item.Include.Contains("$(", StringComparison.Ordinal))
        {
            return null;
        }

        var sourcePath = Path.Combine(repositoryRoot, NormalizePath(item.SourcePath));
        var sourceDirectory = Path.GetDirectoryName(sourcePath) ?? repositoryRoot;
        var resolvedPath = Path.GetFullPath(Path.Combine(sourceDirectory, NormalizePath(item.Include)));
        return RelativePath(repositoryRoot, resolvedPath);
    }

    private static bool IsUnderAnyRoot(string path, IEnumerable<string> roots)
        => roots.Any(root => IsUnderRoot(path, root));

    private static bool IsUnderRoot(string path, string root)
        => string.Equals(path, root, StringComparison.Ordinal)
           || path.StartsWith($"{root.TrimEnd('/')}/", StringComparison.Ordinal);

    private static bool ContainsExcludedDirectory(string relativePath)
    {
        var segments = NormalizeRelativePath(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => InventoryScope.ExcludedDirectoryNames.Contains(segment, StringComparer.Ordinal));
    }

    private static string Describe(IEnumerable<MsBuildItemSnapshot> items)
        => string.Join(
            ", ",
            items.Select(static item => $"{item.SourcePath}:{item.ItemType}:{item.Include}"));

    private static string HashSourceFile(string path)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(File.ReadAllBytes(path));
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "eng", "compatibility")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the Sunder Core repository root.");
    }

    private static string RelativePath(string root, string path)
        => NormalizeRelativePath(Path.GetRelativePath(root, path));

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string NormalizePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private sealed record StackRpcMigrationBaseline(
        int SchemaVersion,
        string Purpose,
        string SourceHashPolicy,
        InventoryScopeSnapshot InventoryScope,
        string AuthorityKind,
        string AuthorityId,
        string Exposure,
        SourceMigrationPolicySnapshot MigrationPolicy,
        string SourcePath,
        string SourceSha256,
        string ContractId,
        int DescriptorVersion,
        string Version,
        string SchemaProfile,
        string CanonicalDescriptorBase64,
        string DescriptorSha256,
        string CanonicalAbiProjectionBase64,
        string AbiSha256,
        IReadOnlyList<ServiceSnapshot> Services,
        IReadOnlyList<ErrorSnapshot> Errors,
        ShippingDistributionSnapshot ShippingDistribution,
        RpcDeclarationInventorySnapshot RpcDeclarations);

    private sealed record InventoryScopeSnapshot(
        string DiscoveryRoot,
        IReadOnlyList<string> IncludedRoots,
        IReadOnlyList<ScopeExclusionSnapshot> ExcludedRoots,
        IReadOnlyList<string> ExcludedDirectoryNames,
        string ExcludedDirectoryReason);

    private sealed record ScopeExclusionSnapshot(string Path, string Reason);

    private sealed record SourceMigrationPolicySnapshot(
        string SourceClassification,
        string CurrentTransportSourcePath,
        string FinalSourceLocation,
        string AuthorityKind,
        string AuthorityId,
        string Exposure,
        string ProviderPublicationPolicy,
        IReadOnlyList<string> ProviderPackageAllowlist,
        string DescriptorSourceDisposition,
        string EmbeddedDescriptorDisposition,
        string PackedDescriptorDisposition,
        string BuildTransitiveDisposition,
        string HandwrittenTransportDisposition,
        string FinalDescriptorDistribution,
        IReadOnlyList<string> RetainedSemanticResponsibilities);

    private sealed record ShippingDistributionSnapshot(
        IReadOnlyList<MsBuildItemSnapshot> EmbeddedDescriptors,
        IReadOnlyList<MsBuildItemSnapshot> PackedDescriptors,
        IReadOnlyList<MsBuildItemSnapshot> PackedBuildTransitiveAssets);

    private sealed record RpcDeclarationInventorySnapshot(
        IReadOnlyList<MsBuildItemSnapshot> SunderRpcBindings,
        IReadOnlyList<MsBuildItemSnapshot> SunderRpcProviders,
        IReadOnlyList<MsBuildItemSnapshot> SunderUsesContracts,
        IReadOnlyList<MsBuildItemSnapshot> SunderContractBundles);

    private sealed record MsBuildItemSnapshot(
        string SourcePath,
        string ItemType,
        string? Condition,
        string Include,
        IReadOnlyDictionary<string, string> Metadata,
        [property: JsonIgnore] string Operation);

    private sealed record ServiceSnapshot(string ServiceId, IReadOnlyList<MethodSnapshot> Methods);

    private sealed record MethodSnapshot(
        string MethodId,
        string Kind,
        string Idempotency,
        string RequestSchemaReference,
        string OutputSchemaReference,
        IReadOnlyList<string> Errors);

    private sealed record ErrorSnapshot(string Code, string? Description, string? DetailsSchema);
}
