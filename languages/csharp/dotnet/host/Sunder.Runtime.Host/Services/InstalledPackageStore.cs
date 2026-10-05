using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class InstalledPackageStore(RuntimePackagePaths paths)
{
    private const int CurrentSchemaVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<InstalledPackageRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.StateFilePath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(paths.StateFilePath, cancellationToken);
        int schemaVersion;
        try
        {
            schemaVersion = ReadSchemaVersion(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Installed package catalog '{paths.StateFilePath}' is invalid. Run 'sunder runtime reset --yes' to reset derived state while preserving package data.", ex);
        }

        if (schemaVersion != CurrentSchemaVersion)
        {
            throw UnsupportedSchema(schemaVersion);
        }

        InstalledPackageStateFile? state;
        try
        {
            state = JsonSerializer.Deserialize<InstalledPackageStateFile>(
                json,
                JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Installed package catalog '{paths.StateFilePath}' is invalid. Run 'sunder runtime reset --yes' to reset derived state while preserving package data.", ex);
        }

        if (state?.SchemaVersion != CurrentSchemaVersion)
        {
            throw UnsupportedSchema(state?.SchemaVersion ?? schemaVersion);
        }

        if (state.Packages is null)
        {
            throw new InvalidDataException($"Installed package catalog '{paths.StateFilePath}' is missing packages.");
        }
        ValidateCatalog(state.Packages);
        return state.Packages;
    }

    public async Task<InstalledPackageRecord?> GetAsync(string packageId, CancellationToken cancellationToken = default)
        => (await ListAsync(cancellationToken)).FirstOrDefault(package =>
            string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    internal async Task WriteAsync(
        IReadOnlyList<InstalledPackageRecord> packages,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(paths.StateFilePath))
        {
            _ = await ListAsync(cancellationToken);
        }

        ValidateCatalog(packages);
        Directory.CreateDirectory(paths.CatalogRootPath);
        var state = new InstalledPackageStateFile(
            CurrentSchemaVersion,
            packages.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase).ToArray());
        await DurableJsonDocument.WriteAsync(paths.StateFilePath, state, JsonOptions, cancellationToken);
    }

    internal void ValidateCatalog(IReadOnlyList<InstalledPackageRecord> packages)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (!PackageId.TryParse(package.PackageId, out _))
            {
                throw new InvalidDataException($"Installed package catalog contains invalid package id '{package.PackageId}'.");
            }

            if (!SemanticVersion.TryParse(package.Version, out _))
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has invalid version '{package.Version}'.");
            }

            if (!packageIds.Add(package.PackageId))
            {
                throw new InvalidDataException($"Installed package catalog contains duplicate package id '{package.PackageId}'.");
            }

            ValidateProvenance(package);
            ValidateCapabilityGrants(package);
            ValidateExecutionRuntimeBindings(package);
            if (InstalledPackageProjection.Keys(package) is { } acquired
                && package.ExecutionRuntimeBindings.Any(binding => !InstalledPackageProjection.Includes(acquired, binding.Role, binding.Rid)))
                throw new InvalidDataException("Installed package acquisition scope is invalid or disagrees with its runtime bindings.");
            if (package.SourcePackageContentIdentity is not null && !IsCanonicalHash(package.SourcePackageContentIdentity))
                throw new InvalidDataException("Installed source package content identity is invalid.");
            package.Policy?.Validate(package);
            package.Compatibility?.Validate(package);

            if (!paths.IsCanonicalInstalledPath(package.PackageId, package.Version, package.InstallPath))
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' path '{package.InstallPath}' is outside its current versioned package root.");
            }

            var expectedManifestPath = Path.GetFullPath(Path.Combine(
                package.InstallPath,
                SunderPackageFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathsEqual(expectedManifestPath, package.ManifestPath))
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' manifest path '{package.ManifestPath}' is not canonical.");
            }
            if (!IsCanonicalHash(package.ContentIdentity))
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid content identity.");
            }
            if (package.ContentInventory is null
                || package.ContentInventory.Count == 0
                || package.ContentInventory.Count > SunderPackageFormat.MaxContentIndexEntries)
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid content inventory.");
            }
            var inventoryPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var content in package.ContentInventory)
            {
                if (content is null
                    || !ArchiveRelativePath.TryParse(
                        content.Path,
                        SunderPackageFormat.MaxArchivePathLength,
                        SunderPackageFormat.MaxArchivePathDepth,
                        out var contentPath,
                        out _)
                    || !SunderPackageFormat.IsAllowedArchivePath(contentPath.ToString())
                    || SunderPackageFormat.IsContentIndexPath(contentPath.ToString())
                    || !inventoryPaths.Add(contentPath.ToString())
                    || !IsCanonicalHash(content.Sha256)
                    || content.Size < 0)
                {
                    throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid content inventory entry.");
                }
            }
            if (!inventoryPaths.Contains(SunderPackageFormat.ManifestPath))
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' content inventory is missing its manifest.");
            }

            if (package.DependsOn is null)
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' is missing dependencies.");
            }
            foreach (var dependency in package.DependsOn)
            {
                if (!PackageId.TryParse(dependency.PackageId, out _)
                    || !PackageVersionRange.TryParse(dependency.VersionRange, out _))
                {
                    throw new InvalidDataException(
                        $"Installed package '{package.PackageId}' has invalid dependency '{dependency.PackageId}' range '{dependency.VersionRange}'.");
                }
            }
        }
    }

    public InstalledPackageDescriptor ToDescriptor(InstalledPackageRecord package)
    {
        var validation = InstalledPackageProjection.ValidateAsync(package)
            .GetAwaiter()
            .GetResult();
        if (!validation.Success || validation.Manifest is null)
        {
            throw new InvalidDataException(
                $"Installed package '{package.PackageId}' is invalid: {string.Join(" | ", validation.Errors)}");
        }
        var manifest = validation.Manifest;
        PackageManifestAdmissionPolicy.EnsureSupported(manifest);
        if (!string.Equals(manifest.Id, package.PackageId, StringComparison.Ordinal)
            || !string.Equals(manifest.Version, package.Version, StringComparison.Ordinal)
            || !string.Equals(manifest.Name, package.Name, StringComparison.Ordinal)
            || !string.Equals(manifest.Summary, package.Summary, StringComparison.Ordinal)
            || !string.Equals(manifest.Icon, package.Icon, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Installed package '{package.PackageId}' catalog identity does not match its strict manifest.");
        }
        if (validation.ContentIndex is null
            || !PackageSessionPreparer.InventoryMatches(validation.ContentIndex, package.ContentInventory)
            || !string.Equals(
                PackageSessionPreparer.ComputeContentIdentity(package.InstallPath),
                package.ContentIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Installed package '{package.PackageId}' content identity or inventory does not match its strict package content.");
        }
        var icon = string.IsNullOrWhiteSpace(package.Icon)
            ? null
            : new PackageIconDescriptor(null, package.Icon);
        var dependencies = package.DependsOn
            .Select(dependency => new PackageDependencyDescriptor(dependency.PackageId, dependency.VersionRange))
            .ToArray();
        return new InstalledPackageDescriptor(
            package.PackageId,
            package.Name,
            package.Version,
            PackageTargetSelection.GetHostRoles(manifest, InstalledPackageProjection.Keys(package)),
            package.Summary,
            icon,
            package.IsEnabled,
            dependencies,
            package.InstalledAtUtc,
            !package.IsEnabled ? "Disabled" : package.Policy is { IsBlocked: true } ? "PolicyBlocked" : null,
            (manifest.UsesContracts ?? [])
                .Where(use => use is not null && (package.AcquiredTargets is null || package.AcquiredTargets.Any(target => target.Role == use.Role)))
                .Select(static use => new PackageRpcContractUseDescriptor(
                    use!.Role!,
                    use.ContractId!,
                    use.ContractVersion!,
                    use.AbiSha256!,
                    use.Required!.Value,
                    (use.Actions ?? []).Where(static action => action is not null).Select(static action => action!).ToArray()))
                .ToArray(),
            package.Provenance.ToDescriptor(),
            PackageSensitiveCapabilityPolicy.GetEffectiveCapabilities(package)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ProjectExecutionRuntimeBindings(package, manifest))
        {
            PolicyErrorCode = package.Policy?.ErrorCode,
        };
    }

    public async Task<string?> TryResolvePackageAssetPathAsync(
        string packageId,
        string assetPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(assetPath))
        {
            return null;
        }

        var package = await GetAsync(packageId, cancellationToken);
        return package is null
            ? null
            : PackageAssetPathResolver.TryResolveInstalledAssetPath(package.InstallPath, assetPath);
    }

    private static bool IsCanonicalHash(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static int ReadSchemaVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Installed package catalog root must be an object.");
        }

        var schemaProperties = document.RootElement
            .EnumerateObject()
            .Where(static property => property.NameEquals("schemaVersion"))
            .ToArray();
        if (schemaProperties.Length != 1
            || schemaProperties[0].Value.ValueKind != JsonValueKind.Number
            || !schemaProperties[0].Value.TryGetInt32(out var schemaVersion))
        {
            throw new JsonException("Installed package catalog must contain one integer schemaVersion.");
        }

        return schemaVersion;
    }

    private InvalidDataException UnsupportedSchema(int schemaVersion)
        => new(
            $"Installed package catalog '{paths.StateFilePath}' uses unsupported pre-release schema version {schemaVersion}. "
            + "No migration or reinterpretation is supported. Stop the Runtime and run 'sunder runtime reset --yes'; "
            + "the data-safe reset preserves package data, settings, secrets, credentials, workspaces, and user files.");

    private static void ValidateProvenance(InstalledPackageRecord package)
    {
        var provenance = package.Provenance
            ?? throw new InvalidDataException($"Installed package '{package.PackageId}' is missing provenance.");
        if (!Enum.IsDefined(provenance.SourceKind) || !Enum.IsDefined(provenance.VersionPolicy))
        {
            throw new InvalidDataException($"Installed package '{package.PackageId}' has invalid provenance.");
        }

        if (provenance.SourceKind == InstalledPackageSourceKind.Registry)
        {
            Uri normalizedOrigin;
            try
            {
                normalizedOrigin = RegistryOrigin.Normalize(provenance.RegistryOrigin ?? string.Empty);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid Registry origin.", exception);
            }

            if (!string.Equals(normalizedOrigin.AbsoluteUri, provenance.RegistryOrigin, StringComparison.Ordinal)
                || !PackageId.TryParse(provenance.SourcePackageId, out _)
                || !string.Equals(provenance.SourcePackageId, package.PackageId, StringComparison.Ordinal)
                || !IsCanonicalHash(provenance.SourceIdentity)
                || provenance.VersionRange is not null && !PackageVersionRange.TryParse(provenance.VersionRange, out _))
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has invalid Registry provenance.");
            }

            var validSelection = provenance.VersionPolicy switch
            {
                InstalledPackageVersionPolicy.FollowTag => !string.IsNullOrWhiteSpace(provenance.RequestedTag)
                    && provenance.RequestedVersion is null,
                InstalledPackageVersionPolicy.ExplicitVersion => SemanticVersion.TryParse(provenance.RequestedVersion, out _)
                    && provenance.RequestedTag is null
                    && provenance.VersionRange is null,
                InstalledPackageVersionPolicy.TransitiveDependency => provenance.RequestedTag is null
                    && provenance.RequestedVersion is null
                    && provenance.VersionRange is null
                    && !provenance.IncludePrerelease,
                _ => false,
            };
            if (!validSelection)
            {
                throw new InvalidDataException($"Installed package '{package.PackageId}' has an invalid Registry version policy.");
            }
            return;
        }

        if (provenance.VersionPolicy != InstalledPackageVersionPolicy.Unmanaged
            || provenance.RegistryOrigin is not null
            || provenance.SourcePackageId is not null
            || provenance.RequestedTag is not null
            || provenance.RequestedVersion is not null
            || provenance.VersionRange is not null
            || provenance.IncludePrerelease
            || provenance.SourceKind == InstalledPackageSourceKind.LocalArchive && !IsCanonicalHash(provenance.SourceIdentity)
            || provenance.SourceKind == InstalledPackageSourceKind.Unknown && provenance.SourceIdentity is not null
            || provenance.SourceKind == InstalledPackageSourceKind.Development && string.IsNullOrWhiteSpace(provenance.SourceIdentity))
        {
            throw new InvalidDataException($"Installed package '{package.PackageId}' has invalid unmanaged provenance.");
        }
    }

    private static void ValidateCapabilityGrants(InstalledPackageRecord package)
    {
        var grants = package.CapabilityGrants
                     ?? throw new InvalidDataException(
                         $"Installed package '{package.PackageId}' is missing capability grants.");
        var manifestSha256 = PackageSensitiveCapabilityPolicy.GetManifestSha256(package);
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (grant is null
                || !PackageSensitiveCapabilityPolicy.IsSensitive(grant.Capability)
                || !capabilities.Add(grant.Capability)
                || !string.Equals(grant.PackageVersion, package.Version, StringComparison.Ordinal)
                || !string.Equals(grant.ManifestSha256, manifestSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' contains an invalid sensitive capability grant.");
            }
        }
    }

    private static void ValidateExecutionRuntimeBindings(InstalledPackageRecord package)
    {
        var bindings = package.ExecutionRuntimeBindings
                       ?? throw new InvalidDataException(
                           $"Installed package '{package.PackageId}' is missing execution-runtime bindings.");
        if (bindings.Count > SunderPackageFormat.MaxTargets)
        {
            throw new InvalidDataException(
                $"Installed package '{package.PackageId}' contains too many execution-runtime bindings.");
        }

        var targetKeys = new HashSet<(string Role, string Rid)>();
        foreach (var binding in bindings)
        {
            if (binding is null
                || !string.Equals(binding.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
                || !SunderPackageFormat.IsRuntimeIdentifier(binding.Rid)
                || !IsExecutionRuntimePair(binding.RuntimeId, binding.ModuleAbi)
                || !SemanticVersion.TryParse(binding.RuntimeVersion, out var runtimeVersion)
                || runtimeVersion.Prerelease is not null
                || binding.RuntimePackagingRevision is <= 0 or > SunderExecutionRuntimeFormat.MaxPackagingRevision
                || !IsCanonicalHash(binding.RuntimeManifestSha256)
                || !IsCanonicalHash(binding.RuntimeArtifactSha256)
                || binding.RuntimeArtifactSize is <= 0 or > SunderExecutionRuntimeFormat.MaxArchiveBytes
                || !IsCanonicalRegistryOrigin(binding.RegistryOrigin)
                || binding.AcquisitionSource is not (RuntimeExecutionRuntimeAcquisitionSources.Registry
                    or RuntimeExecutionRuntimeAcquisitionSources.LocalImport
                    or RuntimeExecutionRuntimeAcquisitionSources.Development)
                || !IsLowerIdentifier(binding.SigningKeyId, SunderExecutionRuntimeFormat.MaxKeyIdLength)
                || binding.TrustStoreGeneration <= 0
                || binding.RuntimePolicyRevision <= 0
                || binding.RuntimeLifecycleState is not ("supported" or "deprecated" or "retired" or "revoked")
                || !IsCanonicalUtc(binding.PolicyObservedAtUtc)
                || !IsCanonicalUtc(binding.ResolvedAtUtc)
                || !targetKeys.Add((binding.Role, binding.Rid)))
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' contains an invalid execution-runtime binding.");
            }
        }
    }

    private static IReadOnlyList<PackageTargetExecutionRuntimeBinding> ProjectExecutionRuntimeBindings(
        InstalledPackageRecord package,
        SunderPackageManifest manifest)
        => package.ExecutionRuntimeBindings.Select(binding =>
        {
            var target = InstalledPackageProjection.Targets(package, manifest)
                .Where(static target => target is not null)
                .Select(static target => target!)
                .SingleOrDefault(target =>
                    string.Equals(target.Role, binding.Role, StringComparison.Ordinal)
                    && string.Equals(target.Rid, binding.Rid, StringComparison.Ordinal));
            var requirement = target?.ExecutionRuntime;
            if (requirement is null
                || !string.Equals(requirement.Id, binding.RuntimeId, StringComparison.Ordinal)
                || !string.Equals(requirement.ModuleAbi, binding.ModuleAbi, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' execution-runtime binding for target "
                    + $"'{binding.Role}/{binding.Rid}' does not match its strict manifest requirement.");
            }

            return new PackageTargetExecutionRuntimeBinding(
                binding.Role,
                binding.Rid,
                new RuntimeExecutionRuntimeRequirement(
                    binding.RuntimeId,
                    requirement.VersionRange!,
                    binding.ModuleAbi),
                new RuntimeExecutionRuntimeIdentity(
                    binding.RuntimeId,
                    binding.RuntimeVersion,
                    binding.RuntimePackagingRevision,
                    binding.Rid,
                    binding.ModuleAbi,
                    binding.RuntimeManifestSha256,
                    binding.RuntimeArtifactSha256),
                binding.RuntimeArtifactSize,
                binding.RegistryOrigin,
                binding.AcquisitionSource,
                binding.SigningKeyId,
                binding.TrustStoreGeneration,
                new RuntimeExecutionRuntimeLifecycle(
                    binding.RuntimeLifecycleState,
                    SupportedFromUtc: null,
                    DeprecatedAtUtc: null,
                    InstallBlockedAtUtc: null,
                    ActivationBlockedAtUtc: null,
                    RevokedAtUtc: null,
                    PolicyMessage: null,
                    binding.RuntimePolicyRevision,
                    binding.PolicyObservedAtUtc),
                binding.ResolvedAtUtc);
        }).ToArray();

    private static bool IsExecutionRuntimePair(string? runtimeId, string? moduleAbi)
        => runtimeId switch
        {
            SunderExecutionRuntimeFormat.DotNetRuntimeId => string.Equals(
                moduleAbi,
                SunderExecutionRuntimeFormat.DotNetModuleAbi,
                StringComparison.Ordinal),
            SunderExecutionRuntimeFormat.NodeRuntimeId => string.Equals(
                moduleAbi,
                SunderExecutionRuntimeFormat.NodeModuleAbi,
                StringComparison.Ordinal),
            _ => false,
        };

    private static bool IsCanonicalRegistryOrigin(string? value)
    {
        try
        {
            return value is not null
                   && value.Length <= SunderExecutionRuntimeFormat.MaxSourceUriLength
                   && string.Equals(RegistryOrigin.Normalize(value).AbsoluteUri, value, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsLowerIdentifier(string? value, int maxLength)
        => !string.IsNullOrEmpty(value)
           && value.Length <= maxLength
           && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && !value.Contains("..", StringComparison.Ordinal)
           && value.Split('.').All(static segment => segment.Length > 0
                                                       && segment[0] != '-'
                                                       && segment[^1] != '-')
           && value.All(static character => character is >= 'a' and <= 'z'
                                                   or >= '0' and <= '9'
                                                   or '.'
                                                   or '-');

    private static bool IsCanonicalUtc(DateTimeOffset value)
        => value != default && value.Offset == TimeSpan.Zero;

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
