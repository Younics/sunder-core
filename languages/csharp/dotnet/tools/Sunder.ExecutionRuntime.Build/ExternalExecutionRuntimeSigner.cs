using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Format;

namespace Sunder.ExecutionRuntime.Build;

internal sealed class ExternalExecutionRuntimeSigner : ISunderExecutionRuntimeArchiveSigner, IDisposable
{
    private const int MaxAdapterErrorBytes = 16 * 1024;
    private static readonly TimeSpan AdapterTimeout = TimeSpan.FromMinutes(2);
    private readonly string _adapterPath;
    private readonly string _workingRoot;
    private readonly string _approvedSha256;
    private readonly SigningFileOperations.FileStamp _ownedSnapshot;
    private readonly DateTime _rootCreatedUtc;
    private readonly CancellationToken _cancellationToken;

    private ExternalExecutionRuntimeSigner(
        string keyId, string adapterPath, string workingRoot, string approvedSha256,
        SigningFileOperations.FileStamp ownedSnapshot, DateTime rootCreatedUtc, CancellationToken cancellationToken)
    {
        KeyId = keyId;
        _adapterPath = adapterPath;
        _workingRoot = workingRoot;
        _approvedSha256 = approvedSha256;
        _ownedSnapshot = ownedSnapshot;
        _rootCreatedUtc = rootCreatedUtc;
        _cancellationToken = cancellationToken;
    }

    public string KeyId { get; }

    public static async Task<ExternalExecutionRuntimeSigner> CreateAsync(
        string keyId,
        string adapterPath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ExecutionRuntimeLockPolicy.IsBoundedIdentifier(keyId)
            || !ExecutionRuntimeLockPolicy.IsLowercaseSha256(expectedSha256))
        {
            throw new InvalidDataException("Signer adapter requires a valid key identity and lower-case SHA-256 digest.");
        }
        var fullAdapterPath = SigningFileOperations.NormalizePath(adapterPath);
        if (!File.Exists(fullAdapterPath))
        {
            throw new FileNotFoundException($"Signer adapter '{fullAdapterPath}' was not found.", fullAdapterPath);
        }
        var observed = SigningFileOperations.ObserveFile(fullAdapterPath);
        SigningFileOperations.EnsureDirectory(Path.GetTempPath());
        var workingRoot = SigningFileOperations.NormalizePath(
            Directory.CreateTempSubdirectory("Sunder.ExecutionRuntime.Build-signer-").FullName);
        var rootCreatedUtc = Directory.GetCreationTimeUtc(workingRoot);
        var snapshotPath = Path.Combine(workingRoot, "adapter" + Path.GetExtension(fullAdapterPath));
        SigningFileOperations.FileStamp? ownedSnapshot = null;
        try
        {
            SigningFileOperations.EnsureDirectory(workingRoot);
            if (observed.Length is <= 0 or > SunderExecutionRuntimeFormat.MaxArchiveBytes)
            {
                throw new InvalidDataException("Signer adapter size is outside the bounded executable limit.");
            }
            var observedLength = observed.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = new FileStream(
                             fullAdapterPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             snapshotPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long copied = 0;
                while (copied < observedLength)
                {
                    var read = await source.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, observedLength - copied)),
                        cancellationToken);
                    if (read == 0)
                    {
                        throw new InvalidDataException("Signer adapter changed while being snapshotted.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                }
                if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
                {
                    throw new InvalidDataException("Signer adapter changed while being snapshotted.");
                }
                await destination.FlushAsync(cancellationToken);
                // Preserve partial snapshots rather than infer ownership from the cleanup pathname.
                ownedSnapshot = SigningFileOperations.ObserveFile(snapshotPath);
            }

            if (SigningFileOperations.ObserveFile(fullAdapterPath) != observed)
            {
                throw new InvalidDataException("Signer adapter changed while being snapshotted.");
            }
            var actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualSha256),
                    Encoding.ASCII.GetBytes(expectedSha256)))
            {
                throw new InvalidDataException(
                    $"Signer adapter SHA-256 mismatch: expected '{expectedSha256}', observed '{actualSha256}'.");
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    snapshotPath,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            ownedSnapshot = SigningFileOperations.ObserveFile(snapshotPath);
            cancellationToken.ThrowIfCancellationRequested();
            return new ExternalExecutionRuntimeSigner(
                keyId, snapshotPath, workingRoot, expectedSha256, ownedSnapshot.Value, rootCreatedUtc, cancellationToken);
        }
        catch
        {
            SigningFileOperations.TryDeleteOwnedFile(snapshotPath, ownedSnapshot, expectedSha256);
            TryDeleteOwnedDirectory(workingRoot, rootCreatedUtc);
            throw;
        }
    }

    public byte[] Sign(ReadOnlySpan<byte> signatureInput)
        => SignAsync(signatureInput.ToArray()).GetAwaiter().GetResult();

    public void Dispose()
    {
        SigningFileOperations.TryDeleteOwnedFile(_adapterPath, _ownedSnapshot, _approvedSha256);
        TryDeleteOwnedDirectory(_workingRoot, _rootCreatedUtc);
    }

    private async Task<byte[]> SignAsync(byte[] signatureInput)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var hash = await SigningFileOperations.HashFileAsync(_adapterPath, _ownedSnapshot.Length, _cancellationToken);
        if (SigningFileOperations.ObserveFile(_adapterPath) != _ownedSnapshot
            || !CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(_approvedSha256)))
        {
            throw new InvalidDataException("Signer adapter snapshot changed before invocation.");
        }
        _cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _adapterPath,
                WorkingDirectory = _workingRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("sign-ed25519");
        process.StartInfo.ArgumentList.Add("--key-id");
        process.StartInfo.ArgumentList.Add(KeyId);
        if (!process.Start())
        {
            throw new IOException("Approved signer adapter did not start.");
        }

        using var timeout = new CancellationTokenSource(AdapterTimeout);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _cancellationToken);
        var outputTask = ReadPipeAsync(
            process.StandardOutput.BaseStream,
            SunderExecutionRuntimeFormat.Ed25519SignatureBytes);
        var errorTask = ReadPipeAsync(process.StandardError.BaseStream, MaxAdapterErrorBytes);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(signatureInput, execution.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(execution.Token);
            var signature = await outputTask;
            _ = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"Approved signer adapter failed with exit code {process.ExitCode}; its output was suppressed.");
            }
            if (signature.Length != SunderExecutionRuntimeFormat.Ed25519SignatureBytes)
            {
                throw new InvalidDataException(
                    $"Approved signer adapter must emit exactly {SunderExecutionRuntimeFormat.Ed25519SignatureBytes} raw signature bytes.");
            }
            return signature;
        }
        catch (Exception) when (_cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new OperationCanceledException(_cancellationToken);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidDataException("Approved signer adapter exceeded its bounded execution time.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
        finally
        {
            execution.Cancel();
            try
            {
                await Task.WhenAll(outputTask, errorTask);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException)
            {
                // Observe both pipe tasks after process failure/cancellation; never expose adapter stderr.
            }
        }

        async Task<byte[]> ReadPipeAsync(Stream stream, int maximumBytes)
        {
            try
            {
                return await ReadBoundedAsync(stream, maximumBytes, execution.Token);
            }
            catch
            {
                TryKill(process);
                throw;
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(maximumBytes);
        var buffer = new byte[Math.Min(maximumBytes + 1, 81920)];
        while (true)
        {
            var remaining = maximumBytes - output.Length;
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining + 1)),
                cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (read > remaining)
            {
                throw new InvalidDataException("Approved signer adapter exceeded its bounded output limit.");
            }
            output.Write(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteOwnedDirectory(string path, DateTime createdUtc)
    {
        try
        {
            SigningFileOperations.EnsureDirectory(path);
            if (Directory.GetCreationTimeUtc(path) == createdUtc)
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }
    }
}
