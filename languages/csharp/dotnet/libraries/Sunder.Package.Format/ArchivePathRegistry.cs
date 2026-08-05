namespace Sunder.Package.Format;

internal sealed class ArchivePathRegistry
{
    private readonly Dictionary<string, string> _portablePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _archiveEntries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ArchiveRelativePath path, bool isDirectory)
    {
        var value = path.ToString();
        if (!_archiveEntries.Add(value))
        {
            throw new InvalidDataException($"Archive contains duplicate path '{value}'.");
        }

        var segments = value.Split('/');
        var current = string.Empty;
        for (var index = 0; index < segments.Length; index++)
        {
            current = index == 0 ? segments[index] : $"{current}/{segments[index]}";
            RegisterPortableSpelling(current);
            var isFinal = index == segments.Length - 1;
            if (!isFinal || isDirectory)
            {
                if (_files.Contains(current))
                {
                    throw new InvalidDataException($"Archive path '{value}' collides with file '{current}'.");
                }

                _directories.Add(current);
            }
        }

        if (!isDirectory)
        {
            if (_directories.Contains(value))
            {
                throw new InvalidDataException($"Archive file '{value}' collides with a directory.");
            }

            _files.Add(value);
        }
    }

    private void RegisterPortableSpelling(string value)
    {
        if (_portablePaths.TryGetValue(value, out var existing)
            && !string.Equals(existing, value, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive paths '{existing}' and '{value}' differ only by case.");
        }

        _portablePaths.TryAdd(value, value);
    }
}
