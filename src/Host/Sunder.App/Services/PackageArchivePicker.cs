using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Sunder.App.Services;

public sealed class PackageArchivePicker(Window owner) : IPackageArchivePicker
{
    public async Task<string?> PickPackagePathAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel is null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install Sunder Package",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Sunder package")
                {
                    Patterns = ["*.sunderpkg"],
                },
            ],
        });

        cancellationToken.ThrowIfCancellationRequested();
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
