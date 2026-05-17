namespace Sunder.App.ViewModels;

internal static class RegistryPackageVersionOrdering
{
    public static Version? TryParse(string value)
        => Version.TryParse(value.Split('-', '+')[0], out var version) ? version : null;

    public static IComparer<Version?> Comparer { get; } = new NullableVersionComparer();

    private sealed class NullableVersionComparer : IComparer<Version?>
    {
        public int Compare(Version? x, Version? y)
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

            return x.CompareTo(y);
        }
    }
}
