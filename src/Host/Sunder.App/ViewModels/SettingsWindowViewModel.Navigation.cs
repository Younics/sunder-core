namespace Sunder.App.ViewModels;

public sealed partial class SettingsWindowViewModel
{
    public async Task SelectSectionAsync(SettingsSectionItemViewModel item)
        => await SelectSectionCoreAsync(
            item,
            new Dictionary<string, string?>(),
            _disposeCts.Token);

    private async Task<bool> SelectSectionCoreAsync(
        SettingsSectionItemViewModel item,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var previousSelection = _presentedSection;
        var selectionVersion = _selection.Select(item);
        if (item.IsPackage)
        {
            return await ApplyPackageSelectionAsync(
                item,
                selectionVersion,
                previousSelection,
                parameters,
                cancellationToken);
        }

        _selectionLoadRequest.Invalidate();
        ApplyCoreSelection(item);
        if (IsCliSelection)
        {
            await RefreshCliStatusAsync(showSuccessStatus: false);
        }
        return true;
    }

    private static IReadOnlyDictionary<string, string?> SnapshotNavigationParameters(
        IReadOnlyDictionary<string, string?>? parameters)
        => parameters is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(parameters, StringComparer.Ordinal);
}
