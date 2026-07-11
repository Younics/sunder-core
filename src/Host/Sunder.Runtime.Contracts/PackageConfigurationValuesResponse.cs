namespace Sunder.Runtime.Contracts;

public sealed record PackageConfigurationValuesResponse(
    string PackageId,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<string> StoredSecretKeys);
