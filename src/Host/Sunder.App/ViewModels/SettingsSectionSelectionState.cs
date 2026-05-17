namespace Sunder.App.ViewModels;

internal sealed class SettingsSectionSelectionState
{
    private SettingsSectionItemViewModel? _selectedSection;
    private int _version;
    private bool _disposed;

    public SettingsSectionItemViewModel? SelectedSection => _selectedSection;

    public int Version => _version;

    public int Select(SettingsSectionItemViewModel item)
    {
        PreserveSelection(item);
        _version++;
        return _version;
    }

    public void PreserveSelection(SettingsSectionItemViewModel item)
    {
        if (_selectedSection is not null)
        {
            _selectedSection.IsSelected = false;
        }

        _selectedSection = item;
        _selectedSection.IsSelected = true;
    }

    public bool IsCurrent(SettingsSectionItemViewModel item, int? version = null)
        => !_disposed
           && ReferenceEquals(_selectedSection, item)
           && (!version.HasValue || version.Value == _version);

    public void Dispose()
    {
        _disposed = true;
        _version++;
    }
}
