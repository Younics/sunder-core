namespace Sunder.App.Services;

public interface IPackageArchivePicker
{
    Task<string?> PickPackagePathAsync(CancellationToken cancellationToken = default);
}
