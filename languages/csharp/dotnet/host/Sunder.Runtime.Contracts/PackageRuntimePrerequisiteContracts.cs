using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackageRuntimePrerequisiteExpectation(
    [property: JsonRequired] string SourceFingerprint,
    [property: JsonRequired] string Rid);

/// <summary>
/// Exactly one source: a Registry selection, source adoption, or local install/upgrade uploads.
/// Host inspects authoritative manifests; client-authored runtime requirements never authorize installation.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackageRuntimePrerequisiteRequest(
    RuntimeRegistryPackageBatchRequest? Registry = null,
    PackageStoreStageRequest? Local = null,
    string? ExpectedSourceFingerprint = null)
{
    private RuntimeRegistrySourceAdoptionRequest? _adoption;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeRegistrySourceAdoptionRequest? Adoption
    {
        get => _adoption;
        init => _adoption = value is null ? null : value with
        {
            DesiredTargets = value.DesiredTargets is null ? null : RuntimeContractCollections.FreezeBounded(value.DesiredTargets, RuntimeExecutionRuntimeManagementLimits.MaximumItems),
            CapabilityConsents = value.CapabilityConsents is null ? null : RuntimeContractCollections.FreezeBounded(value.CapabilityConsents, RuntimeExecutionRuntimeManagementLimits.MaximumItems),
        };
    }

    public RuntimeRegistryPackageBatchRequest? Registry { get; } = Snapshot(Registry);
    public PackageStoreStageRequest? Local { get; } = Local is null
        ? null
        : new(RuntimeContractCollections.FreezeBounded(Local.Mutations, RuntimeExecutionRuntimeManagementLimits.MaximumPackageSources))
        {
            RuntimePrerequisiteExpectation = Local.RuntimePrerequisiteExpectation,
        };

    private static RuntimeRegistryPackageBatchRequest? Snapshot(RuntimeRegistryPackageBatchRequest? request)
        => request is null ? null : new(
            request.RegistryOrigin,
            RuntimeContractCollections.FreezeBounded(request.Packages, RuntimeExecutionRuntimeManagementLimits.MaximumPackageSources)
                .Select(package => package with
                {
                    DesiredTargets = RuntimeContractCollections.FreezeBounded(package.DesiredTargets, RuntimeExecutionRuntimeManagementLimits.MaximumItems),
                }).ToArray(),
            request.IncludePrerelease,
            request.AllowDowngrade,
            request.Reinstall,
            RuntimeContractCollections.FreezeBounded(request.CapabilityConsents, RuntimeExecutionRuntimeManagementLimits.MaximumItems),
            request.ExpectedResolutionFingerprint)
        {
            RuntimePrerequisiteExpectation = request.RuntimePrerequisiteExpectation,
        };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackageRuntimePrerequisite(
    [property: JsonRequired] RuntimeExecutionRuntimePackageReference Package,
    [property: JsonRequired] string ManifestSha256,
    [property: JsonRequired] RuntimeExecutionRuntimeRequirement Requirement,
    [property: JsonRequired] bool Satisfied,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] RuntimeExecutionRuntimeIdentity? InstalledRuntime,
    [property: JsonRequired] RuntimeExecutionRuntimeInstallPlan? Installation);

/// <summary>
/// SourceFingerprint pins package selection/artifacts/manifests/targets, not mutable runtime availability.
/// Satisfied never grants package or capability consent; resume revalidates through the original install API.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackageRuntimePrerequisitePlan(
    [property: JsonRequired] bool Success,
    [property: JsonRequired] bool Satisfied,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string Message,
    [property: JsonRequired] string SourceFingerprint,
    [property: JsonRequired] string Rid,
    IReadOnlyList<RuntimePackageRuntimePrerequisite> Requirements,
    IReadOnlyList<string> Warnings)
{
    private IReadOnlyList<RuntimePackageRuntimePrerequisite> _requirements
        = RuntimeContractCollections.FreezeBounded(Requirements, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<string> _warnings
        = RuntimeContractCollections.FreezeBounded(Warnings, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    [JsonRequired]
    public IReadOnlyList<RuntimePackageRuntimePrerequisite> Requirements
    {
        get => _requirements;
        init => _requirements = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
    [JsonRequired]
    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        init => _warnings = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
}
