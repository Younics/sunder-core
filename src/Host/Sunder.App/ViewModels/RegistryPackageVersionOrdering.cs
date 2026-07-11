using Sunder.Package.Format;

namespace Sunder.App.ViewModels;

internal static class RegistryPackageVersionOrdering
{
    public static SemanticVersion? TryParse(string value)
        => SemanticVersion.TryParse(value, out var version) ? version : null;

    public static IComparer<SemanticVersion?> Comparer { get; } = new NullableVersionComparer();

    private sealed class NullableVersionComparer : IComparer<SemanticVersion?>
    {
        public int Compare(SemanticVersion? x, SemanticVersion? y)
        {
            if (x is null && y is null)
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return x.Value.CompareTo(y.Value);
        }
    }
}
