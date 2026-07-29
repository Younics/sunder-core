using System.Text.Json;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed record PackageValuesDocument(Dictionary<string, string> Values, long Revision);

internal sealed class PackageStateSerializer
{
    private const string Format = "sunder.package-state";
    private const int Version = 1;

    internal PackageValuesDocument Deserialize(
        byte[] contents,
        string canonicalPath,
        bool enforcePackageKeyValidation = true)
    {
        using var json = JsonDocument.Parse(contents);
        var root = PackageStorageJson.RequireObject(json, "state document");
        if (PackageStorageJson.TryReadStringDictionary(root, out _))
        {
            throw new PackageStorageNotSupportedException(
                "Legacy package state is not supported in the Runtime V1 state root.");
        }

        if (StorageFailureMarker.IsMarker(root))
        {
            throw StorageFailureMarker.CreateException(root, canonicalPath);
        }

        var format = PackageStorageJson.ReadString(root, "format", "state document");
        if (!string.Equals(format, Format, StringComparison.Ordinal))
        {
            throw new PackageStorageNotSupportedException($"Unsupported package state format '{format}'.");
        }

        var version = PackageStorageJson.ReadInt32(root, "version", "state document");
        PackageStorageJson.RequireVersion(version, Version, "Package state version");
        var revision = PackageStorageJson.ReadInt64(root, "revision", "state document");
        if (revision < 0)
        {
            throw new InvalidDataException("The state revision is invalid.");
        }

        if (!root.TryGetProperty("values", out var valuesElement))
        {
            throw new InvalidDataException("The state document does not contain values.");
        }

        return new PackageValuesDocument(
            PackageStorageJson.ReadStringDictionary(
                valuesElement,
                "Stored values must be a JSON object.",
                "Every stored state value must be a string.",
                enforcePackageKeyValidation),
            revision);
    }

    internal byte[] Serialize(Dictionary<string, string> values, long revision) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new StateEnvelope(Format, Version, revision, values),
            PackageStorageJson.Options);

    private sealed record StateEnvelope(
        string Format,
        int Version,
        long Revision,
        Dictionary<string, string> Values);
}
