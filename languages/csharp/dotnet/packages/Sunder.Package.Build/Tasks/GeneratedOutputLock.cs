using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Sunder.Package.Build.Tasks;

internal sealed class GeneratedOutputLock : IDisposable
{
    internal const string LockSuffix = ".sunder-output.lock";
    internal const string OwnerFileName = "owner.json";
    internal const string TargetLeafDiscoverySuffix = ".sunder-target-leaf-container";

    private const string TransitionSuffix = ".transition";
    private const string ProcessMarkerDirectory = "sunder-generated-output/processes";
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly Lazy<HostIdentity> CurrentHostIdentity = new(ReadCurrentHostIdentity);
    private static readonly Lazy<ProcessMarkerRegistration> CurrentProcessMarker = new(CreateProcessMarker);

    private readonly IReadOnlyList<OwnedDirectory> _locks;
    private bool _disposed;

    private GeneratedOutputLock(IReadOnlyList<OwnedDirectory> locks)
        => _locks = locks;

    public static GeneratedOutputLock Acquire(IEnumerable<string> paths, TimeSpan? waitTimeout = null)
        => Acquire([], paths, waitTimeout);

    public static GeneratedOutputLock Acquire(
        IEnumerable<string> containerPaths,
        IEnumerable<string> outputPaths,
        TimeSpan? waitTimeout = null)
    {
        var containers = NormalizePaths(containerPaths);
        var containerSet = containers.ToHashSet(PathComparer);
        var outputs = NormalizePaths(outputPaths)
            .Where(path => !containerSet.Contains(path));
        var ordered = containers.Concat(outputs).ToArray();
        var timeout = waitTimeout ?? DefaultWaitTimeout;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        var deadline = DateTime.UtcNow + timeout;
        var locks = new List<OwnedDirectory>(ordered.Length);
        try
        {
            foreach (var path in ordered) locks.Add(AcquireOne(path, deadline, timeout));
            return new GeneratedOutputLock(locks);
        }
        catch (Exception acquisitionException)
        {
            List<Exception>? releaseErrors = null;
            for (var index = locks.Count - 1; index >= 0; index--)
            {
                try
                {
                    Release(locks[index], DateTime.UtcNow + timeout, timeout);
                }
                catch (Exception releaseException)
                {
                    (releaseErrors ??= []).Add(releaseException);
                }
            }
            if (releaseErrors is null) throw;
            throw new AggregateException(
                "Generated output lock acquisition failed and one or more prior locks could not be released safely.",
                [acquisitionException, .. releaseErrors]);
        }
    }

    public static string TargetLeafDiscoveryKey(string discoveryRoot, string containedPath)
    {
        if (string.IsNullOrWhiteSpace(discoveryRoot))
        {
            throw new ArgumentException("Generated output discovery root must be explicit and nonempty.", nameof(discoveryRoot));
        }
        if (string.IsNullOrWhiteSpace(containedPath))
        {
            throw new ArgumentException("Generated output path must be nonempty.", nameof(containedPath));
        }
        var root = NormalizePath(discoveryRoot);
        var path = NormalizePath(containedPath);
        if (!PathsEqual(root, path) && !IsDescendantOf(path, root))
        {
            throw new ArgumentException($"Generated output '{path}' is outside configured discovery root '{root}'.");
        }
        return root + TargetLeafDiscoverySuffix;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var timeout = DefaultWaitTimeout;
        List<Exception>? errors = null;
        for (var index = _locks.Count - 1; index >= 0; index--)
        {
            try
            {
                Release(_locks[index], DateTime.UtcNow + timeout, timeout);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }
        if (errors is not null) throw new AggregateException("One or more generated output locks could not be released safely.", errors);
    }

    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string> paths)
        => paths
            .Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(PathComparer)
            .Order(PathComparer)
            .ToArray();

    private static OwnedDirectory AcquireOne(string outputPath, DateTime deadline, TimeSpan timeout)
    {
        var lockPath = outputPath + LockSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)
                                  ?? throw new InvalidOperationException($"Output lock '{lockPath}' has no parent directory."));
        while (true)
        {
            OwnedDirectory? acquired = null;
            string? quarantinePath = null;
            var transition = AcquireTransitionGuard(lockPath, deadline, timeout);
            try
            {
                if (!PathExists(lockPath))
                {
                    acquired = TryPublishOwnedDirectory(lockPath)
                               ?? throw new IOException($"Generated output lock '{lockPath}' changed while its transition guard was held.");
                }
                else
                {
                    EnsureDirectory(lockPath, "generated output lock");
                    var candidate = TryReadOwner(lockPath);
                    if (candidate is not null && GetOwnerState(candidate) == OwnerState.Dead)
                    {
                        var current = TryReadOwner(lockPath);
                        if (current is not null
                            && string.Equals(current.Token, candidate.Token, StringComparison.Ordinal))
                        {
                            quarantinePath = lockPath + ".stale-" + candidate.Token + "-" + Guid.NewGuid().ToString("N");
                            if (!TryMoveDirectory(lockPath, quarantinePath)) quarantinePath = null;
                        }
                    }
                }
            }
            finally
            {
                ReleaseTransitionGuard(transition, deadline, timeout);
            }

            if (quarantinePath is not null) TryDeleteDirectory(quarantinePath);
            if (acquired is not null) return acquired;
            ThrowIfTimedOut(deadline, timeout, lockPath);
            Thread.Sleep(Random.Shared.Next(25, 101));
        }
    }

    private static OwnedDirectory AcquireTransitionGuard(string lockPath, DateTime deadline, TimeSpan timeout)
    {
        var guardPath = lockPath + TransitionSuffix;
        while (true)
        {
            var owned = TryPublishOwnedDirectory(guardPath);
            if (owned is not null) return owned;
            if (!TryEnsureDirectory(guardPath, "generated output transition guard"))
            {
                ThrowIfTimedOut(deadline, timeout, guardPath);
                continue;
            }
            var candidate = TryReadOwner(guardPath);
            if (candidate is not null && GetOwnerState(candidate) == OwnerState.Dead)
            {
                var current = TryReadOwner(guardPath);
                if (current is not null
                    && string.Equals(current.Token, candidate.Token, StringComparison.Ordinal))
                {
                    // This deterministic nonempty tombstone permanently fences contenders that
                    // paused after reading the dead guard but before attempting its rename.
                    var tombstonePath = guardPath + ".stale-" + candidate.Token;
                    if (!PathExists(tombstonePath)) TryMoveDirectory(guardPath, tombstonePath);
                }
            }
            ThrowIfTimedOut(deadline, timeout, guardPath);
            Thread.Sleep(Random.Shared.Next(25, 101));
        }
    }

    private static void Release(OwnedDirectory owned, DateTime deadline, TimeSpan timeout)
    {
        while (true)
        {
            string? quarantinePath = null;
            var ownerUnreadable = false;
            var transition = AcquireTransitionGuard(owned.Path, deadline, timeout);
            try
            {
                var current = TryReadOwner(owned.Path);
                if (current is null)
                {
                    if (!PathExists(owned.Path))
                    {
                        ReleaseProcessMarkerOwnership(owned);
                        return;
                    }
                    ownerUnreadable = true;
                }
                else if (!string.Equals(current.Token, owned.Token, StringComparison.Ordinal))
                {
                    ReleaseProcessMarkerOwnership(owned);
                    return;
                }
                if (!ownerUnreadable) current = TryReadOwner(owned.Path);
                if (!ownerUnreadable && current is null)
                {
                    if (!PathExists(owned.Path))
                    {
                        ReleaseProcessMarkerOwnership(owned);
                        return;
                    }
                    ownerUnreadable = true;
                }
                else if (!ownerUnreadable && !string.Equals(current!.Token, owned.Token, StringComparison.Ordinal))
                {
                    ReleaseProcessMarkerOwnership(owned);
                    return;
                }
                if (!ownerUnreadable)
                {
                    var candidate = owned.Path + ".release-" + owned.Token + "-" + Guid.NewGuid().ToString("N");
                    if (TryMoveDirectory(owned.Path, candidate)) quarantinePath = candidate;
                }
            }
            finally
            {
                ReleaseTransitionGuard(transition, deadline, timeout);
            }
            if (quarantinePath is not null)
            {
                TryDeleteDirectory(quarantinePath);
                ReleaseProcessMarkerOwnership(owned);
                return;
            }
            ThrowIfTimedOut(deadline, timeout, owned.Path);
            Thread.Sleep(Random.Shared.Next(25, 101));
        }
    }

    private static void ReleaseTransitionGuard(OwnedDirectory guard, DateTime deadline, TimeSpan timeout)
    {
        while (true)
        {
            var current = TryReadOwner(guard.Path);
            if (current is null)
            {
                if (!PathExists(guard.Path))
                {
                    ReleaseProcessMarkerOwnership(guard);
                    return;
                }
                ThrowIfTimedOut(deadline, timeout, guard.Path);
                Thread.Sleep(Random.Shared.Next(25, 101));
                continue;
            }
            if (!string.Equals(current.Token, guard.Token, StringComparison.Ordinal))
            {
                ReleaseProcessMarkerOwnership(guard);
                return;
            }
            current = TryReadOwner(guard.Path);
            if (current is null)
            {
                if (!PathExists(guard.Path))
                {
                    ReleaseProcessMarkerOwnership(guard);
                    return;
                }
                ThrowIfTimedOut(deadline, timeout, guard.Path);
                Thread.Sleep(Random.Shared.Next(25, 101));
                continue;
            }
            if (!string.Equals(current.Token, guard.Token, StringComparison.Ordinal))
            {
                ReleaseProcessMarkerOwnership(guard);
                return;
            }
            var releasePath = guard.Path + ".guard-release-" + guard.Token;
            if (TryMoveDirectory(guard.Path, releasePath))
            {
                TryDeleteDirectory(releasePath);
                ReleaseProcessMarkerOwnership(guard);
                return;
            }
            ThrowIfTimedOut(deadline, timeout, guard.Path);
            Thread.Sleep(Random.Shared.Next(25, 101));
        }
    }

    private static OwnedDirectory? TryPublishOwnedDirectory(string path)
    {
        var processMarker = CurrentProcessMarker.Value;
        var owner = CreateOwner(processMarker);
        var candidatePath = path + ".candidate-" + owner.Token;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(candidatePath);
        var retained = false;
        var published = false;
        try
        {
            WriteDurableJson(Path.Combine(candidatePath, OwnerFileName), owner);
            RetainProcessMarkerOwnership(processMarker);
            retained = true;
            try
            {
                Directory.Move(candidatePath, path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            published = true;
            return new OwnedDirectory(path, owner.Token, processMarker);
        }
        finally
        {
            if (retained && !published) DecrementProcessMarkerOwnership(processMarker);
            TryDeleteDirectory(candidatePath);
        }
    }

    private static LockOwner CreateOwner(ProcessMarkerRegistration processMarker)
    {
        var marker = processMarker.Marker;
        return new LockOwner(
            2,
            Guid.NewGuid().ToString("D"),
            marker.Pid,
            marker.Hostname,
            marker.ProcessStartedAt,
            marker.Platform,
            processMarker.Path,
            marker.Token,
            marker.LinuxBootId,
            marker.LinuxPidNamespace,
            DateTime.UtcNow);
    }

    private static LockOwner? TryReadOwner(string directory)
    {
        try
        {
            var ownerPath = Path.Combine(directory, OwnerFileName);
            var attributes = File.GetAttributes(ownerPath);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0) return null;
            var owner = JsonSerializer.Deserialize<LockOwner>(
                File.ReadAllText(ownerPath),
                JsonOptions);
            return owner is { SchemaVersion: 2, Pid: > 0 }
                   && Guid.TryParseExact(owner.Token, "D", out _)
                   && !string.IsNullOrWhiteSpace(owner.Hostname)
                   && !string.IsNullOrWhiteSpace(owner.ProcessStartedAt)
                   && IsSupportedPlatform(owner.Platform)
                   && !string.IsNullOrWhiteSpace(owner.ProcessMarkerPath)
                   && Guid.TryParseExact(owner.ProcessMarkerToken, "D", out _)
                   && ValidLinuxProof(owner.Platform, owner.LinuxBootId, owner.LinuxPidNamespace)
                ? owner
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static OwnerState GetOwnerState(LockOwner owner)
    {
        if (!HasValidLocalProcessMarker(owner)) return OwnerState.Unknown;
        try
        {
            using var process = Process.GetProcessById(owner.Pid);
            if (process.HasExited) return OwnerState.Dead;
            var actualStart = GetProcessStartIdentity(process);
            if (!string.Equals(actualStart, owner.ProcessStartedAt, StringComparison.Ordinal)) return OwnerState.Dead;
            return string.Equals(owner.Platform, "darwin", StringComparison.Ordinal)
                ? OwnerState.Unknown
                : OwnerState.Alive;
        }
        catch (ArgumentException)
        {
            return OwnerState.Dead;
        }
        catch
        {
            return OwnerState.Unknown;
        }
    }

    private static ProcessMarkerRegistration CreateProcessMarker()
    {
        using var process = Process.GetCurrentProcess();
        var host = CurrentHostIdentity.Value;
        var token = Guid.NewGuid().ToString("D");
        var marker = new ProcessMarker(
            1,
            token,
            Environment.ProcessId,
            host.Hostname,
            GetProcessStartIdentity(process),
            host.Platform,
            host.LinuxBootId,
            host.LinuxPidNamespace,
            DateTime.UtcNow);
        var root = ProcessMarkerRoot();
        Directory.CreateDirectory(root);
        EnsureDirectory(root, "generated output process marker root");
        var path = Path.Combine(root, token + ".json");
        WriteDurableJson(path, marker);
        var registration = new ProcessMarkerRegistration(path, marker);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupProcessMarkerAtExit(registration);
        return registration;
    }

    private static HostIdentity ReadCurrentHostIdentity()
    {
        var hostname = CanonicalHostname(Environment.MachineName);
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new InvalidOperationException("Could not establish the current host identity for generated output locking.");
        }
        var platform = CurrentPlatform();
        if (!string.Equals(platform, "linux", StringComparison.Ordinal))
        {
            return new HostIdentity(hostname, platform, null, null);
        }
        var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim().ToLowerInvariant();
        var pidNamespace = new FileInfo("/proc/self/ns/pid").LinkTarget;
        if (!Guid.TryParseExact(bootId, "D", out _) || !IsPidNamespace(pidNamespace))
        {
            throw new InvalidDataException("Could not establish the current Linux boot and PID namespace identity for generated output locking.");
        }
        return new HostIdentity(hostname, platform, bootId, pidNamespace);
    }

    private static bool HasValidLocalProcessMarker(LockOwner owner)
    {
        try
        {
            var markerRoot = ProcessMarkerRoot();
            var rootAttributes = File.GetAttributes(markerRoot);
            if ((rootAttributes & FileAttributes.Directory) == 0 || (rootAttributes & FileAttributes.ReparsePoint) != 0) return false;
            var expectedPath = Path.Combine(markerRoot, owner.ProcessMarkerToken + ".json");
            if (!PathsEqual(Path.GetFullPath(owner.ProcessMarkerPath), expectedPath)) return false;
            var attributes = File.GetAttributes(expectedPath);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0) return false;
            var marker = JsonSerializer.Deserialize<ProcessMarker>(File.ReadAllText(expectedPath), JsonOptions);
            if (marker is not { SchemaVersion: 1, Pid: > 0 }
                || !string.Equals(marker.Token, owner.ProcessMarkerToken, StringComparison.Ordinal)
                || marker.Pid != owner.Pid
                || !string.Equals(marker.Hostname, owner.Hostname, StringComparison.Ordinal)
                || !string.Equals(marker.ProcessStartedAt, owner.ProcessStartedAt, StringComparison.Ordinal)
                || !string.Equals(marker.Platform, owner.Platform, StringComparison.Ordinal)
                || !string.Equals(marker.LinuxBootId, owner.LinuxBootId, StringComparison.Ordinal)
                || !string.Equals(marker.LinuxPidNamespace, owner.LinuxPidNamespace, StringComparison.Ordinal)
                || marker.CreatedAt == default)
            {
                return false;
            }
            var local = CurrentHostIdentity.Value;
            return string.Equals(CanonicalHostname(local.Hostname), CanonicalHostname(owner.Hostname), StringComparison.Ordinal)
                   && string.Equals(local.Platform, owner.Platform, StringComparison.Ordinal)
                   && string.Equals(local.LinuxBootId, owner.LinuxBootId, StringComparison.Ordinal)
                   && string.Equals(local.LinuxPidNamespace, owner.LinuxPidNamespace, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string ProcessMarkerRoot()
        => NormalizePath(Path.Combine(Path.GetTempPath(), ProcessMarkerDirectory));

    private static string CurrentPlatform()
        => OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : throw new PlatformNotSupportedException("Generated output process identity is unavailable on this operating system.");

    private static bool IsSupportedPlatform(string? value)
        => value is "linux" or "win32" or "darwin";

    private static string CanonicalHostname(string value)
    {
        var normalized = value.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized.EndsWith(".local", StringComparison.Ordinal)
            ? normalized[..^".local".Length]
            : normalized;
    }

    private static bool ValidLinuxProof(string platform, string? bootId, string? pidNamespace)
        => string.Equals(platform, "linux", StringComparison.Ordinal)
            ? Guid.TryParseExact(bootId, "D", out _) && IsPidNamespace(pidNamespace)
            : bootId is null && pidNamespace is null;

    private static bool IsPidNamespace(string? value)
        => value is not null
           && value.Length > 6
           && value.StartsWith("pid:[", StringComparison.Ordinal)
           && value.EndsWith(']')
           && value.AsSpan(5, value.Length - 6).IndexOfAnyExceptInRange('0', '9') < 0;

    private static void RetainProcessMarkerOwnership(ProcessMarkerRegistration registration)
        => Interlocked.Increment(ref registration.OwnedDirectoryCount);

    private static void DecrementProcessMarkerOwnership(ProcessMarkerRegistration registration)
    {
        if (Interlocked.Decrement(ref registration.OwnedDirectoryCount) < 0)
        {
            throw new InvalidOperationException("Generated output process marker ownership underflowed.");
        }
    }

    private static void ReleaseProcessMarkerOwnership(OwnedDirectory owned)
    {
        if (Interlocked.Exchange(ref owned.MarkerReleased, 1) == 0)
        {
            DecrementProcessMarkerOwnership(owned.ProcessMarker);
        }
    }

    private static void CleanupProcessMarkerAtExit(ProcessMarkerRegistration registration)
    {
        if (Volatile.Read(ref registration.OwnedDirectoryCount) != 0) return;
        try
        {
            var attributes = File.GetAttributes(registration.Path);
            if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0) return;
            var marker = JsonSerializer.Deserialize<ProcessMarker>(File.ReadAllText(registration.Path), JsonOptions);
            if (marker is { SchemaVersion: 1 }
                && string.Equals(marker.Token, registration.Marker.Token, StringComparison.Ordinal))
            {
                File.Delete(registration.Path);
            }
        }
        catch
        {
            // A missing or replaced marker must not be modified during process shutdown.
        }
    }

    private static string GetProcessStartIdentity(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            var closingParenthesis = stat.LastIndexOf(')');
            if (closingParenthesis < 0) throw new InvalidDataException($"Process {process.Id} has invalid /proc start metadata.");
            var fields = stat[(closingParenthesis + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new InvalidDataException($"Process {process.Id} has invalid /proc start metadata.");
            }
            var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim().ToLowerInvariant();
            if (!Guid.TryParseExact(bootId, "D", out _)) throw new InvalidDataException("Linux boot identity is invalid.");
            return "linux:" + bootId + ":" + fields[19];
        }
        if (OperatingSystem.IsWindows())
        {
            return "windows:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        }
        if (OperatingSystem.IsMacOS())
        {
            var utc = process.StartTime.ToUniversalTime();
            var canonical = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc);
            return "darwin:" + canonical.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
        throw new PlatformNotSupportedException("Generated output process identity is unavailable on this operating system.");
    }

    private static void EnsureDirectory(string path, string label)
    {
        if (!Directory.Exists(path) || GeneratedOutputPathSafety.IsReparsePoint(path))
        {
            throw new IOException($"The {label} '{path}' is not a regular directory.");
        }
    }

    private static bool TryEnsureDirectory(string path, string label)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The {label} '{path}' is not a regular directory.");
        }
        return true;
    }

    private static bool TryMoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteDurableJson<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, value, JsonOptions);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Quarantined locks and unpublished candidates are safe to collect later.
        }
    }

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void ThrowIfTimedOut(DateTime deadline, TimeSpan timeout, string path)
    {
        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException($"Timed out after {timeout.TotalMilliseconds:F0} ms waiting for generated output lock '{path}'.");
        }
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right), PathComparison);

    private static bool IsDescendantOf(string path, string root)
        => NormalizePath(path).StartsWith(NormalizePath(root) + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class OwnedDirectory(string path, string token, ProcessMarkerRegistration processMarker)
    {
        public string Path { get; } = path;
        public string Token { get; } = token;
        public ProcessMarkerRegistration ProcessMarker { get; } = processMarker;
        public int MarkerReleased;
    }

    private sealed class ProcessMarkerRegistration(string path, ProcessMarker marker)
    {
        public string Path { get; } = path;
        public ProcessMarker Marker { get; } = marker;
        public int OwnedDirectoryCount;
    }

    private enum OwnerState
    {
        Alive,
        Dead,
        Unknown,
    }

    private sealed record LockOwner(
        int SchemaVersion,
        string Token,
        int Pid,
        string Hostname,
        string ProcessStartedAt,
        string Platform,
        string ProcessMarkerPath,
        string ProcessMarkerToken,
        string? LinuxBootId,
        string? LinuxPidNamespace,
        DateTime AcquiredAt);

    private sealed record ProcessMarker(
        int SchemaVersion,
        string Token,
        int Pid,
        string Hostname,
        string ProcessStartedAt,
        string Platform,
        string? LinuxBootId,
        string? LinuxPidNamespace,
        DateTime CreatedAt);

    private sealed record HostIdentity(
        string Hostname,
        string Platform,
        string? LinuxBootId,
        string? LinuxPidNamespace);
}
