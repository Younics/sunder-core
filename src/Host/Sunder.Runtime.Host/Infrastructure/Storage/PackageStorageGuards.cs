using Sunder.Sdk.Storage;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal static class PackageStorageGuards
{
    internal static void Key(string? key, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException(
                $"Package storage keys must be portable ASCII tokens of at most {PackageStorageValidation.MaximumKeyLength} characters.",
                parameterName);
        }
    }

    internal static void KeyPrefix(string? prefix, string parameterName)
    {
        if (prefix is not null && prefix.Length != 0 && !PackageStorageValidation.IsValidKey(prefix))
        {
            throw new ArgumentException("Package storage key prefixes must be empty or valid portable ASCII key prefixes.", parameterName);
        }
    }

    internal static void Value(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException(
                $"Package storage values cannot exceed {PackageStorageValidation.MaximumValueUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }
    }

    internal static void Values(IReadOnlyDictionary<string, string>? values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        foreach (var pair in values)
        {
            Key(pair.Key, parameterName);
            Value(pair.Value, parameterName);
        }
    }

    internal static void RelativePath(string? relativePath, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(relativePath, parameterName);
        if (!PackageStorageValidation.IsValidRelativePath(relativePath))
        {
            throw new ArgumentException(
                "Package file paths must be relative, portable, non-empty, free of traversal, and within the declared length limits.",
                parameterName);
        }
    }

    internal static void FileLength(long byteCount, string parameterName)
    {
        if (!PackageStorageValidation.IsValidFileLength(byteCount))
        {
            throw new ArgumentException(
                $"Package files cannot exceed {PackageStorageValidation.MaximumFileBytes} bytes.",
                parameterName);
        }
    }
}
