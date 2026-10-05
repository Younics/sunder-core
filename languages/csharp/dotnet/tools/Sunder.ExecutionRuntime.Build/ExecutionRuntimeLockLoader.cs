using System.Security.Cryptography;

namespace Sunder.ExecutionRuntime.Build;

internal static class ExecutionRuntimeLockLoader
{
    public static async Task<ExecutionRuntimeLockDocument> LoadAsync(
        string path,
        string? expectedKind = null,
        CancellationToken cancellationToken = default)
        => (await LoadSnapshotAsync(path, expectedKind, cancellationToken)).Document;

    public static async Task<ExecutionRuntimeLockSnapshot> LoadSnapshotAsync(
        string path,
        string? expectedKind = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Execution-runtime lock '{fullPath}' was not found.", fullPath);
        }
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Execution-runtime lock '{fullPath}' must not be a link.");
        }

        var observed = new FileInfo(fullPath);
        var observedLength = observed.Length;
        var observedWriteTime = observed.LastWriteTimeUtc;
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > ExecutionRuntimeLockPolicy.MaxLockBytes)
        {
            throw new InvalidDataException(
                $"Execution-runtime lock must contain between 1 and {ExecutionRuntimeLockPolicy.MaxLockBytes} bytes.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Execution-runtime lock changed while being snapshotted.");
        }
        observed.Refresh();
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0
            || observed.Length != observedLength
            || observed.LastWriteTimeUtc != observedWriteTime)
        {
            throw new InvalidDataException("Execution-runtime lock changed while being snapshotted.");
        }
        var document = StrictJson.Deserialize<ExecutionRuntimeLockDocument>(bytes, "Execution-runtime lock");
        var errors = ExecutionRuntimeLockValidator.Validate(document);
        if (expectedKind is not null
            && !string.Equals(expectedKind, document.LockKind, StringComparison.Ordinal))
        {
            errors.Add(
                $"Execution-runtime lock kind '{document.LockKind}' does not match required kind '{expectedKind}'.");
        }
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "Execution-runtime lock validation failed:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(static error => $"- {error}")));
        }

        return new ExecutionRuntimeLockSnapshot(
            document,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}

internal sealed record ExecutionRuntimeLockSnapshot(
    ExecutionRuntimeLockDocument Document,
    string Sha256);
