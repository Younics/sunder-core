using System.Security.Cryptography;

namespace Sunder.Package.Format;

internal static class PackageContentSignatureValidator
{
    public static bool IsLowercaseSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static async Task ValidateAsync(
        SunderPackageContentIndexEntry entry,
        string? filePath,
        string normalizedPath,
        ICollection<string> errors,
        bool validateHash,
        CancellationToken cancellationToken)
    {
        if (filePath is null)
        {
            errors.Add($"Package content index references missing file '{normalizedPath}'.");
            return;
        }

        var info = new FileInfo(filePath);
        if (info.Length != entry.Size)
        {
            errors.Add($"Package file '{normalizedPath}' size mismatch.");
        }
        if (!validateHash)
        {
            return;
        }
        await using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
        {
            errors.Add($"Package file '{normalizedPath}' SHA-256 mismatch.");
        }
    }
}
