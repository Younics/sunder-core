namespace Sunder.Sdk.Abstractions;

public interface IPackageConfiguration
{
    string? GetValue(string key);
}
