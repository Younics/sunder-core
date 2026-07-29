using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal enum StorageCommitPhase
{
    StateDocument,
    SecretDocument,
    MasterKey,
    MigrationBackup,
    Quarantine,
    FailureMarker,
}

internal class AtomicFileSystem
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal virtual void CreateDirectory(string path, bool sensitive)
    {
        EnsureNotReparsePoint(path);
        if (sensitive && !OperatingSystem.IsWindows() && !Directory.Exists(path))
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        if (sensitive)
        {
            RestrictDirectory(path);
        }

        EnsureNotReparsePoint(path);
    }

    internal virtual bool FileExists(string path)
    {
        var exists = File.Exists(path);
        if (exists)
        {
            EnsureNotReparsePoint(path);
        }

        return exists;
    }

    internal virtual byte[] ReadAllBytes(string path)
    {
        EnsureNotReparsePoint(path);
        return File.ReadAllBytes(path);
    }

    internal virtual IDisposable OpenExclusiveLock(string path, bool sensitive)
    {
        EnsureNotReparsePoint(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 1,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        var stream = new FileStream(path, options);
        try
        {
            if (sensitive)
            {
                RestrictFile(path);
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal virtual void WriteTempFile(string path, byte[] contents, bool sensitive)
    {
        EnsureNotReparsePoint(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        using var stream = new FileStream(path, options);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);

        if (sensitive)
        {
            RestrictFile(path);
        }
    }

    internal virtual void BeforeCommit(StorageCommitPhase phase, string destinationPath)
    {
    }

    internal virtual void ReplaceFile(string sourcePath, string destinationPath)
    {
        EnsureNotReparsePoint(sourcePath);
        EnsureNotReparsePoint(destinationPath);
        if (File.Exists(destinationPath))
        {
            File.Replace(sourcePath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(sourcePath, destinationPath);
    }

    internal virtual void DeleteFile(string path) => File.Delete(path);

    internal virtual void RestrictDirectory(string path)
    {
        EnsureNotReparsePoint(path);
        if (OperatingSystem.IsWindows())
        {
            RestrictWindowsDirectory(path);
            return;
        }

        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    internal virtual void RestrictFile(string path)
    {
        EnsureNotReparsePoint(path);
        if (OperatingSystem.IsWindows())
        {
            RestrictWindowsFile(path);
            return;
        }

        File.SetUnixFileMode(path, PrivateFileMode);
    }

    internal virtual void SyncDirectory(string path)
    {
        EnsureNotReparsePoint(path);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var descriptor = NativeMethods.Open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not open storage directory '{path}' for durability synchronization.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            if (NativeMethods.Fsync(descriptor) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                // Some POSIX filesystems do not support fsync on directories.
                if (error is not (22 or 30 or 45 or 95))
                {
                    throw new IOException(
                        $"Could not synchronize storage directory '{path}'.",
                        new Win32Exception(error));
                }
            }
        }
        finally
        {
            NativeMethods.Close(descriptor);
        }
    }

    internal virtual void EnsureNotReparsePoint(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Package storage path '{path}' must not be a symbolic link or reparse point.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsFile(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new IOException("The current Windows user does not have a security identifier.");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsDirectory(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new IOException("The current Windows user does not have a security identifier.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int Fsync(int descriptor);

        [DllImport("libc", EntryPoint = "close")]
        internal static extern int Close(int descriptor);
    }
}

internal sealed class AtomicFileDocument
{
    private const int MaxLockAttempts = 200;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly AtomicFileSystem _fileSystem;
    private readonly string _filePath;
    private readonly string _directoryPath;
    private readonly string _lockPath;
    private readonly bool _sensitive;
    private readonly StorageCommitPhase _commitPhase;

    internal AtomicFileDocument(
        string filePath,
        bool sensitive,
        StorageCommitPhase commitPhase,
        AtomicFileSystem? fileSystem = null)
    {
        _filePath = Path.GetFullPath(filePath);
        _directoryPath = Path.GetDirectoryName(_filePath)
            ?? throw new ArgumentException("The document path must have a parent directory.", nameof(filePath));
        _lockPath = $"{_filePath}.lock";
        _sensitive = sensitive;
        _commitPhase = commitPhase;
        _fileSystem = fileSystem ?? new AtomicFileSystem();
    }

    internal T Execute<T>(Func<AtomicFileTransaction, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _fileSystem.CreateDirectory(_directoryPath, _sensitive);

        IOException? lastException = null;
        for (var attempt = 0; attempt < MaxLockAttempts; attempt++)
        {
            IDisposable? lockHandle;
            try
            {
                lockHandle = _fileSystem.OpenExclusiveLock(_lockPath, _sensitive);
            }
            catch (IOException exception)
            {
                lastException = exception;
                if (attempt + 1 == MaxLockAttempts)
                {
                    break;
                }

                Thread.Sleep(LockRetryDelay);
                continue;
            }

            using (lockHandle)
            {
                return action(new AtomicFileTransaction(
                    _filePath,
                    _directoryPath,
                    _sensitive,
                    _commitPhase,
                    _fileSystem));
            }
        }

        throw new TimeoutException($"Timed out waiting for the package storage lock '{_lockPath}'.", lastException);
    }

    internal async Task<T> ExecuteAsync<T>(
        Func<AtomicFileTransaction, T> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        _fileSystem.CreateDirectory(_directoryPath, _sensitive);

        IOException? lastException = null;
        for (var attempt = 0; attempt < MaxLockAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDisposable? lockHandle;
            try
            {
                lockHandle = _fileSystem.OpenExclusiveLock(_lockPath, _sensitive);
            }
            catch (IOException exception)
            {
                lastException = exception;
                if (attempt + 1 == MaxLockAttempts)
                {
                    break;
                }

                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (lockHandle)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return action(new AtomicFileTransaction(
                    _filePath,
                    _directoryPath,
                    _sensitive,
                    _commitPhase,
                    _fileSystem));
            }
        }

        throw new TimeoutException($"Timed out waiting for the package storage lock '{_lockPath}'.", lastException);
    }
}

internal sealed class AtomicFileTransaction(
    string filePath,
    string directoryPath,
    bool sensitive,
    StorageCommitPhase commitPhase,
    AtomicFileSystem fileSystem)
{
    internal string FilePath => filePath;

    internal byte[]? Read()
    {
        if (!fileSystem.FileExists(filePath))
        {
            return null;
        }

        if (sensitive)
        {
            fileSystem.RestrictFile(filePath);
        }

        return fileSystem.ReadAllBytes(filePath);
    }

    internal void Write(byte[] contents) => WriteFile(filePath, contents, sensitive, commitPhase);

    internal string RetainMigrationBackup(byte[] contents)
    {
        var backupPath = $"{filePath}.migration-backup.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}";
        WriteFile(backupPath, contents, sensitive, StorageCommitPhase.MigrationBackup);
        return backupPath;
    }

    internal byte[] ReadRecoveryQuarantine(StorageFailureMarker marker)
    {
        var canonicalFileName = Path.GetFileName(filePath);
        if (!marker.QuarantineFile.StartsWith(canonicalFileName + ".corrupt.", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(marker.QuarantineFile), marker.QuarantineFile, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The package storage recovery marker has an invalid quarantine reference.");
        }

        var quarantinePath = Path.Combine(directoryPath, marker.QuarantineFile);
        if (!fileSystem.FileExists(quarantinePath))
        {
            throw new InvalidDataException("The package storage recovery quarantine is missing.");
        }
        if (sensitive)
        {
            fileSystem.RestrictFile(quarantinePath);
        }

        return fileSystem.ReadAllBytes(quarantinePath);
    }

    internal string QuarantineAndReplaceWithFailure(
        byte[] quarantineContents,
        string quarantineProtection,
        string failureKind)
    {
        var quarantinePath = $"{filePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}";
        WriteFile(quarantinePath, quarantineContents, sensitive, StorageCommitPhase.Quarantine);

        var marker = new StorageFailureMarker(
            StorageFailureMarker.FormatName,
            StorageFailureMarker.CurrentVersion,
            failureKind,
            Path.GetFileName(quarantinePath),
            quarantineProtection,
            DateTimeOffset.UtcNow);
        WriteFile(
            filePath,
            JsonSerializer.SerializeToUtf8Bytes(marker, PackageStorageJson.Options),
            sensitive,
            StorageCommitPhase.FailureMarker);
        return quarantinePath;
    }

    internal bool ResetFailureMarker()
    {
        var contents = Read();
        if (contents is null)
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(contents);
            if (!StorageFailureMarker.IsMarker(json.RootElement))
            {
                throw new InvalidOperationException("Package storage can only be reset when a recovery marker is present.");
            }

            fileSystem.DeleteFile(filePath);
            fileSystem.SyncDirectory(directoryPath);
            return true;
        }
        finally
        {
            if (sensitive)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(contents);
            }
        }
    }

    private void WriteFile(string destinationPath, byte[] contents, bool destinationSensitive, StorageCommitPhase phase)
    {
        var tempPath = $"{destinationPath}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            fileSystem.WriteTempFile(tempPath, contents, destinationSensitive);
            fileSystem.BeforeCommit(phase, destinationPath);
            fileSystem.ReplaceFile(tempPath, destinationPath);
            fileSystem.SyncDirectory(directoryPath);
        }
        finally
        {
            try
            {
                fileSystem.DeleteFile(tempPath);
            }
            catch (IOException)
            {
                // The destination remains authoritative; a stale temp file is never read.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original storage failure rather than masking it during cleanup.
            }
        }
    }
}

internal sealed record StorageFailureMarker(
    string Format,
    int Version,
    string FailureKind,
    string QuarantineFile,
    string QuarantineProtection,
    DateTimeOffset DetectedAtUtc)
{
    internal const string FormatName = "sunder.package-storage-failure";
    internal const int CurrentVersion = 1;

    internal static bool IsMarker(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("format", out var format)
        && format.ValueKind == JsonValueKind.String
        && string.Equals(format.GetString(), FormatName, StringComparison.Ordinal);

    internal static PackageStorageRecoveryRequiredException CreateException(
        JsonElement root,
        string canonicalPath)
    {
        var marker = Read(root);

        var quarantinePath = Path.Combine(Path.GetDirectoryName(canonicalPath)!, marker.QuarantineFile);
        return new PackageStorageRecoveryRequiredException(
            $"Package storage is locked after corruption was quarantined at '{quarantinePath}'. "
                + "Explicit recovery or reset is required.",
            quarantinePath);
    }

    internal static StorageFailureMarker Read(JsonElement root)
    {
        if (!IsMarker(root)
            || !root.TryGetProperty("version", out var versionElement)
            || !versionElement.TryGetInt32(out var version)
            || version != CurrentVersion
            || !root.TryGetProperty("failureKind", out var failureKindElement)
            || failureKindElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(failureKindElement.GetString())
            || !root.TryGetProperty("quarantineFile", out var quarantineElement)
            || quarantineElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("quarantineProtection", out var protectionElement)
            || protectionElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(protectionElement.GetString())
            || !root.TryGetProperty("detectedAtUtc", out var detectedElement)
            || !detectedElement.TryGetDateTimeOffset(out var detectedAtUtc))
        {
            throw new InvalidDataException("The package storage recovery marker is malformed.");
        }

        var quarantineFile = quarantineElement.GetString()!;
        if (!string.Equals(Path.GetFileName(quarantineFile), quarantineFile, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The package storage recovery marker has an invalid quarantine reference.");
        }

        return new StorageFailureMarker(
            FormatName,
            CurrentVersion,
            failureKindElement.GetString()!,
            quarantineFile,
            protectionElement.GetString()!,
            detectedAtUtc);
    }
}
