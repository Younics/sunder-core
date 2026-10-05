using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Package.Projections;

internal static class PackageProjectionJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static byte[] SerializeCanonical<T>(T value)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options) + "\n");
}

internal static class PackageProjectionDurableFile
{
    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = PackageProjectionJson.SerializeCanonical(value);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            SyncDirectory(Path.GetDirectoryName(path)!);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static async Task<(T Value, long Length)> ReadAsync<T>(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"Projection metadata '{path}' has an invalid size.");
        }
        var value = await JsonSerializer.DeserializeAsync<T>(stream, PackageProjectionJson.Options, cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Projection metadata '{path}' is empty.");
        return (value, stream.Length);
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            SyncDirectory(Path.GetDirectoryName(path)!);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            SyncDirectory(Path.GetDirectoryName(path)!);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public static void MoveDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        Directory.Move(sourcePath, destinationPath);
        SyncDirectory(Path.GetDirectoryName(sourcePath)!);
        SyncDirectory(Path.GetDirectoryName(destinationPath)!);
    }

    public static void SyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return;
        }
        var descriptor = open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not open projection directory '{path}' for synchronization.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        try
        {
            if (fsync(descriptor) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is not (22 or 30 or 45 or 95))
                {
                    throw new IOException(
                        $"Could not synchronize projection directory '{path}'.",
                        new Win32Exception(error));
                }
            }
        }
        finally
        {
            close(descriptor);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("libc")]
    private static extern int close(int descriptor);
}

internal sealed class PackageProjectionFileLock : IDisposable
{
    private static readonly Dictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly object GatesSync = new();
    private readonly SemaphoreSlim _gate;
    private FileStream? _stream;

    private PackageProjectionFileLock(SemaphoreSlim gate, FileStream stream)
    {
        _gate = gate;
        _stream = stream;
    }

    public static async Task<PackageProjectionFileLock> AcquireAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        var fullPath = Path.GetFullPath(path);
        SemaphoreSlim gate;
        lock (GatesSync)
        {
            if (!Gates.TryGetValue(fullPath, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                Gates.Add(fullPath, gate);
            }
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await gate.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out acquiring projection lock '{fullPath}'.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            while (true)
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        fullPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.None);
                    return new PackageProjectionFileLock(gate, stream);
                }
                catch (IOException)
                {
                    await Task.Delay(25, timeoutSource.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            gate.Release();
            throw new TimeoutException($"Timed out acquiring projection lock '{fullPath}'.");
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stream, null) is not { } stream)
        {
            return;
        }
        stream.Dispose();
        _gate.Release();
    }
}

internal sealed class PackageProjectionFamilyLock : IDisposable
{
    private const int RetryDelayMilliseconds = 25;
    private FileStream? _stream;

    private PackageProjectionFamilyLock(FileStream stream)
    {
        _stream = stream;
    }

    public static Task<PackageProjectionFamilyLock> AcquireSharedAsync(
        string path,
        string trustedRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => AcquireAsync(path, trustedRoot, FileShare.ReadWrite, timeout, cancellationToken);

    public static Task<PackageProjectionFamilyLock> AcquireExclusiveAsync(
        string path,
        string trustedRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => AcquireAsync(path, trustedRoot, FileShare.None, timeout, cancellationToken);

    public void Dispose()
        => Interlocked.Exchange(ref _stream, null)?.Dispose();

    private static async Task<PackageProjectionFamilyLock> AcquireAsync(
        string path,
        string trustedRoot,
        FileShare share,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(trustedRoot);
        PackageProjectionLayout.EnsureContained(fullPath, fullRoot);
        EnsureSafeParentPath(fullPath, fullRoot);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (true)
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        fullPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        share,
                        bufferSize: 1,
                        FileOptions.None);
                    try
                    {
                        EnsureRegularLockFile(fullPath);
                        VerifyLockEnforced(fullPath);
                        return new PackageProjectionFamilyLock(stream);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
                catch (IOException exception) when (IsContention(exception))
                {
                    await Task.Delay(RetryDelayMilliseconds, timeoutSource.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out acquiring projection coordination lock '{fullPath}'.");
        }
    }

    private static void EnsureSafeParentPath(string path, string trustedRoot)
    {
        var parent = Path.GetDirectoryName(path)
                     ?? throw new InvalidDataException("The projection coordination lock has no parent directory.");
        var current = parent;
        while (!PathsEqual(current, trustedRoot))
        {
            if (Path.Exists(current))
            {
                EnsureDirectory(current);
            }
            current = Path.GetDirectoryName(current)
                      ?? throw new InvalidDataException("The projection coordination lock is outside its trusted root.");
        }
        EnsureDirectory(trustedRoot);
        Directory.CreateDirectory(parent);

        current = parent;
        while (!PathsEqual(current, trustedRoot))
        {
            EnsureDirectory(current);
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static void EnsureDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Projection coordination path '{path}' must be a physical directory.");
        }
    }

    private static void EnsureRegularLockFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException($"Projection coordination lock '{path}' must be a regular file.");
        }
    }

    private static void VerifyLockEnforced(string path)
    {
        try
        {
            using var unexpected = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception) when (IsContention(exception))
        {
            return;
        }
        throw new PlatformNotSupportedException(
            $"The filesystem containing projection lock '{path}' does not enforce cross-process file sharing locks.");
    }

    private static bool IsContention(IOException exception)
        => (exception.HResult & 0xffff) is 11 or 13 or 32 or 33 or 35;

    private static bool PathsEqual(string first, string second)
        => string.Equals(
            Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed class SystemPackageProjectionProcessIdentityProvider : IPackageProjectionProcessIdentityProvider
{
    public PackageProjectionProcessIdentity ObserveCurrent()
    {
        using var process = Process.GetCurrentProcess();
        return new PackageProjectionProcessIdentity(
            process.Id,
            CaptureBirthIdentity(process),
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }

    public PackageProjectionProcessProbe Probe(int processId, string birthIdentity)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return PackageProjectionProcessProbe.Missing;
            }
            return string.Equals(CaptureBirthIdentity(process), birthIdentity, StringComparison.Ordinal)
                ? PackageProjectionProcessProbe.Matching
                : PackageProjectionProcessProbe.BirthMismatch;
        }
        catch (ArgumentException)
        {
            return PackageProjectionProcessProbe.Missing;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or Win32Exception
                                           or NotSupportedException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return PackageProjectionProcessProbe.Unavailable;
        }
    }

    internal static string CaptureBirthIdentity(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            var closingParenthesis = stat.LastIndexOf(')');
            if (closingParenthesis < 0)
            {
                throw new InvalidDataException($"Process {process.Id} has invalid Linux start metadata.");
            }
            var fields = stat[(closingParenthesis + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19
                || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new InvalidDataException($"Process {process.Id} has invalid Linux start metadata.");
            }
            var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim().ToLowerInvariant();
            if (!Guid.TryParseExact(bootId, "D", out _))
            {
                throw new InvalidDataException("The Linux boot identity is invalid.");
            }
            return $"linux:{bootId}:{fields[19]}";
        }

        var ticks = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
        {
            return "windows:" + ticks;
        }
        if (OperatingSystem.IsMacOS())
        {
            return "darwin:" + ticks;
        }
        throw new PlatformNotSupportedException("Process birth identity is unavailable on this operating system.");
    }
}

internal sealed record PackageProjectionHealthV1(
    int SchemaVersion,
    string CacheKey,
    string ReceiptSha256,
    DateTimeOffset CompletedAtUtc);

internal sealed record PackageProjectionLeaseV1(
    int SchemaVersion,
    string LeaseId,
    string CacheKey,
    PackageProjectionRole ConsumerRole,
    string ProjectionRootPath,
    string OwnerKind,
    Guid HostInstanceId,
    string PackageId,
    string GenerationId,
    int OwnerProcessId,
    string OwnerProcessBirthIdentity,
    DateTimeOffset OwnerProcessStartedAtUtc,
    DateTimeOffset CreatedAtUtc,
    string? ProcessTreeLeaseId = null,
    IReadOnlyList<PackageProjectionProcessIdentity>? ProcessTreeProcesses = null,
    DateTimeOffset? ProcessTreeAttachedAtUtc = null);

internal sealed record PackageProjectionTombstoneV1(
    int SchemaVersion,
    string CacheKey,
    PackageProjectionRole ConsumerRole,
    string SourceContentIdentity,
    DateTimeOffset MarkedAtUtc,
    DateTimeOffset DeleteAfterUtc);
