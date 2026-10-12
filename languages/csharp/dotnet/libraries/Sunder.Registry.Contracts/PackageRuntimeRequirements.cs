using System.Text.Json.Serialization;

namespace Sunder.Registry.Contracts;

public static class RegistryPackageActionKinds
{
    public const string PackageInstall = "package-install";
    public const string PackageUpdate = "package-update";
    public const string PackageRemove = "package-remove";
    public const string Keep = "keep";
}

/// <summary>Immutable package target requirement. Registry never selects or authorizes runtime distribution files.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryExecutionRuntimeRequirement(
    [property: JsonRequired] string RuntimeId,
    [property: JsonRequired] string VersionRange,
    [property: JsonRequired] string ModuleAbi);

public static class RegistryPackagePlanLimits
{
    public const int MaximumItems = 1024;
}
