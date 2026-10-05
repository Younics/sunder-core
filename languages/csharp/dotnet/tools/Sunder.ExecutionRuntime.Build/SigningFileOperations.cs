using System.Security.Cryptography;

namespace Sunder.ExecutionRuntime.Build;

// Only explicit input paths and invocation-owned temporaries are touched. No tree enumeration or orphan cleanup.
internal static class SigningFileOperations
{
    internal readonly record struct FileStamp(long Length, DateTime CreatedUtc, DateTime WrittenUtc);

    internal static string NormalizePath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsMacOS()) return fullPath;
        // Match the existing build-tool convention for OS-owned aliases, not arbitrary user links.
        foreach (var alias in new[] { "/var", "/tmp", "/etc" })
        {
            if ((fullPath == alias || fullPath.StartsWith(alias + '/', StringComparison.Ordinal))
                && new DirectoryInfo(alias).ResolveLinkTarget(returnFinalTarget: true)?.FullName == "/private" + alias)
            {
                return "/private" + fullPath;
            }
        }
        return fullPath;
    }

    internal static string EnsureDirectory(string path)
    {
        var fullPath = NormalizePath(path);
        var current = Path.GetPathRoot(fullPath)!;
        Check(current);
        foreach (var segment in fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Check(current);
        }
        return fullPath;

        static void Check(string directory)
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Signing directory '{directory}' must not be a symbolic link, reparse point, or non-directory.");
            }
        }
    }

    internal static void EnsureAbsent(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            if (new FileInfo(path).LinkTarget is null) return;
        }
        catch (DirectoryNotFoundException)
        {
            throw new IOException($"Signing output parent disappeared: '{path}'.");
        }
        throw new IOException($"Signing output already exists: '{path}'.");
    }

    internal static FileStamp ObserveFile(string path)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException($"Signing input '{path}' must be a non-link regular file.");
        }
        return new FileStamp(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
    }

    internal static async Task<byte[]> ReadStableAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken,
        Action<string>? afterRead = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = NormalizePath(path);
        var observed = ObserveFile(fullPath);
        if (observed.Length < 1 || observed.Length > maximumBytes)
        {
            throw new InvalidDataException($"Signing input must contain between 1 and {maximumBytes} bytes.");
        }
        byte[] bytes;
        await using (var stream = new FileStream(
                         fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length != observed.Length)
            {
                throw new InvalidDataException("Signing input changed size before being read.");
            }
            bytes = new byte[checked((int)observed.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
            {
                throw new InvalidDataException("Signing input changed while being read (unexpected trailing bytes).");
            }
        }
        afterRead?.Invoke(fullPath);
        // Reopen and hash the path as well as checking metadata: same-size, restored-mtime substitutions are not stable.
        var secondHash = await HashFileAsync(fullPath, observed.Length, cancellationToken);
        if (ObserveFile(fullPath) != observed
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), secondHash))
        {
            throw new InvalidDataException("Signing input changed while being read (metadata or SHA-256 mismatch).");
        }
        return bytes;
    }

    internal static async Task<byte[]> HashFileAsync(string path, long expectedLength, CancellationToken cancellationToken)
    {
        var observed = ObserveFile(path);
        if (observed.Length != expectedLength)
        {
            throw new InvalidDataException("Signing input changed size while being read.");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Cleanup also consumes this bounded hash synchronously through IDisposable.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long remaining = expectedLength;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Signing input changed while being read (unexpected EOF).");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0 || ObserveFile(path) != observed)
        {
            throw new InvalidDataException("Signing input changed while being hashed.");
        }
        return hash.GetHashAndReset();
    }

    internal static void TryDeleteOwnedFile(string path, FileStamp? owned, string expectedSha256)
    {
        if (owned is null) return;
        try
        {
            if (ObserveFile(path) != owned) return;
            // The expected digest comes from verified/approved bytes, never from this cleanup pathname.
            var hash = HashFileAsync(path, owned.Value.Length, CancellationToken.None).GetAwaiter().GetResult();
            if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expectedSha256))
                && ObserveFile(path) == owned)
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A missing, replaced, linked, or otherwise unsafe path is not ours to clean up.
        }
    }
}
