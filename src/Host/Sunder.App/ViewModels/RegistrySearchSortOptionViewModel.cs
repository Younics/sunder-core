using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

public sealed class RegistrySearchSortOptionViewModel(string label, RegistrySearchSort sort)
{
    public string Label { get; } = label;

    public RegistrySearchSort Sort { get; } = sort;

    public static IReadOnlyList<RegistrySearchSortOptionViewModel> Defaults { get; } =
    [
        new("Most downloaded", RegistrySearchSort.Downloads),
        new("Recently updated", RegistrySearchSort.Updated),
        new("Most starred", RegistrySearchSort.Stars),
    ];
}
