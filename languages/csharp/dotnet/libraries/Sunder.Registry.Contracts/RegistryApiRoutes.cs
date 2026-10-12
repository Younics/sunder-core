namespace Sunder.Registry.Contracts;

public static class RegistryApiRoutes
{
    public const string PublicationAuthorityPolicy = "/api/v1/me/publication-authority-policy";
    public const string Root = "/api/v1";
    public const string PolicySynchronization = Root + "/policy/synchronize";
    public const string PackageCohortAvailability = Root + "/me/package-cohorts/availability";
    public const string Contracts = Root + "/contracts";
    public const string PlatformContracts = Root + "/platform-contracts/sunder.core";
    public const string PlatformContractResolve = PlatformContracts + "/resolve";
    public const string CliAuth = Root + "/cli-auth";
    public const string CliAuthAuthorize = CliAuth + "/authorize";
    public const string CliAuthToken = CliAuth + "/token";
    public const string CurrentUser = Root + "/me";
    public const string CurrentUserProfile = CurrentUser + "/profile";
    public const string CurrentUserPackages = CurrentUser + "/packages";
    public const string CurrentUserStacks = CurrentUser + "/stacks";
    public const string CurrentUserUsers = CurrentUser + "/users";
    public const string CurrentUserCliTokens = CurrentUser + "/tokens";
    public const string CurrentUserPublishTokens = CurrentUser + "/publish-tokens";
    public const string CurrentUserPackageReservations = CurrentUser + "/package-reservations";
    public const string CurrentUserStackReservations = CurrentUser + "/stack-reservations";
    public const string CurrentUserContractAuthorities = CurrentUser + "/contract-authorities";
    public const string Packages = Root + "/packages";
    public const string PackageResolveUpdates = Packages + "/resolve-updates";
    public const string PackageResolveInstallPlan = Packages + "/resolve-install-plan";
    public const string PackageResolvePackageChanges = Packages + "/resolve-package-changes";
    public const string PackagePublish = Packages + "/publish";
    public const string Stacks = Root + "/stacks";
    public const string StackPublish = Stacks + "/publish";
    public const string DevelopmentPackages = Root + "/dev/packages";
    public const string DevelopmentPackagePublishLocal = DevelopmentPackages + "/publish/local";
    public const string DevelopmentStacks = Root + "/dev/stacks";
    public const string DevelopmentStackPublishLocal = DevelopmentStacks + "/publish/local";

    public static string ContractVersions(string contractId)
        => $"{Contracts}/{Segment(contractId, nameof(contractId))}/versions";

    public static string ContractVersion(string contractId, string version)
        => $"{ContractVersions(contractId)}/{Segment(version, nameof(version))}";

    public static string ContractVersionDescriptor(string contractId, string version)
        => $"{ContractVersion(contractId, version)}/descriptor";

    public static string ContractResolve(string contractId)
        => $"{Contracts}/{Segment(contractId, nameof(contractId))}/resolve";

    public static string CurrentUserCliToken(Guid tokenId)
        => $"{CurrentUserCliTokens}/{tokenId:D}";

    public static string CurrentUserPublishToken(Guid tokenId)
        => $"{CurrentUserPublishTokens}/{tokenId:D}";

    public static string CurrentUserPackageReservation(string packageId)
        => $"{CurrentUserPackageReservations}/{Segment(packageId, nameof(packageId))}";

    public static string CurrentUserStackReservation(string stackId)
        => $"{CurrentUserStackReservations}/{Segment(stackId, nameof(stackId))}";

    public static string CurrentUserPackageReservationRenewal(string packageId)
        => $"{CurrentUserPackageReservation(packageId)}/renew";

    public static string CurrentUserPackageReservationCancellation(string packageId)
        => $"{CurrentUserPackageReservation(packageId)}/cancel";

    public static string CurrentUserStackReservationRenewal(string stackId)
        => $"{CurrentUserStackReservation(stackId)}/renew";

    public static string CurrentUserStackReservationCancellation(string stackId)
        => $"{CurrentUserStackReservation(stackId)}/cancel";

    public static string CurrentUserContractAuthority(string contractId)
        => $"{CurrentUserContractAuthorities}/{Segment(contractId, nameof(contractId))}";

    public static string CurrentUserContractAuthorityRenewal(string contractId)
        => $"{CurrentUserContractAuthority(contractId)}/renew";

    public static string CurrentUserContractAuthorityCancellation(string contractId)
        => $"{CurrentUserContractAuthority(contractId)}/cancel";

    public static string CurrentUserContractProviderPolicy(string contractId)
        => $"{CurrentUserContractAuthority(contractId)}/provider-policy";

    public static string CurrentUserPackage(string packageId)
        => $"{CurrentUserPackages}/{Segment(packageId, nameof(packageId))}";

    public static string CurrentUserPackageProfile(string packageId)
        => $"{CurrentUserPackage(packageId)}/profile";

    public static string CurrentUserPackageMedia(string packageId)
        => $"{CurrentUserPackage(packageId)}/media";

    public static string CurrentUserPackageMedia(string packageId, Guid mediaId)
        => $"{CurrentUserPackageMedia(packageId)}/{mediaId:D}";

    public static string CurrentUserStack(string stackId)
        => $"{CurrentUserStacks}/{Segment(stackId, nameof(stackId))}";

    public static string CurrentUserStackProfile(string stackId)
        => $"{CurrentUserStack(stackId)}/profile";

    public static string CurrentUserStackMedia(string stackId)
        => $"{CurrentUserStack(stackId)}/media";

    public static string CurrentUserStackMedia(string stackId, Guid mediaId)
        => $"{CurrentUserStackMedia(stackId)}/{mediaId:D}";

    public static string Package(string packageId)
        => $"{Packages}/{Segment(packageId, nameof(packageId))}";

    public static string PackageVersions(string packageId)
        => $"{Package(packageId)}/versions";

    public static string PackageVersion(string packageId, string version)
        => $"{PackageVersions(packageId)}/{Segment(version, nameof(version))}";

    public static string PackageVersionIcon(string packageId, string version)
        => $"{PackageVersion(packageId, version)}/icon";

    public static string PackageVersionCanonical(string packageId, string version)
        => $"{PackageVersion(packageId, version)}/canonical";

    public static string PackageVersionPublicApiInventory(string packageId, string version)
        => $"{PackageVersion(packageId, version)}/public-api";

    public static string PlatformContractRelease(string releaseVersion)
        => $"{PlatformContracts}/releases/{Segment(releaseVersion, nameof(releaseVersion))}";

    public static string PlatformContractCatalog(string releaseVersion)
        => $"{PlatformContractRelease(releaseVersion)}/catalog";

    public static string PlatformContractRevocation(string releaseVersion)
        => $"{PlatformContractRelease(releaseVersion)}/revoke";

    public static string PackageVersionSharedProjectionDownload(string packageId, string version)
        => $"{PackageVersion(packageId, version)}/projections/shared/download";

    public static string PackageVersionTargetProjectionDownload(
        string packageId,
        string version,
        string role,
        string rid)
        => $"{PackageVersion(packageId, version)}/projections/{Segment(role, nameof(role))}/{Segment(rid, nameof(rid))}/download";

    public static string PackageView(string packageId)
        => $"{Package(packageId)}/view";

    public static string PackageStar(string packageId)
        => $"{Package(packageId)}/star";

    public static string PackageResolve(string packageId)
        => $"{Package(packageId)}/resolve";

    public static string PackageDistTags(string packageId)
        => $"{Package(packageId)}/dist-tags";

    public static string PackageDistTag(string packageId, string tag)
        => $"{PackageDistTags(packageId)}/{Segment(tag, nameof(tag))}";

    public static string PackageVersionYank(string packageId, string version)
        => $"{PackageVersion(packageId, version)}/yank";

    public static string PackageSecurityBlock(string packageId)
        => $"{Package(packageId)}/security-block";

    public static string PackageVersionSecurityBlock(string packageId, string version)
        => $"{PackageVersion(packageId, version)}/security-block";

    public static string StackRevision(string stackId, long revision)
        => revision > 0 ? $"{Stack(stackId)}/revisions/{revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : throw new ArgumentOutOfRangeException(nameof(revision));

    public static string StackRevisionDownload(string stackId, long revision)
        => $"{StackRevision(stackId, revision)}/download";

    public static string StackVisibility(string stackId)
        => $"{Stack(stackId)}/visibility";

    public static string StackPolicy(string stackId)
        => $"{Stack(stackId)}/policy";

    public static string StackSecurityBlock(string stackId)
        => $"{Stack(stackId)}/security-block";

    public static string PackageVersionDeprecation(string packageId, string version)
        => $"{PackageVersion(packageId, version)}/deprecation";

    public static string PackageProfile(string packageId)
        => $"{Package(packageId)}/profile";

    public static string PackageMedia(string packageId, Guid mediaId)
        => $"{Package(packageId)}/media/{mediaId:D}";

    public static string PackageMaintainers(string packageId)
        => $"{Package(packageId)}/maintainers";

    public static string PackageMaintainer(string packageId, string userId)
        => $"{PackageMaintainers(packageId)}/{Segment(userId, nameof(userId))}";

    public static string Stack(string stackId)
        => $"{Stacks}/{Segment(stackId, nameof(stackId))}";

    public static string StackView(string stackId)
        => $"{Stack(stackId)}/view";

    public static string StackDownload(string stackId)
        => $"{Stack(stackId)}/download";

    public static string StackStar(string stackId)
        => $"{Stack(stackId)}/star";

    public static string StackProfile(string stackId)
        => $"{Stack(stackId)}/profile";

    public static string StackMedia(string stackId, Guid mediaId)
        => $"{Stack(stackId)}/media/{mediaId:D}";

    public static string StackMaintainers(string stackId)
        => $"{Stack(stackId)}/maintainers";

    public static string StackMaintainer(string stackId, string userId)
        => $"{StackMaintainers(stackId)}/{Segment(userId, nameof(userId))}";

    public static string ToHttpClientRelativePath(string serverRootedPath)
    {
        ArgumentNullException.ThrowIfNull(serverRootedPath);
        if (serverRootedPath.Length < 2
            || serverRootedPath[0] != '/'
            || serverRootedPath[1] == '/'
            || serverRootedPath.Contains('\\')
            || serverRootedPath.Contains('?')
            || serverRootedPath.Contains('#')
            || serverRootedPath.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("A Registry route must be a non-empty server-rooted path.", nameof(serverRootedPath));
        }

        var relativePath = serverRootedPath[1..];
        if (Uri.TryCreate(relativePath, UriKind.Absolute, out _))
        {
            throw new ArgumentException("A Registry route cannot be an absolute URI.", nameof(serverRootedPath));
        }

        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 || !HasValidPercentEncoding(segment) || IsDotSegment(segment))
            {
                throw new ArgumentException("A Registry route cannot contain empty, dot, or invalid path segments.", nameof(serverRootedPath));
            }
        }

        return relativePath;
    }

    private static string Segment(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value is "." or "..")
        {
            throw new ArgumentException("A Registry route segment cannot be empty, '.' or '..'.", parameterName);
        }

        return Uri.EscapeDataString(value);
    }

    private static bool IsDotSegment(string segment)
    {
        var decoded = Uri.UnescapeDataString(segment);
        return decoded is "." or "..";
    }

    private static bool HasValidPercentEncoding(string segment)
    {
        for (var index = 0; index < segment.Length; index++)
        {
            if (segment[index] != '%') continue;
            if (index + 2 >= segment.Length
                || !Uri.IsHexDigit(segment[index + 1])
                || !Uri.IsHexDigit(segment[index + 2]))
            {
                return false;
            }
            index += 2;
        }

        return true;
    }
}
