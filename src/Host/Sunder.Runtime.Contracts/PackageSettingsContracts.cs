namespace Sunder.Runtime.Contracts;

public sealed record PackageSettingsValuesResponse(
    string PackageId,
    IReadOnlyDictionary<string, string?> StoredValues,
    IReadOnlyList<string> StoredSecretKeys)
{
    public IReadOnlyDictionary<string, string?> StoredValues { get; }
        = RuntimeContractCollections.Freeze(StoredValues);
    public IReadOnlyList<string> StoredSecretKeys { get; }
        = RuntimeContractCollections.Freeze(StoredSecretKeys);
}

public enum PackageSettingsUpdateMode
{
    Replace = 0,
    Patch = 1,
}

public sealed record UpdatePackageSettingsRequest(
    IReadOnlyDictionary<string, string?> Values,
    PackageSettingsUpdateMode Mode = PackageSettingsUpdateMode.Replace)
{
    public IReadOnlyDictionary<string, string?> Values { get; }
        = RuntimeContractCollections.Freeze(Values);
}

public sealed record PackageSettingValueResponse(
    bool IsStored,
    string? StoredValue,
    string? EffectiveValue);

public sealed record SetPackageSettingValueRequest(string Value);
