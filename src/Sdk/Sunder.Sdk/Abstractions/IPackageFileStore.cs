using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
public interface IPackageFileStore
{
    string RootPath { get; }

    string GetPath(string relativePath);
}
