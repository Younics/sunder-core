namespace Sunder.App.Services;

public interface IPackageArchivePicker
{
    Task<PackageArchiveSelection?> PickPackageAsync(CancellationToken cancellationToken = default);
}

public sealed record PackageArchiveSelection(
    string PackagePath,
    string Sha256,
    bool DeleteAfterUse);
