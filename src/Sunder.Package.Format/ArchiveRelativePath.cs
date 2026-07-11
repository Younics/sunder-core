namespace Sunder.Package.Format;

public readonly record struct ArchiveRelativePath
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string? _value;

    private ArchiveRelativePath(string value) => _value = value;

    public int Depth => _value?.Count(static character => character == '/') + 1 ?? 0;

    public static ArchiveRelativePath Parse(string value, int maxLength = int.MaxValue, int maxDepth = int.MaxValue)
        => TryParse(value, maxLength, maxDepth, out var path, out var error)
            ? path
            : throw new InvalidDataException($"Archive path '{value}' is unsafe: {error}");

    public static bool TryParse(
        string? value,
        int maxLength,
        int maxDepth,
        out ArchiveRelativePath path,
        out string? error)
    {
        path = default;
        error = null;
        if (string.IsNullOrEmpty(value))
        {
            error = "paths cannot be empty";
            return false;
        }

        if (value.Length > maxLength)
        {
            error = $"paths cannot exceed {maxLength} characters";
            return false;
        }

        if (value[0] == '/' || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\'))
        {
            error = "rooted, UNC, and backslash paths are not allowed";
            return false;
        }

        if (value.Any(static character => character < ' ' || character > '~'))
        {
            error = "only portable visible ASCII characters are allowed";
            return false;
        }

        var segments = value.Split('/');
        if (segments.Length > maxDepth)
        {
            error = $"paths cannot exceed {maxDepth} segments";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                error = "empty, dot, and parent segments are not allowed";
                return false;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                error = "segments cannot end in a space or period";
                return false;
            }

            if (segment.Any(static character => character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
            {
                error = "segments contain characters with platform-dependent meaning";
                return false;
            }

            var deviceName = segment.Split('.')[0];
            if (WindowsReservedNames.Contains(deviceName))
            {
                error = $"'{segment}' is a reserved platform name";
                return false;
            }
        }

        path = new ArchiveRelativePath(value);
        return true;
    }

    public string ToPlatformPath(string rootPath)
        => Path.Combine([rootPath, .. ToString().Split('/')]);

    public override string ToString() => _value ?? string.Empty;
}
