using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Sunder.App.Services;

public sealed class StackArchivePicker(Window owner) : IStackArchivePicker
{
    private static readonly FilePickerFileType StackFileType = new("Sunder Stack")
    {
        Patterns = ["*.sunderstack"],
        MimeTypes = ["application/vnd.sunder.stack"],
    };

    public async Task<string?> PickStackPathAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel is null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Sunder Stack",
            AllowMultiple = false,
            FileTypeFilter = [StackFileType],
        });

        cancellationToken.ThrowIfCancellationRequested();
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickStackSavePathAsync(string suggestedFileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel is null)
        {
            return null;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Sunder Stack",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "sunderstack",
            FileTypeChoices = [StackFileType],
        });

        cancellationToken.ThrowIfCancellationRequested();
        return file?.TryGetLocalPath();
    }

}
