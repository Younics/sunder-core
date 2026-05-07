namespace Sunder.Sdk.Abstractions;

public interface IPackageFileStore
{
    string RootPath { get; }

    string GetPath(string relativePath);
}
