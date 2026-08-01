using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed record DevProcessLaunchMetadata(
    int SchemaVersion,
    string Kind,
    string PackageId,
    string PackageVersion,
    string Rid,
    string EntryPoint,
    string NodePath,
    string NodeVersion,
    string ContentIdentity)
{
    public const string MetadataSuffix = ".sunder-node-dev.json";
    public const string NodeKind = "node";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string GetPath(string canonicalPackageRoot)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalPackageRoot)) + MetadataSuffix;

    public static bool RejectForInstalledSource(
        string canonicalPackageRoot,
        ICollection<string> errors)
    {
        var path = GetPath(canonicalPackageRoot);
        if (!File.Exists(path)) return false;
        errors.Add(
            $"Installed package source '{canonicalPackageRoot}' has forbidden external Node dev metadata '{path}'. External Node launch metadata is allowed only for local path-based dev packages.");
        return true;
    }

    public static DevProcessLaunchMetadata? LoadForDevSource(
        string canonicalPackageRoot,
        SunderPackageManifest manifest,
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target,
        string contentIdentity,
        ICollection<string> errors)
    {
        var path = GetPath(canonicalPackageRoot);
        if (!File.Exists(path)) return null;

        DevProcessLaunchMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<DevProcessLaunchMetadata>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"Node dev launch metadata '{path}' is invalid: {exception.Message}");
            return null;
        }

        if (metadata is null
            || metadata.SchemaVersion != 1
            || !string.Equals(metadata.Kind, NodeKind, StringComparison.Ordinal)
            || !string.Equals(metadata.PackageId, manifest.Id, StringComparison.Ordinal)
            || !string.Equals(metadata.PackageVersion, manifest.Version, StringComparison.Ordinal)
            || !string.Equals(metadata.Rid, targetKey.Rid, StringComparison.Ordinal)
            || !string.Equals(metadata.EntryPoint, target.EntryPoint, StringComparison.Ordinal)
            || !string.Equals(metadata.ContentIdentity, contentIdentity, StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(metadata.NodePath)
            || !File.Exists(metadata.NodePath)
            || !SemanticVersion.TryParse(metadata.NodeVersion.TrimStart('v'), out _))
        {
            errors.Add(
                $"Node dev launch metadata '{path}' does not exactly bind the validated package identity, content, RID, entry point, and explicit Node executable.");
            return null;
        }

        return metadata with { NodePath = Path.GetFullPath(metadata.NodePath) };
    }
}
