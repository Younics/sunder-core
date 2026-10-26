using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed class SettingsPackageNavigationGroup : ViewModelBase
{
    private bool _isExpanded;

    internal SettingsPackageNavigationGroup(SettingsSectionItemViewModel root,
        IReadOnlyList<SettingsSectionItemViewModel> children)
    {
        Root = root;
        Children = children;
        foreach (var child in children)
        {
            child.NavigationTitle = ChildTitle(root.Title, child.Title);
        }
    }

    public SettingsSectionItemViewModel Root { get; }
    public IReadOnlyList<SettingsSectionItemViewModel> Children { get; }
    public bool HasChildren => Children.Count > 0;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ChevronAngle));
                OnPropertyChanged(nameof(ToggleDescription));
            }
        }
    }
    public double ChevronAngle => IsExpanded ? 90 : 0;
    public string ToggleDescription => $"{(IsExpanded ? "Collapse" : "Expand")} {Root.Title} dependent settings";

    private static string ChildTitle(string parent, string title)
    {
        var prefix = parent.Trim();
        if (prefix.Length == 0 || !title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return title;
        var suffix = title[prefix.Length..].TrimStart();
        if (suffix.Length == 0 || suffix[0] is not ('-' or '–' or '—' or ':')) return title;
        var shortened = suffix[1..].Trim();
        return shortened.Length > 0 ? shortened : title;
    }
}

internal static class SettingsPackageNavigationProjector
{
    public static IReadOnlyList<SettingsPackageNavigationGroup> Project(
        IReadOnlyList<SettingsSectionItemViewModel> sections, IReadOnlyList<SessionPackageDescriptor> packages)
    {
        // Context is presentation-only. A package that becomes independent on a
        // subsequent projection must regain its full name without changing identity.
        foreach (var section in sections) section.NavigationTitle = section.Title;
        var graph = packages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var byId = sections.ToDictionary(section => section.PackageId!, StringComparer.OrdinalIgnoreCase);
        var memo = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            var id = section.PackageId!;
            var roots = Roots(id, new(StringComparer.OrdinalIgnoreCase));
            var settingsRoots = roots?.Where(byId.ContainsKey).ToArray();
            owners[id] = settingsRoots is { Length: 1 } ? settingsRoots[0] : id;
        }
        // An unresolved/ambiguous package remains top-level, never an arbitrary
        // child. Likewise do not bury a package below an already nested owner.
        return sections.Where(section => Same(owners[section.PackageId!], section.PackageId!))
            .OrderBy(section => section.Title, StringComparer.OrdinalIgnoreCase).ThenBy(section => section.PackageId, StringComparer.Ordinal)
            .Select(root => new SettingsPackageNavigationGroup(root, sections
                .Where(section => !Same(section.PackageId!, root.PackageId!) && Same(owners[section.PackageId!], root.PackageId!))
                .OrderBy(section => section.Title, StringComparer.OrdinalIgnoreCase).ThenBy(section => section.PackageId, StringComparer.Ordinal).ToArray()))
            .ToArray();

        HashSet<string>? Roots(string id, HashSet<string> visiting)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            if (visiting.Count >= 256 || !visiting.Add(id)) return null;
            HashSet<string>? result = null;
            if (graph.TryGetValue(id, out var package) && package.RequiredPackageIds is { } dependencies)
            {
                result = new(StringComparer.OrdinalIgnoreCase);
                foreach (var dependency in dependencies)
                {
                    var roots = Roots(dependency, visiting);
                    if (roots is null) { result = null; break; }
                    result.UnionWith(roots);
                }
                if (result is { Count: 0 } && byId.ContainsKey(id)) result.Add(id);
            }
            visiting.Remove(id);
            memo[id] = result;
            return result;
        }
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
