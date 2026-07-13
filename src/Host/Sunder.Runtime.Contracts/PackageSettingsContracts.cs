namespace Sunder.Runtime.Contracts;

public sealed record PackageSettingsValuesResponse(
    string PackageId,
    IReadOnlyDictionary<string, string?> StoredValues,
    IReadOnlyList<string> StoredSecretKeys);

public sealed record UpdatePackageSettingsRequest(IReadOnlyDictionary<string, string?> Values);

public sealed record PackageSettingValueResponse(
    bool IsStored,
    string? StoredValue,
    string? EffectiveValue);

public sealed record SetPackageSettingValueRequest(string Value);
