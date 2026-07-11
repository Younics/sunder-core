namespace Sunder.Package.Format;

public readonly record struct PackageId
{
    private readonly string? _value;

    private PackageId(string value) => _value = value;

    public static PackageId Parse(string value)
        => TryParse(value, out var packageId)
            ? packageId
            : throw new FormatException($"'{value}' is not a lowercase dot-separated ASCII package id.");

    public static bool TryParse(string? value, out PackageId packageId)
    {
        packageId = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var segments = value.Split('.');
        if (segments.Any(static segment => segment.Length == 0
                                           || segment.Any(static character => character is not (>= 'a' and <= 'z')
                                                                                           and not (>= '0' and <= '9'))))
        {
            return false;
        }

        packageId = new PackageId(value);
        return true;
    }

    public override string ToString() => _value ?? string.Empty;
}
