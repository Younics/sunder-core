namespace Sunder.Registry.Contracts;

public sealed record RegistryStackSummary(
    string StackId,
    string Name,
    string? Summary,
    int PackageCount,
    int FragmentCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RegistryStackStats? Stats = null,
    RegistryUserAttribution? Creator = null);

public sealed record RegistryStackSearchResult(
    IReadOnlyList<RegistryStackSummary> Items,
    int TotalCount,
    int Skip,
    int Take);

public sealed record RegistryStackDetails(
    string StackId,
    string Name,
    string? Summary,
    IReadOnlyList<RegistryStackPackageRequirement> Packages,
    IReadOnlyList<RegistryStackFragmentSummary> Fragments,
    IReadOnlyList<RegistryStackRequiredInput> RequiredInputs,
    RegistryStackArtifact Artifact,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    RegistryStackStats? Stats = null,
    RegistryStackProfile? Profile = null,
    RegistryUserAttribution? Creator = null,
    IReadOnlyList<RegistryUserAttribution>? Maintainers = null);

public sealed record RegistryStackStats(
    long TotalDownloads,
    long Stars,
    bool IsStarred,
    long WeeklyDownloads = 0,
    IReadOnlyList<RegistryStackDownloadPoint>? DailyDownloads = null,
    long TotalViews = 0);

public sealed record RegistryStackViewResponse(long Views);

/// <summary>Download count for a single calendar day, used to plot the Stack downloads chart.</summary>
public sealed record RegistryStackDownloadPoint(
    DateOnly Date,
    long Count);

public sealed record RegistryStackPackageRequirement(
    string PackageId,
    string InstallTag,
    string? CreatedWithVersion,
    string? MinimumVersion,
    bool Required,
    string? Name = null,
    string? IconUrl = null);

public sealed record RegistryStackFragmentSummary(
    string FragmentId,
    string OwnerPackageId,
    string ContributorId,
    string SchemaId,
    int SchemaVersion,
    string DisplayName,
    string? Description,
    bool DefaultSelected,
    string? SourceItemId = null,
    string? Kind = null,
    IReadOnlyList<RegistryStackFragmentDetail>? DisplayDetails = null);

public sealed record RegistryStackFragmentDetail(
    string Label,
    string Value,
    string? Behavior);

public sealed record RegistryStackRequiredInput(
    string InputId,
    string OwnerPackageId,
    string ContributorId,
    string Label,
    string? Description,
    bool Required);

public sealed record RegistryStackArtifact(
    string Sha256,
    long? Size,
    string DownloadUrl);

public sealed record RegistryStackProfile(
    string StackId,
    string? ShortDescription,
    string? ReadmeMarkdown,
    string? WebsiteUrl,
    string? SourceUrl,
    string? IssueTrackerUrl,
    string? License,
    IReadOnlyList<string> Tags,
    IReadOnlyList<RegistryStackMedia> Media,
    DateTimeOffset? UpdatedAtUtc);

public sealed record RegistryStackMedia(
    Guid MediaId,
    string FileName,
    string ContentType,
    long Size,
    string? AltText,
    int SortOrder,
    string Url);

public sealed record RegistryUpdateStackProfileRequest(
    string? ShortDescription,
    string? ReadmeMarkdown,
    string? WebsiteUrl,
    string? SourceUrl,
    string? IssueTrackerUrl,
    string? License,
    IReadOnlyList<string>? Tags);

public sealed record RegistryStackProfileOperationResponse(
    bool Success,
    string? Message,
    RegistryStackProfile? Profile,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
}
