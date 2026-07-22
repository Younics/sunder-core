using System.Text;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Storage;

/// <summary>Defines and validates the portable V1 package storage and settings bounds.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
public static class PackageStorageValidation
{
    /// <summary>Maximum number of ASCII characters in a package storage or settings key.</summary>
    public const int MaximumKeyLength = 256;

    /// <summary>Maximum number of UTF-8 bytes in a package storage or settings string value.</summary>
    public const int MaximumValueUtf8Bytes = 1024 * 1024;

    /// <summary>Maximum number of characters in a portable package-relative path.</summary>
    public const int MaximumRelativePathLength = 1024;

    /// <summary>Maximum number of characters in one portable package-relative path segment.</summary>
    public const int MaximumPathSegmentLength = 255;

    /// <summary>Maximum number of bytes in one package file.</summary>
    public const int MaximumFileBytes = 16 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Returns whether <paramref name="key"/> contains 1 to <see cref="MaximumKeyLength"/> ASCII letters, digits,
    /// periods, hyphens, or underscores.
    /// </summary>
    public static bool IsValidKey(string? key)
        => !string.IsNullOrEmpty(key)
           && key.Length <= MaximumKeyLength
           && key.All(static character => char.IsAsciiLetterOrDigit(character)
                                          || character is '.' or '-' or '_');

    /// <summary>
    /// Returns whether <paramref name="value"/> is valid Unicode whose UTF-8 representation does not exceed
    /// <see cref="MaximumValueUtf8Bytes"/>.
    /// </summary>
    public static bool IsValidValue(string? value)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(value) <= MaximumValueUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="relativePath"/> is a portable visible-ASCII path using forward-slash
    /// separators, safe platform-independent segments, and the declared path bounds.
    /// </summary>
    public static bool IsValidRelativePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)
            || relativePath.Length > MaximumRelativePathLength
            || relativePath.Contains('\\')
            || relativePath.Any(static character => character < ' ' || character > '~'))
        {
            return false;
        }

        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length is 0 or > MaximumPathSegmentLength
                || segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.Any(static character => character is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                || WindowsReservedNames.Contains(segment.Split('.')[0]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns whether <paramref name="byteCount"/> is a supported package file length.</summary>
    public static bool IsValidFileLength(long byteCount)
        => byteCount is >= 0 and <= MaximumFileBytes;
}
