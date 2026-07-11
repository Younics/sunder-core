namespace Sunder.Runtime.Contracts;

public sealed record UpdatePackageConfigurationValuesRequest(IReadOnlyDictionary<string, string?> Values);
