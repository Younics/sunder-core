using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public sealed partial class LocalStacksViewModel : ViewModelBase
{
    private readonly StackLibraryCoordinator _library;
    private readonly Action<LocalStackLibraryItemViewModel?> _selectionChanged;

    internal LocalStacksViewModel(
        LocalStackLibraryService library,
        Action<LocalStackLibraryItemViewModel?> selectionChanged)
    {
        _library = new StackLibraryCoordinator(library);
        _selectionChanged = selectionChanged;
    }

    public ObservableCollection<LocalStackLibraryItemViewModel> Stacks { get; } = [];

    public bool HasStacks => Stacks.Count > 0;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    internal int Count => _library.Count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LocalStackLibraryItemViewModel? _selectedStack;

    partial void OnSearchTextChanged(string value) => Rebuild(SelectedStack?.StackId);

    partial void OnSelectedStackChanged(LocalStackLibraryItemViewModel? value)
    {
        foreach (var stack in Stacks)
        {
            stack.IsSelected = ReferenceEquals(stack, value);
        }

        _selectionChanged(value);
    }

    internal async Task RefreshAsync(string? preferredStackId, CancellationToken cancellationToken)
    {
        await _library.RefreshAsync(cancellationToken);
        Rebuild(preferredStackId);
    }

    internal async Task<LocalStackLibraryItem> ImportAsync(string path, CancellationToken cancellationToken)
    {
        var item = await _library.ImportAsync(path, cancellationToken);
        Rebuild(item.StackId);
        return item;
    }

    internal async Task DeleteAsync(LocalStackLibraryItemViewModel stack, CancellationToken cancellationToken)
    {
        await _library.DeleteAsync(stack.Item, cancellationToken);
        Rebuild();
    }

    internal Task ExportAsync(LocalStackLibraryItemViewModel stack, string path, CancellationToken cancellationToken)
        => _library.ExportAsync(stack.Item, path, cancellationToken);

    internal void Rebuild(string? preferredStackId = null)
    {
        Stacks.ReplaceWith(_library.Project(SearchText));
        OnPropertyChanged(nameof(HasStacks));
        SelectedStack = !string.IsNullOrWhiteSpace(preferredStackId)
            ? Stacks.FirstOrDefault(item => string.Equals(item.StackId, preferredStackId, StringComparison.OrdinalIgnoreCase))
            : Stacks.FirstOrDefault();
        if (SelectedStack is null)
        {
            _selectionChanged(null);
        }
    }
}
