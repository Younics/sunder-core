using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeCurrentAuthorityException(string code)
    : RuntimeException(code, "Current runtime recovery required", StatusCodes.Status409Conflict,
        "The original runtime binding changed or is no longer locally verifiable; preview the operation again.");

internal static class ExecutionRuntimeManagementSupport
{
    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static bool IsHash(string? value) => OfficialRuntimeSourcePolicy.IsDigest(value, 64);
    internal static string Rid => PackageTargetSelection.GetCurrentRuntimeIdentifier();

    internal static void ValidateRequirement(RuntimeExecutionRuntimeRequirement? requirement)
    {
        if (requirement is null) throw new RuntimeValidationException("A supported runtime family/ABI and bounded stable major range are required.");
        OfficialRuntimeSourcePolicy.ValidateRequirement(requirement);
    }

    internal static void ValidateIdentity(RuntimeExecutionRuntimeIdentity? identity)
    {
        if (identity is null || identity.Rid != Rid)
            throw new RuntimeValidationException("Invalid exact runtime identity for this Host.");

        OfficialRuntimeSourcePolicy.ValidateIdentity(identity);
    }

    internal static void ValidateArtifact(RuntimeExecutionRuntimeVendorArtifact? artifact)
    {
        if (artifact is null) throw new RuntimeValidationException("Runtime artifact evidence is required.");
        ValidateIdentity(artifact.Identity);
        OfficialRuntimeSourcePolicy.ValidateArtifact(artifact);
    }

    internal static void ValidateResolution(RuntimeExecutionRuntimeResolution value)
    {
        ValidateRequirement(value.Requirement);
        ValidateArtifact(value.Artifact);
        if (!IsHash(value.ResolutionFingerprint)
            || value.ExpiresAtUtc == default
            || value.Artifact.Identity.RuntimeId != value.Requirement.RuntimeId
            || value.Artifact.Identity.ModuleAbi != value.Requirement.ModuleAbi
            || !PackageVersionRange.IsSatisfiedBy(value.Artifact.Identity.VendorVersion, value.Requirement.VersionRange))
        {
            throw new InvalidDataException("Runtime resolution evidence is invalid.");
        }
    }

    internal static ExecutionRuntimeIdentity Identity(RuntimeExecutionRuntimeIdentity value)
        => ExecutionRuntimeIdentity.FromContract(value);

    internal static RuntimeExecutionRuntimeIdentity Identity(ExecutionRuntimeIdentity value)
        => value.ToContract();

    internal static RuntimeExecutionRuntimeIdentity Identity(ExecutionRuntimeReceiptV1 value)
        => value.Identity.ToContract();

    internal static RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact(ExecutionRuntimeReceiptV1 receipt)
        => receipt.InstalledArtifact;

    internal static RuntimeExecutionRuntimeException Stale() => new(ExecutionRuntimeErrorCodes.PlanStale);

    internal static RuntimeExecutionRuntimeRequirement UpdateLine(RuntimeExecutionRuntimeIdentity identity)
    {
        var major = System.Numerics.BigInteger.Parse(SemanticVersion.Parse(identity.VendorVersion).Major, System.Globalization.CultureInfo.InvariantCulture);
        return new(identity.RuntimeId, $">={major}.0.0 <{major + 1}.0.0", identity.ModuleAbi);
    }

    internal static string DisplayName(RuntimeExecutionRuntimeVendorArtifact artifact)
        => artifact.Identity.SourceId == ConfiguredOfficialSourceId.DotNet
            ? $".NET Runtime {artifact.Identity.VendorVersion}"
            : $"Node.js {artifact.Identity.VendorVersion}";
}
