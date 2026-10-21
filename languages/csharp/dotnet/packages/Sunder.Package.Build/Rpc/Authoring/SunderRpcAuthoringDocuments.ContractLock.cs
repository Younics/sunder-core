using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcLockedArtifactContract(
    string ContractId,
    string ArtifactSource,
    string AuthorityKind,
    string AuthorityId,
    string SelectedAuthorityRelease,
    string SelectedAuthorityContentIdentity,
    string ResolvedContractVersion,
    int DescriptorBytes,
    string DescriptorSha256,
    string AbiSha256,
    string Etag);

internal sealed record SunderRpcLockedPackageArtifact(
    string PackageId,
    string RequestedVersionRange,
    bool IncludePrerelease,
    string SelectedPackageVersion,
    string SelectedPackageContentIdentity,
    string? RegistryOrigin,
    int PublicContractCount,
    string PublicInventorySha256,
    int ModelProjectionBytes,
    string ModelProjectionSha256,
    IReadOnlyDictionary<string, SunderRpcLockedArtifactContract> Contracts);

internal sealed record SunderRpcLockedPackageImport(
    string PackageId,
    string LocalName,
    string Presence,
    IReadOnlyList<string> RequiredContracts,
    IReadOnlyList<string> ConsumerRoles);

internal sealed record SunderRpcLockedProviderImport(
    string Key,
    string PackageId,
    string ContractId);

internal sealed record SunderRpcLockedPlatformImport(
    string Key,
    string AuthorityKind,
    string AuthorityId,
    string AuthorityReleaseRange,
    bool IncludePrerelease,
    string SelectedAuthorityRelease,
    string SelectedAuthorityContentIdentity,
    string ContractId,
    string ResolvedContractVersion,
    string? RegistryOrigin,
    int DescriptorBytes,
    string DescriptorSha256,
    string AbiSha256,
    string Etag);

internal sealed record SunderRpcContractLockMaterialization(
    int ContractBundles,
    int RoleScopedUses,
    int Providers,
    int PackageDependencies);

internal sealed record SunderRpcContractLock(
    IReadOnlyDictionary<string, SunderRpcLockedPackageArtifact> PackageArtifacts,
    IReadOnlyDictionary<string, SunderRpcLockedPackageImport> PackageImports,
    IReadOnlyDictionary<string, SunderRpcLockedProviderImport> ProviderImports,
    IReadOnlyDictionary<string, SunderRpcLockedPlatformImport> PlatformImports,
    SunderRpcContractLockMaterialization Materialization,
    byte[] ExactBytes,
    string Sha256);

internal static partial class SunderRpcAuthoringDocuments
{
    private const int MaximumCacheReceiptBytes = 16_384;
    private const int MaximumLockEntries = 128;
    private const int MaximumDescriptorBytes = 4_194_304;

    public static SunderRpcContractLock ReadContractLock(
        string path,
        bool allowDevelopmentRegistryOrigin = false)
        => ParseContractLock(SunderRpcDescriptorFileReader.ReadStableBytes(path, MaximumContractLockBytes, "Sunder contract lock"), allowDevelopmentRegistryOrigin);

    internal static SunderRpcContractLock ParseContractLock(byte[] bytes, bool allowDevelopmentRegistryOrigin = false)
    {
        var root = ParseStrictDocument(bytes, MaximumContractLockBytes, "Sunder contract lock", ValidatePresentConsumerRoles);
        using (root)
        {
            var document = root.RootElement;
            RequireObject(
                document,
                "contract lock",
                ["lockVersion", "packageArtifacts", "packageImports", "providerImports", "platformImports"],
                ["lockVersion", "packageArtifacts", "packageImports", "providerImports", "platformImports"],
                []);
            RequireInteger(document, "lockVersion", 1, "contract lock");

            var artifacts = ReadPackageArtifacts(document, allowDevelopmentRegistryOrigin);
            var packageImports = ReadPackageImports(document);
            var providerImports = ReadProviderImports(document);
            var platformImports = ReadPlatformImports(document, allowDevelopmentRegistryOrigin);
            var materialization = ValidateContractLock(artifacts, packageImports, providerImports, platformImports);
            return new(
                artifacts,
                packageImports,
                providerImports,
                platformImports,
                materialization,
                bytes,
                LowerSha256(bytes));
        }
    }

    public static SunderRpcContractCacheEntryReceipt ReadContractCacheEntryReceipt(string path)
    {
        var (_, root) = ReadStrictDocument(path, MaximumCacheReceiptBytes, "Sunder contract cache entry receipt");
        using (root)
        {
            var document = root.RootElement;
            const string label = "contract cache entry receipt";
            RequireObject(
                document,
                label,
                ["schemaVersion", "contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256"],
                ["schemaVersion", "contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256"],
                []);
            RequireInteger(document, "schemaVersion", 1, label);
            return new(
                RequirePackageId(document, "contractId", label),
                RequireSemanticVersion(document, "contractVersion", label),
                RequireBoundedInteger(document, "descriptorBytes", 2, MaximumDescriptorBytes, label),
                RequireSha256(document, "descriptorSha256", label),
                RequireSha256(document, "abiSha256", label));
        }
    }

    private static IReadOnlyDictionary<string, SunderRpcLockedPackageArtifact> ReadPackageArtifacts(
        JsonElement root,
        bool allowDevelopmentRegistryOrigin)
    {
        var map = RequireProperty(root, "packageArtifacts", JsonValueKind.Object, "contract lock");
        var properties = ReadOrderedLockMap(map, "contract lock packageArtifacts");
        var result = new Dictionary<string, SunderRpcLockedPackageArtifact>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            RequireCanonicalPackageId(property.Name, "contract lock package artifact key");
            result.Add(property.Name, ReadPackageArtifact(
                property.Name,
                property.Value,
                allowDevelopmentRegistryOrigin));
        }
        return result;
    }

    private static SunderRpcLockedPackageArtifact ReadPackageArtifact(
        string packageId,
        JsonElement value,
        bool allowDevelopmentRegistryOrigin)
    {
        var label = $"contract lock package artifact '{packageId}'";
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"The {label} must be an object.");
        if (!value.TryGetProperty("modelProjectionBytes", out _) || !value.TryGetProperty("modelProjectionSha256", out _))
            throw ContractLockError("package-api.model-projection-invalid",
                $"The {label} is missing required model projection pins. Restore the complete reviewed lock; commands never infer missing pins.");
        RequireObject(
            value,
            label,
            [
                "requestedVersionRange",
                "includePrerelease",
                "selectedPackageVersion",
                "selectedPackageContentIdentity",
                "registryOrigin",
                "publicContractCount",
                "publicInventorySha256",
                "modelProjectionBytes",
                "modelProjectionSha256",
                "contracts",
            ],
            [
                "requestedVersionRange",
                "includePrerelease",
                "selectedPackageVersion",
                "selectedPackageContentIdentity",
                "publicContractCount",
                "publicInventorySha256",
                "modelProjectionBytes",
                "modelProjectionSha256",
                "contracts",
            ],
            ["registryOrigin"]);
        var range = RequireVersionRange(value, "requestedVersionRange", label);
        var includePrerelease = RequireBoolean(value, "includePrerelease", label);
        var selectedVersion = RequireSemanticVersion(value, "selectedPackageVersion", label);
        ValidateSelectedVersion(selectedVersion, range, includePrerelease, label);
        var selectedContentIdentity = RequireString(value, "selectedPackageContentIdentity", label);
        var registryOrigin = ReadOptionalRegistryOrigin(value, label, allowDevelopmentRegistryOrigin);
        var publicContractCount = RequireBoundedInteger(
            value,
            "publicContractCount",
            1,
            MaximumLockEntries,
            label);
        var publicInventorySha256 = RequireString(value, "publicInventorySha256", label);
        var contracts = ReadArtifactContracts(packageId, value);

        var artifact = new SunderRpcLockedPackageArtifact(
            packageId,
            range,
            includePrerelease,
            selectedVersion,
            selectedContentIdentity,
            registryOrigin,
            publicContractCount,
            publicInventorySha256,
            RequireBoundedInteger(value, "modelProjectionBytes", 1, 4_194_304, label),
            RequireSha256(value, "modelProjectionSha256", label),
            contracts);
        ValidatePackagePublicInventory(artifact);
        return artifact;
    }

    private static IReadOnlyDictionary<string, SunderRpcLockedArtifactContract> ReadArtifactContracts(
        string packageId,
        JsonElement artifact)
    {
        var map = RequireProperty(artifact, "contracts", JsonValueKind.Object, $"package artifact '{packageId}'");
        var properties = ReadOrderedLockMap(map, $"package artifact '{packageId}' contracts", requireNonempty: true);
        var result = new Dictionary<string, SunderRpcLockedArtifactContract>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            result.Add(property.Name, ReadArtifactContract(packageId, property.Name, property.Value));
        }
        return result;
    }

    private static SunderRpcLockedArtifactContract ReadArtifactContract(
        string packageId,
        string contractId,
        JsonElement value)
    {
        var label = $"package artifact '{packageId}' contract '{contractId}'";
        RequireObject(
            value,
            label,
            [
                "artifactSource",
                "authorityKind",
                "authorityId",
                "selectedAuthorityRelease",
                "selectedAuthorityContentIdentity",
                "resolvedContractVersion",
                "descriptorBytes",
                "descriptorSha256",
                "abiSha256",
                "etag",
            ],
            [
                "artifactSource",
                "authorityKind",
                "authorityId",
                "selectedAuthorityRelease",
                "selectedAuthorityContentIdentity",
                "resolvedContractVersion",
                "descriptorBytes",
                "descriptorSha256",
                "abiSha256",
                "etag",
            ],
            []);
        // Read the lock's typed fields here; the shared inventory parser validates their grammar and provenance.
        var artifactSource = RequireString(value, "artifactSource", label);
        var authorityKind = RequireString(value, "authorityKind", label);
        var authorityId = RequireString(value, "authorityId", label);
        var authorityRelease = RequireString(value, "selectedAuthorityRelease", label);
        var authorityContentIdentity = RequireString(value, "selectedAuthorityContentIdentity", label);
        var resolvedVersion = RequireString(value, "resolvedContractVersion", label);
        var descriptorBytes = RequireBoundedInteger(value, "descriptorBytes", 0, int.MaxValue, label);
        var descriptorSha256 = RequireString(value, "descriptorSha256", label);
        var abiSha256 = RequireString(value, "abiSha256", label);
        var etag = RequireStrongDescriptorEtag(value, descriptorSha256, label);
        return new(
            contractId,
            artifactSource,
            authorityKind,
            authorityId,
            authorityRelease,
            authorityContentIdentity,
            resolvedVersion,
            descriptorBytes,
            descriptorSha256,
            abiSha256,
            etag);
    }

    private static IReadOnlyDictionary<string, SunderRpcLockedPackageImport> ReadPackageImports(JsonElement root)
    {
        var map = RequireProperty(root, "packageImports", JsonValueKind.Object, "contract lock");
        var properties = ReadOrderedLockMap(map, "contract lock packageImports");
        var result = new Dictionary<string, SunderRpcLockedPackageImport>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            var packageId = property.Name;
            RequireCanonicalPackageId(packageId, "contract lock package import key");
            var label = $"contract lock package import '{packageId}'";
            if (property.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"The {label} must be an object.");
            if (!property.Value.TryGetProperty("consumerRoles", out var roles))
                throw ContractLockError("package-api.consumer-roles-migration-required",
                    $"{label} is missing consumerRoles. Restore a current lock with explicitly reviewed role intent; Build/Restore never infer roles.");
            var consumerRoles = ReadConsumerRoles(roles, label);
            RequireObject(
                property.Value,
                label,
                ["localName", "presence", "requiredContracts", "consumerRoles"],
                ["localName", "presence", "requiredContracts", "consumerRoles"],
                []);
            var localName = RequireLocalName(property.Value, "localName", label);
            var presence = RequireOneOf(property.Value, "presence", label, "required", "optional");
            var requiredValues = RequireProperty(property.Value, "requiredContracts", JsonValueKind.Array, label)
                .EnumerateArray()
                .ToArray();
            if (requiredValues.Length > MaximumLockEntries)
            {
                throw new InvalidDataException($"The {label} exceeds the {MaximumLockEntries}-contract limit.");
            }
            var requiredContracts = new List<string>(requiredValues.Length);
            string? previous = null;
            foreach (var requiredValue in requiredValues)
            {
                if (requiredValue.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"The {label} requiredContracts entries must be strings.");
                }
                var contractId = requiredValue.GetString()!;
                RequireCanonicalPackageId(contractId, $"{label} required contract");
                if (previous is not null && string.CompareOrdinal(previous, contractId) >= 0)
                {
                    throw ContractLockError(
                        "rpc-authoring.order-mismatch",
                        $"The {label} requiredContracts must be an ascending ordinal set.");
                }
                requiredContracts.Add(contractId);
                previous = contractId;
            }
            result.Add(packageId, new(packageId, localName, presence, requiredContracts, consumerRoles));
        }
        return result;
    }

    internal static IReadOnlyList<string> ReadConsumerRoles(JsonElement value, string label)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var roles = value.EnumerateArray().Select(role => role.ValueKind == JsonValueKind.String ? role.GetString()! : "").ToArray();
            if (roles.SequenceEqual(new[] { "app" }) || roles.SequenceEqual(new[] { "runtime" }) || roles.SequenceEqual(new[] { "app", "runtime" }))
                return Array.AsReadOnly(roles);
        }
        throw ContractLockError("package-api.consumer-roles-invalid", $"{label} consumerRoles must be exactly [\"app\"], [\"runtime\"] or [\"app\",\"runtime\"].");
    }

    private static void ValidatePresentConsumerRoles(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("packageImports", out var imports) || imports.ValueKind != JsonValueKind.Object) return;
        foreach (var import in imports.EnumerateObject())
            if (import.Value.ValueKind == JsonValueKind.Object && import.Value.TryGetProperty("consumerRoles", out var roles)) _ = ReadConsumerRoles(roles, import.Name);
    }

    internal static IReadOnlyList<string> ParseConsumerRolesOption(string value)
        => value is "app" or "runtime" or "app,runtime" ? Array.AsReadOnly(value.Split(','))
            : throw ContractLockError("package-api.consumer-roles-invalid", "SunderPackageApiConsumerRoles must be app, runtime or app,runtime.");

    private static IReadOnlyDictionary<string, SunderRpcLockedProviderImport> ReadProviderImports(JsonElement root)
    {
        var map = RequireProperty(root, "providerImports", JsonValueKind.Object, "contract lock");
        var properties = ReadOrderedLockMap(map, "contract lock providerImports");
        var result = new Dictionary<string, SunderRpcLockedProviderImport>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            RequireLocalName(property.Name, "contract lock provider import key");
            var label = $"contract lock provider import '{property.Name}'";
            RequireObject(property.Value, label, ["packageId", "contractId"], ["packageId", "contractId"], []);
            result.Add(property.Name, new(
                property.Name,
                RequirePackageId(property.Value, "packageId", label),
                RequirePackageId(property.Value, "contractId", label)));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, SunderRpcLockedPlatformImport> ReadPlatformImports(
        JsonElement root,
        bool allowDevelopmentRegistryOrigin)
    {
        var map = RequireProperty(root, "platformImports", JsonValueKind.Object, "contract lock");
        var properties = ReadOrderedLockMap(map, "contract lock platformImports");
        var result = new Dictionary<string, SunderRpcLockedPlatformImport>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            RequireLocalName(property.Name, "contract lock platform import key");
            var label = $"contract lock platform import '{property.Name}'";
            RequireObject(
                property.Value,
                label,
                [
                    "authorityKind",
                    "authorityId",
                    "authorityReleaseRange",
                    "includePrerelease",
                    "selectedAuthorityRelease",
                    "selectedAuthorityContentIdentity",
                    "contractId",
                    "resolvedContractVersion",
                    "registryOrigin",
                    "descriptorBytes",
                    "descriptorSha256",
                    "abiSha256",
                    "etag",
                ],
                [
                    "authorityKind",
                    "authorityId",
                    "authorityReleaseRange",
                    "includePrerelease",
                    "selectedAuthorityRelease",
                    "selectedAuthorityContentIdentity",
                    "contractId",
                    "resolvedContractVersion",
                    "descriptorBytes",
                    "descriptorSha256",
                    "abiSha256",
                    "etag",
                ],
                ["registryOrigin"]);
            RequireString(property.Value, "authorityKind", "platform", label);
            RequireString(property.Value, "authorityId", "sunder.core", label);
            var range = RequireVersionRange(property.Value, "authorityReleaseRange", label);
            var includePrerelease = RequireBoolean(property.Value, "includePrerelease", label);
            if (includePrerelease)
            {
                throw new InvalidDataException($"The {label} member 'includePrerelease' must be false.");
            }
            var selectedRelease = RequireSemanticVersion(property.Value, "selectedAuthorityRelease", label);
            ValidateSelectedVersion(selectedRelease, range, includePrerelease, label);
            var selectedContentIdentity = RequireSha256(property.Value, "selectedAuthorityContentIdentity", label);
            var contractId = RequirePackageId(property.Value, "contractId", label);
            var resolvedVersion = RequireSemanticVersion(property.Value, "resolvedContractVersion", label);
            var registryOrigin = ReadOptionalRegistryOrigin(property.Value, label, allowDevelopmentRegistryOrigin);
            var descriptorBytes = RequireBoundedInteger(
                property.Value,
                "descriptorBytes",
                2,
                MaximumDescriptorBytes,
                label);
            var descriptorSha256 = RequireSha256(property.Value, "descriptorSha256", label);
            var abiSha256 = RequireSha256(property.Value, "abiSha256", label);
            var etag = RequireStrongDescriptorEtag(property.Value, descriptorSha256, label);
            result.Add(property.Name, new(
                property.Name,
                "platform",
                "sunder.core",
                range,
                includePrerelease,
                selectedRelease,
                selectedContentIdentity,
                contractId,
                resolvedVersion,
                registryOrigin,
                descriptorBytes,
                descriptorSha256,
                abiSha256,
                etag));
        }
        return result;
    }

    private static string? ReadOptionalRegistryOrigin(JsonElement value, string label, bool allowDevelopmentRegistryOrigin)
    {
        // Absence identifies a local selection. It never supplies an implicit
        // Registry or makes malformed/null origin metadata a local fallback.
        if (!value.TryGetProperty("registryOrigin", out _)) return null;
        var origin = RequireString(value, "registryOrigin", label);
        RequireCanonicalRegistryOrigin(origin, label, allowDevelopmentRegistryOrigin);
        return origin;
    }

    private static SunderRpcContractLockMaterialization ValidateContractLock(
        IReadOnlyDictionary<string, SunderRpcLockedPackageArtifact> artifacts,
        IReadOnlyDictionary<string, SunderRpcLockedPackageImport> packageImports,
        IReadOnlyDictionary<string, SunderRpcLockedProviderImport> providerImports,
        IReadOnlyDictionary<string, SunderRpcLockedPlatformImport> platformImports)
    {
        var authorities = new Dictionary<(string Kind, string Id), AuthorityPin>();
        var contractEvidence = new Dictionary<(string Id, string Version), ContractEvidence>();
        foreach (var artifact in artifacts.Values)
        {
            AddAuthority(
                authorities,
                "package",
                artifact.PackageId,
                new(artifact.SelectedPackageVersion, artifact.SelectedPackageContentIdentity));
            foreach (var contract in artifact.Contracts.Values)
            {
                AddAuthority(authorities, contract.AuthorityKind, contract.AuthorityId, AuthorityPin.From(contract));
                AddContractEvidence(contractEvidence, ContractEvidence.From(contract));
            }
        }
        foreach (var import in platformImports.Values)
        {
            AddAuthority(authorities, import.AuthorityKind, import.AuthorityId, AuthorityPin.From(import));
            AddContractEvidence(contractEvidence, ContractEvidence.From(import));
        }

        var referencedArtifacts = new HashSet<string>(StringComparer.Ordinal);
        var packageLocalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerTuples = new HashSet<(string PackageId, string ContractId)>();
        var platformTuples = new HashSet<(string AuthorityId, string ContractId)>();
        var bundles = new Dictionary<(string Id, string Version), ContractEvidence>();
        var uses = new HashSet<(string Role, string Id, string Version)>();
        var dependencies = new Dictionary<string, AuthorityPin>(StringComparer.Ordinal);

        foreach (var import in packageImports.Values)
        {
            if (!packageLocalNames.Add(import.LocalName))
            {
                throw ContractLockError(
                    "package-api.local-name-collision",
                    $"Package import local name '{import.LocalName}' collides under ASCII case folding.");
            }
            if (!artifacts.TryGetValue(import.PackageId, out var artifact))
            {
                throw ContractLockError(
                    "rpc-authoring.root-mismatch",
                    $"Package import '{import.PackageId}' has no selected package artifact.");
            }
            referencedArtifacts.Add(import.PackageId);
            var packageContracts = artifact.Contracts.Values
                .Where(static contract => string.Equals(contract.AuthorityKind, "package", StringComparison.Ordinal))
                .ToArray();
            if (packageContracts.Length == 0)
            {
                throw ContractLockError(
                    "package-api.consumer-api-empty",
                    $"Package import '{import.PackageId}' has no package-authority public contracts.");
            }
            if (import.RequiredContracts.Any(contractId => !packageContracts.Any(contract =>
                    string.Equals(contract.ContractId, contractId, StringComparison.Ordinal))))
            {
                throw ContractLockError(
                    "rpc-authoring.root-mismatch",
                    $"Package import '{import.PackageId}' requires a contract outside its package-authority public API.");
            }
            AddDependency(dependencies, import.PackageId, AuthorityPin.ForArtifact(artifact));
            foreach (var contract in packageContracts)
            {
                AddMaterializedBundle(bundles, contract);
                foreach (var role in import.ConsumerRoles) uses.Add((role, contract.ContractId, contract.ResolvedContractVersion));
                AddDependency(dependencies, contract.AuthorityId, AuthorityPin.From(contract));
            }
        }

        foreach (var import in providerImports.Values)
        {
            if (!providerKeys.Add(import.Key))
            {
                throw ContractLockError(
                    "rpc-authoring.duplicate-projected-key",
                    $"Provider import key '{import.Key}' collides under ASCII case folding.");
            }
            if (!providerTuples.Add((import.PackageId, import.ContractId)))
            {
                throw ContractLockError(
                    "rpc-authoring.duplicate-projected-key",
                    $"Provider import tuple '{import.PackageId}/{import.ContractId}' is duplicated.");
            }
            if (!artifacts.TryGetValue(import.PackageId, out var artifact)
                || !artifact.Contracts.TryGetValue(import.ContractId, out var contract))
            {
                throw ContractLockError(
                    "rpc-authoring.root-mismatch",
                    $"Provider import '{import.Key}' does not resolve in package artifact '{import.PackageId}'.");
            }
            if (!string.Equals(contract.AuthorityKind, "package", StringComparison.Ordinal))
            {
                throw ContractLockError(
                    "package-api.provider-contract-not-public",
                    $"Provider import '{import.Key}' must select a package-authority public contract.");
            }
            referencedArtifacts.Add(import.PackageId);
            AddMaterializedBundle(bundles, contract);
            AddDependency(dependencies, import.PackageId, AuthorityPin.ForArtifact(artifact));
            AddDependency(dependencies, contract.AuthorityId, AuthorityPin.From(contract));
        }

        foreach (var import in platformImports.Values)
        {
            if (!providerKeys.Add(import.Key))
            {
                throw ContractLockError(
                    "rpc-authoring.duplicate-projected-key",
                    $"Platform import key '{import.Key}' collides with another provider import key.");
            }
            if (!platformTuples.Add((import.AuthorityId, import.ContractId)))
            {
                throw ContractLockError(
                    "rpc-authoring.duplicate-projected-key",
                    $"Platform import tuple '{import.AuthorityId}/{import.ContractId}' is duplicated.");
            }
            AddMaterializedBundle(bundles, ContractEvidence.From(import));
        }

        var unreferenced = artifacts.Keys.FirstOrDefault(packageId => !referencedArtifacts.Contains(packageId));
        if (unreferenced is not null)
        {
            throw ContractLockError(
                "rpc-authoring.unreferenced-artifact",
                $"Package artifact '{unreferenced}' is not referenced by a package or provider import.");
        }

        var providerCount = providerImports.Count + platformImports.Count;
        if (bundles.Count > MaximumLockEntries
            || uses.Count > MaximumLockEntries
            || providerCount > MaximumLockEntries
            || dependencies.Count > MaximumLockEntries)
        {
            throw ContractLockError(
                "rpc-authoring.aggregate-materializability-overflow",
                "The contract lock exceeds an aggregate 128-item materialization limit.");
        }
        return new(bundles.Count, uses.Count, providerCount, dependencies.Count);
    }

    private static JsonProperty[] ReadOrderedLockMap(
        JsonElement value,
        string label,
        bool requireNonempty = false)
    {
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length > MaximumLockEntries || requireNonempty && properties.Length == 0)
        {
            throw new InvalidDataException(
                $"The {label} must contain {(requireNonempty ? "between 1 and " : "at most ")}{MaximumLockEntries} entries.");
        }
        var keys = properties.Select(static property => property.Name).ToArray();
        if (!keys.SequenceEqual(keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw ContractLockError("rpc-authoring.order-mismatch", $"The {label} is not in ascending ordinal key order.");
        }
        return properties;
    }

    private static string RequireVersionRange(JsonElement value, string name, string label)
    {
        var result = RequireString(value, name, label);
        if (!PackageVersionRange.TryParse(result, out var parsed)
            || !string.Equals(result, parsed.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {label} member '{name}' is not a canonical package version range.");
        }
        return result;
    }

    private static bool RequireBoolean(JsonElement value, string name, string label)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"The {label} member '{name}' must be a boolean.");
        }
        return property.GetBoolean();
    }

    private static int RequireBoundedInteger(
        JsonElement value,
        string name,
        int minimum,
        int maximum,
        string label)
        => checked((int)RequireBoundedInteger(value, name, (long)minimum, maximum, label));

    private static long RequireBoundedInteger(
        JsonElement value,
        string name,
        long minimum,
        long maximum,
        string label)
    {
        var result = RequireSafeInteger(value, name, label);
        if (result < minimum || result > maximum)
        {
            throw new InvalidDataException(
                $"The {label} member '{name}' must be between {minimum.ToString(CultureInfo.InvariantCulture)} "
                + $"and {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }
        return result;
    }

    private static string RequireOneOf(JsonElement value, string name, string label, params string[] permitted)
    {
        var result = RequireString(value, name, label);
        if (!permitted.Contains(result, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"The {label} member '{name}' must be one of: {string.Join(", ", permitted)}.");
        }
        return result;
    }

    private static void RequireCanonicalPackageId(string value, string label)
    {
        if (!PackageId.TryParse(value, out _))
        {
            throw new InvalidDataException($"The {label} '{value}' is not a canonical package identity.");
        }
    }

    private static string RequireLocalName(JsonElement value, string name, string label)
    {
        var result = RequireString(value, name, label);
        RequireLocalName(result, $"{label} member '{name}'");
        return result;
    }

    private static void RequireLocalName(string value, string label)
    {
        if (value.Length is < 1 or > 128
            || value[0] is not (>= 'a' and <= 'z')
            || value.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidDataException($"The {label} '{value}' is not a canonical local name.");
        }
    }

    private static string RequireStrongDescriptorEtag(
        JsonElement value,
        string descriptorSha256,
        string label)
    {
        var result = RequireString(value, "etag", label);
        if (!string.Equals(result, $"\"{descriptorSha256}\"", StringComparison.Ordinal))
        {
            throw ContractLockError(
                "rpc-authoring.hash-mismatch",
                $"The {label} ETag does not match descriptorSha256.");
        }
        return result;
    }

    private static void ValidateSelectedVersion(
        string selectedVersion,
        string range,
        bool includePrerelease,
        string label)
    {
        var parsedVersion = SemanticVersion.Parse(selectedVersion);
        if (!PackageVersionRange.IsSatisfiedBy(selectedVersion, range)
            || !includePrerelease && parsedVersion.Prerelease is not null)
        {
            throw ContractLockError(
                "rpc-authoring.version-selection-mismatch",
                $"The {label} selected version '{selectedVersion}' does not satisfy its reviewed range and prerelease policy.");
        }
    }

    internal static SunderPackagePublicApiInventory ValidatePackagePublicInventory(SunderRpcLockedPackageArtifact artifact)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            // Project the lock-specific names and omit its ETag; Format owns inventory validation and hash bytes.
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("packageId", artifact.PackageId);
            writer.WriteString("packageVersion", artifact.SelectedPackageVersion);
            writer.WriteString("packageContentIdentity", artifact.SelectedPackageContentIdentity);
            writer.WriteNumber("publicContractCount", artifact.PublicContractCount);
            writer.WriteStartArray("contracts");
            foreach (var contract in artifact.Contracts.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("contractId", contract.ContractId);
                writer.WriteString("contractVersion", contract.ResolvedContractVersion);
                writer.WriteNumber("descriptorBytes", contract.DescriptorBytes);
                writer.WriteString("descriptorSha256", contract.DescriptorSha256);
                writer.WriteString("abiSha256", contract.AbiSha256);
                writer.WriteString("artifactSource", contract.ArtifactSource);
                writer.WriteString("authorityKind", contract.AuthorityKind);
                writer.WriteString("authorityId", contract.AuthorityId);
                writer.WriteString("selectedAuthorityRelease", contract.SelectedAuthorityRelease);
                writer.WriteString("selectedAuthorityContentIdentity", contract.SelectedAuthorityContentIdentity);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return SunderPackagePublicApiInventory.ParseVerifyHash(output.ToArray(), artifact.PublicInventorySha256);
    }

    private static void AddAuthority(
        IDictionary<(string Kind, string Id), AuthorityPin> authorities,
        string kind,
        string id,
        AuthorityPin pin)
    {
        var key = (kind, id);
        if (authorities.TryGetValue(key, out var existing) && existing != pin)
        {
            throw ContractLockError(
                "rpc-authoring.authority-mismatch",
                $"Authority '{kind}/{id}' has conflicting selected release or content evidence.");
        }
        authorities[key] = pin;
    }

    private static void AddContractEvidence(
        IDictionary<(string Id, string Version), ContractEvidence> contracts,
        ContractEvidence evidence)
    {
        var key = (evidence.ContractId, evidence.ContractVersion);
        if (contracts.TryGetValue(key, out var existing) && existing != evidence)
        {
            throw ContractLockError(
                "rpc-authoring.evidence-mismatch",
                $"Contract '{evidence.ContractId}' version '{evidence.ContractVersion}' has conflicting immutable evidence.");
        }
        contracts[key] = evidence;
    }

    private static void AddMaterializedBundle(
        IDictionary<(string Id, string Version), ContractEvidence> bundles,
        SunderRpcLockedArtifactContract contract)
        => AddMaterializedBundle(bundles, ContractEvidence.From(contract));

    private static void AddMaterializedBundle(
        IDictionary<(string Id, string Version), ContractEvidence> bundles,
        ContractEvidence evidence)
    {
        var key = (evidence.ContractId, evidence.ContractVersion);
        if (bundles.TryGetValue(key, out var existing) && existing != evidence)
        {
            throw ContractLockError(
                "rpc-authoring.evidence-mismatch",
                $"Materialized contract '{evidence.ContractId}' version '{evidence.ContractVersion}' conflicts across imports.");
        }
        bundles[key] = evidence;
    }

    private static void AddDependency(
        IDictionary<string, AuthorityPin> dependencies,
        string packageId,
        AuthorityPin pin)
    {
        if (dependencies.TryGetValue(packageId, out var existing) && existing != pin)
        {
            throw ContractLockError(
                "package-api.authority-release-conflict",
                $"Package dependency '{packageId}' resolves to conflicting immutable releases.");
        }
        dependencies[packageId] = pin;
    }

    private static InvalidDataException ContractLockError(string diagnosticId, string message)
        => new($"{diagnosticId}: {message}");

    private sealed record AuthorityPin(
        string Release,
        string ContentIdentity)
    {
        public static AuthorityPin ForArtifact(SunderRpcLockedPackageArtifact artifact)
            => new(artifact.SelectedPackageVersion, artifact.SelectedPackageContentIdentity);

        public static AuthorityPin From(SunderRpcLockedArtifactContract contract)
            => new(
                contract.SelectedAuthorityRelease,
                contract.SelectedAuthorityContentIdentity);

        public static AuthorityPin From(SunderRpcLockedPlatformImport import)
            => new(
                import.SelectedAuthorityRelease,
                import.SelectedAuthorityContentIdentity);
    }

    // ArtifactSource is authenticated per inventory, not part of cross-artifact contract identity.
    private sealed record ContractEvidence(
        string ContractId,
        string ContractVersion,
        int DescriptorBytes,
        string DescriptorSha256,
        string AbiSha256,
        string AuthorityKind,
        string AuthorityId,
        AuthorityPin Authority)
    {
        public static ContractEvidence From(SunderRpcLockedArtifactContract contract)
            => new(
                contract.ContractId,
                contract.ResolvedContractVersion,
                contract.DescriptorBytes,
                contract.DescriptorSha256,
                contract.AbiSha256,
                contract.AuthorityKind,
                contract.AuthorityId,
                AuthorityPin.From(contract));

        public static ContractEvidence From(SunderRpcLockedPlatformImport import)
            => new(
                import.ContractId,
                import.ResolvedContractVersion,
                import.DescriptorBytes,
                import.DescriptorSha256,
                import.AbiSha256,
                import.AuthorityKind,
                import.AuthorityId,
                AuthorityPin.From(import));
    }
}
