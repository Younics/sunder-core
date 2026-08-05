using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class RegistryStacksViewModel : ViewModelBase, IDisposable
{
    private readonly Func<Uri, IRegistryApiClient> _clientFactory;
    private readonly Action<RegistryStackSearchItemViewModel?> _selectionChanged;
    private readonly LatestAsyncRequest _searchRequest = new();

    internal RegistryStacksViewModel(
        Func<Uri, IRegistryApiClient> clientFactory,
        Action<RegistryStackSearchItemViewModel?> selectionChanged)
    {
        _clientFactory = clientFactory;
        _selectionChanged = selectionChanged;
        RegistryUrlText = RegistryUrlHelper.DefaultRegistryUrl.ToString();
    }

    public ObservableCollection<RegistryStackSearchItemViewModel> Stacks { get; } = [];

    public ObservableCollection<RegistrySearchSortOptionViewModel> SortOptions { get; } = new(RegistrySearchSortOptionViewModel.Defaults);

    public bool HasStacks => Stacks.Count > 0;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasRegistryUrlText => !string.IsNullOrWhiteSpace(RegistryUrlText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private RegistrySearchSortOptionViewModel? _selectedSortOption = RegistrySearchSortOptionViewModel.Defaults[0];

    [ObservableProperty]
    private RegistryStackSearchItemViewModel? _selectedStack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRegistryUrlText))]
    private string _registryUrlText;

    partial void OnSelectedStackChanged(RegistryStackSearchItemViewModel? value)
    {
        foreach (var stack in Stacks)
        {
            stack.IsSelected = ReferenceEquals(stack, value);
        }

        _selectionChanged(value);
    }

    internal async Task<RegistryStackSearchResult> SearchAsync(CancellationToken cancellationToken)
    {
        if (!TryResolveRegistryUrl(out var registryUrl))
        {
            return RegistryStackSearchResult.Failed("Enter a valid HTTP Registry URL before using Registry Stacks.");
        }

        using var request = _searchRequest.Start(cancellationToken);
        SelectedStack = null;
        Stacks.Clear();
        OnPropertyChanged(nameof(HasStacks));
        try
        {
            using var registryClient = _clientFactory(registryUrl);
            var query = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
            var results = await registryClient.SearchStacksAsync(
                query,
                0,
                50,
                SelectedSortOption?.Sort ?? RegistrySearchSort.Downloads,
                request.Token);
            if (!request.IsCurrent)
            {
                return RegistryStackSearchResult.Superseded;
            }

            Stacks.ReplaceWith(results.Select(stack => new RegistryStackSearchItemViewModel(stack)));
            OnPropertyChanged(nameof(HasStacks));
            SelectedStack = Stacks.FirstOrDefault();
            return RegistryStackSearchResult.Succeeded(Stacks.Count);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            return RegistryStackSearchResult.Superseded;
        }
        catch (Exception ex)
        {
            return request.IsCurrent
                ? RegistryStackSearchResult.Failed(ex.Message)
                : RegistryStackSearchResult.Superseded;
        }
    }

    internal IRegistryApiClient CreateClient(Uri registryUrl) => _clientFactory(registryUrl);

    internal bool TryResolveRegistryUrl(out Uri registryUrl)
        => RegistryUrlHelper.TryParse(RegistryUrlText, out registryUrl!) && registryUrl is not null;

    internal void KeepOnly(RegistryStackSearchItemViewModel stack)
    {
        Stacks.ReplaceWith([stack]);
        OnPropertyChanged(nameof(HasStacks));
    }

    public void Dispose() => _searchRequest.Dispose();
}

internal sealed record RegistryStackSearchResult(bool Applied, int Count, string? ErrorMessage)
{
    public static RegistryStackSearchResult Superseded { get; } = new(false, 0, null);

    public static RegistryStackSearchResult Succeeded(int count) => new(true, count, null);

    public static RegistryStackSearchResult Failed(string errorMessage) => new(true, 0, errorMessage);
}
