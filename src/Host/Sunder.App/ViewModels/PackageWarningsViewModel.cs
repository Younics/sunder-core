using System.Collections.ObjectModel;

namespace Sunder.App.ViewModels;

internal sealed class PackageWarningsViewModel
{
    public ObservableCollection<string> Lines { get; } = [];

    public bool HasWarnings => Lines.Count > 0;

    public int Count => Lines.Count;

    public void Clear()
    {
        Lines.Clear();
    }

    public void Add(string warning)
    {
        Lines.Add(warning);
    }

    public void ReplaceWith(IReadOnlyList<string> warnings)
    {
        Lines.ReplaceWith(warnings);
    }
}
