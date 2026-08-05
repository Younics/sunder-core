using System.Security.Cryptography;

namespace Sunder.Runtime.Client;

public static class VerifiedFileTransfer
{
    private const int BufferSize = 128 * 1024;

    public static async Task PublishAsync(
        Stream source,
        string destinationPath,
        long maxBytes,
        long? expectedLength,
        string expectedSha256,
        string description,
        CancellationToken cancellationToken = default,
        bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        if (expectedLength is < 0 || expectedLength > maxBytes)
        {
            throw new InvalidDataException($"{description} has an invalid declared length.");
        }

        var expectedHash = ParseSha256(expectedSha256, description);
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new ArgumentException("Destination path must have a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.download-{Guid.NewGuid():N}.tmp");

        try
        {
            long length = 0;
            byte[] actualHash;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var destination = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 BufferSize,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[BufferSize];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        length += read;
                        if (length > maxBytes || expectedLength is { } declaredLength && length > declaredLength)
                        {
                            throw new InvalidDataException($"{description} exceeds its allowed length.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                actualHash = hash.GetHashAndReset();
            }

            if (expectedLength is { } lengthLimit && length != lengthLimit)
            {
                throw new InvalidDataException($"{description} length verification failed.");
            }
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException($"{description} SHA-256 verification failed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static byte[] ParseSha256(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 64)
        {
            throw new InvalidDataException($"{description} does not declare a valid SHA-256 hash.");
        }

        try
        {
            return Convert.FromHexString(value.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{description} does not declare a valid SHA-256 hash.", exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup must not replace the transfer or verification failure.
        }
    }
}
