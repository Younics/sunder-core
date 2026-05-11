using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
public interface IPackageStorageContext
{
    string DataRootPath { get; }

    string CacheRootPath { get; }

    string LogsRootPath { get; }

    IPackageFileStore Files { get; }

    IPackageKeyValueStore State { get; }
}
