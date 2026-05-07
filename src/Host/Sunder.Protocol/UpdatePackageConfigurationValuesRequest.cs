namespace Sunder.Protocol;

public sealed record UpdatePackageConfigurationValuesRequest(IReadOnlyDictionary<string, string?> Values);
