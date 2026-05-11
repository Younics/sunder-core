using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ConfigurationValuesV1)]
public interface IPackageConfiguration
{
    string? GetValue(string key);
}
