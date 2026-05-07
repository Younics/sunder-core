namespace Sunder.Sdk.Abstractions;

public interface IPackageStorageContext
{
    string DataRootPath { get; }

    string CacheRootPath { get; }

    string LogsRootPath { get; }

    IPackageFileStore Files { get; }

    IPackageKeyValueStore State { get; }
}
