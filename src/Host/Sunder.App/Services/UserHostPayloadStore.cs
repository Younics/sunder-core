using System.Security.Cryptography;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sunder.Host.Client;
using Sunder.Host.Contracts;

namespace Sunder.App.Services;

internal sealed class UserHostPayloadStore
{
    private const int DescriptorVersion = 1;
    private const string MarkerFileName = ".sunder-host-payload.json";
    private const string RootMarkerFileName = ".sunder-host-payload-root.json";
    private const string RootMarkerIdentity = "dev.sunder.host-payload-root";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _sourceDirectory;
    private readonly string _payloadRoot;
    private readonly string _installationLockPath;
    private readonly string _payloadVersion;

    public UserHostPayloadStore(
        string? sourceDirectory = null,
        string? payloadRoot = null,
        string? payloadVersion = null)
    {
        _sourceDirectory = Path.GetFullPath(
            sourceDirectory ?? Path.Combine(AppContext.BaseDirectory, "RuntimeHost"));
        _payloadRoot = Path.GetFullPath(payloadRoot ?? GetDefaultPayloadRoot());
        _installationLockPath = GetInstallationLockPath(_payloadRoot);
        _payloadVersion = string.IsNullOrWhiteSpace(payloadVersion)
            ? SunderAppVersion.CurrentText
            : payloadVersion;
    }

    internal string InstallationLockPath => _installationLockPath;

    public UserHostPayload Prepare()
    {
        if (!Directory.Exists(_sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The bundled Sunder Host payload was not found at '{_sourceDirectory}'.");
        }

        var activationLease = new UserHostPayloadActivationLease(
            _installationLockPath,
            AcquireInstallationLock());
        try
        {
            Directory.CreateDirectory(_payloadRoot);
            SetPrivateDirectoryMode(_payloadRoot);
            EnsurePayloadRootMarker();

            var directoryName = CreatePayloadDirectoryName(_payloadVersion);
            var destination = Path.Combine(_payloadRoot, directoryName);
            var currentDescriptor = LoadCurrentPayloadDescriptor();
            var currentPath = currentDescriptor?.PayloadPath;
            var stagedRepair = false;
            if (!IsCompletePayload(destination, _payloadVersion))
            {
                if (Directory.Exists(destination))
                {
                    stagedRepair = true;
                    var repairDestination = Path.Combine(_payloadRoot, $"{directoryName}-repair");
                    destination = IsCompletePayload(repairDestination, _payloadVersion)
                        ? repairDestination
                        : Directory.Exists(repairDestination)
                            ? Path.Combine(_payloadRoot, $"{directoryName}-repair-{Guid.NewGuid():N}")
                            : repairDestination;
                }
                if (!IsCompletePayload(destination, _payloadVersion))
                {
                    StagePayload(destination);
                }
            }

            var executablePath = ResolveSupervisorExecutable(destination)
                ?? throw new InvalidDataException("The staged Sunder Host payload does not contain the Supervisor executable.");
            var previous = LoadCurrentPayload(currentDescriptor, activationLease);
            if (previous is not null && PathEquals(previous.DirectoryPath, destination))
            {
                previous = null;
            }
            var replacesCurrent = stagedRepair
                                  || (currentPath is not null && !PathEquals(currentPath, destination));
            return new UserHostPayload(
                _payloadVersion,
                destination,
                executablePath,
                previous,
                replacesCurrent,
                activationLease);
        }
        catch
        {
            activationLease.Dispose();
            throw;
        }
    }

    public void Commit(UserHostPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        EnsurePathIsDirectChild(payload.DirectoryPath);
        payload.ActivationLease.EnsureActiveFor(_installationLockPath);
        if (!Directory.Exists(_payloadRoot)
            || !IsCompletePayload(payload.DirectoryPath, payload.Version))
        {
            throw new InvalidDataException("The Sunder Host payload cannot be activated because it is incomplete.");
        }
        EnsurePayloadRootMarker();
        var staleDirectories = Directory.EnumerateDirectories(_payloadRoot)
            .Where(directory => !PathEquals(directory, payload.DirectoryPath))
            .ToArray();
        var safeStaleDirectories = new List<string>(staleDirectories.Length);
        foreach (var directory in staleDirectories)
        {
            try
            {
                EnsurePathIsDirectChild(directory);
                EnsureSafePayloadDirectory(directory);
                safeStaleDirectories.Add(directory);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                AppSessionLog.WriteError(
                    $"Preserved unsafe inactive current-user Host payload '{Path.GetFileName(directory)}'.",
                    exception);
            }
        }
        var descriptor = new UserHostPayloadDescriptor(
            DescriptorVersion,
            payload.Version,
            payload.DirectoryPath);
        WriteJsonAtomically(GetDescriptorPath(), descriptor);

        foreach (var directory in safeStaleDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppSessionLog.WriteError(
                    $"Could not remove inactive current-user Host payload '{Path.GetFileName(directory)}'.",
                    exception);
            }
        }
        payload.Dispose();
    }

    public bool Matches(UserHostPayload payload, HostHandshakeResponse handshake)
        => string.Equals(
               handshake.Product.InformationalVersion,
               payload.Version,
               StringComparison.Ordinal)
            && HostProtocolCompatibility.GetManagedSupervisorIncompatibility(handshake) is null;

    public void Abandon(UserHostPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        EnsurePathIsDirectChild(payload.DirectoryPath);
        payload.ActivationLease.EnsureFor(_installationLockPath);
        using var installationLock = payload.ActivationLease.IsActive
            ? null
            : AcquireInstallationLock();
        try
        {
            if (!Directory.Exists(_payloadRoot))
            {
                return;
            }
            EnsurePayloadRootMarker();
            var current = LoadCurrentPayloadDescriptor();
            if (current is not null && PathEquals(current.PayloadPath, payload.DirectoryPath))
            {
                return;
            }
            if (!Directory.Exists(payload.DirectoryPath))
            {
                return;
            }
            EnsureSafePayloadDirectory(payload.DirectoryPath);
            Directory.Delete(payload.DirectoryPath, recursive: true);
        }
        finally
        {
            payload.Dispose();
        }
    }

    public static string GetDefaultPayloadRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SUNDER_HOST_PAYLOAD_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        }
        return Path.Combine(localApplicationData, "Sunder", "host", "payloads");
    }

    internal static bool TryRemoveStagedPayloads(string payloadRoot, TimeSpan lockWait)
    {
        payloadRoot = Path.GetFullPath(payloadRoot);
        using var installationLock = TryAcquireInstallationLock(payloadRoot, lockWait);
        if (installationLock is null)
        {
            return false;
        }
        RemoveStagedPayloadsCore(payloadRoot);
        return true;
    }

    private static void RemoveStagedPayloadsCore(string payloadRoot)
    {
        if (!Directory.Exists(payloadRoot))
        {
            return;
        }
        var item = new DirectoryInfo(payloadRoot);
        if (item.LinkTarget is not null
            || (item.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The Sunder Host payload root is not safe to remove.");
        }
        var markerPath = Path.Combine(payloadRoot, RootMarkerFileName);
        try
        {
            var marker = JsonSerializer.Deserialize<UserHostPayloadRootMarker>(
                File.ReadAllText(markerPath),
                JsonOptions);
            if (marker is not { Version: DescriptorVersion, Identity: RootMarkerIdentity })
            {
                throw new InvalidDataException("The Sunder Host payload root identity is invalid.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                "The Sunder Host payload root does not contain a valid identity marker.",
                exception);
        }
        EnsureSafePayloadDirectory(payloadRoot);
        Directory.Delete(payloadRoot, recursive: true);
    }

    public static void StopAndRemoveForUninstall()
    {
        var cleanupStarted = Stopwatch.GetTimestamp();
        var cleanupBudget = TimeSpan.FromSeconds(22);
        var payloadRoot = GetDefaultPayloadRoot();
        using var installationLock = TryAcquireInstallationLock(
            payloadRoot,
            Min(TimeSpan.FromSeconds(2), GetRemaining(cleanupStarted, cleanupBudget)))
            ?? throw new InvalidOperationException(
                "Another Sunder process is updating the current-user Host payload during uninstall.");
        var connection = HostConnectionInfoStore.Load();
        if (connection is not null)
        {
            try
            {
                var remaining = GetRemaining(cleanupStarted, cleanupBudget);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("Sunder Host uninstall cleanup timed out before shutdown.");
                }
                using var deadline = new CancellationTokenSource(Min(TimeSpan.FromSeconds(5), remaining));
                using var client = new HostManagementClient(() => connection);
                client.ShutdownHostForUninstallAsync(deadline.Token).GetAwaiter().GetResult();
                while (File.Exists(HostConnectionInfoStore.GetDefaultPath())
                       && GetRemaining(cleanupStarted, cleanupBudget) > TimeSpan.Zero)
                {
                    Thread.Sleep(100);
                }
            }
            catch when (GetRemaining(cleanupStarted, cleanupBudget) > TimeSpan.Zero
                        && !CanConnect(
                            connection.RuntimeUrl,
                            Min(TimeSpan.FromSeconds(1), GetRemaining(cleanupStarted, cleanupBudget))))
            {
                RuntimeConnectionInfoStore.DeleteIfMatches(
                    connection,
                    HostConnectionInfoStore.GetDefaultPath());
            }

            if (File.Exists(HostConnectionInfoStore.GetDefaultPath()))
            {
                var remaining = GetRemaining(cleanupStarted, cleanupBudget);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("Sunder Host uninstall cleanup timed out while waiting for shutdown.");
                }
                if (CanConnect(connection.RuntimeUrl, Min(TimeSpan.FromSeconds(1), remaining)))
                {
                    throw new InvalidOperationException(
                        "The current-user Sunder Host did not stop during uninstall.");
                }
                RuntimeConnectionInfoStore.DeleteIfMatches(
                    connection,
                    HostConnectionInfoStore.GetDefaultPath());
            }
        }

        while (!HostConnectionInfoStore.IsLifecycleLockAvailable()
               && GetRemaining(cleanupStarted, cleanupBudget) > TimeSpan.Zero)
        {
            Thread.Sleep(100);
        }
        if (!HostConnectionInfoStore.IsLifecycleLockAvailable())
        {
            throw new InvalidOperationException(
                "The current-user Sunder Host did not release its lifecycle state during uninstall.");
        }
        if (GetRemaining(cleanupStarted, cleanupBudget) <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "Sunder Host shutdown did not leave enough time for safe payload removal during uninstall.");
        }

        RemoveStagedPayloadsCore(payloadRoot);
    }

    private static bool CanConnect(Uri runtimeUrl, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }
        try
        {
            using var client = new TcpClient();
            using var deadline = new CancellationTokenSource(timeout);
            client.ConnectAsync(runtimeUrl.Host, runtimeUrl.Port, deadline.Token)
                .GetAwaiter()
                .GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private UserHostPayloadDescriptor? LoadCurrentPayloadDescriptor()
    {
        var descriptorPath = GetDescriptorPath();
        if (!File.Exists(descriptorPath))
        {
            return null;
        }

        try
        {
            var descriptor = JsonSerializer.Deserialize<UserHostPayloadDescriptor>(
                File.ReadAllText(descriptorPath),
                JsonOptions);
            if (descriptor is not { Version: DescriptorVersion }
                || string.IsNullOrWhiteSpace(descriptor.PayloadVersion)
                || string.IsNullOrWhiteSpace(descriptor.PayloadPath))
            {
                return null;
            }

            var directoryPath = Path.GetFullPath(descriptor.PayloadPath);
            EnsurePathIsDirectChild(directoryPath);
            return descriptor with { PayloadPath = directoryPath };
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or InvalidDataException)
        {
            AppSessionLog.WriteError("Ignored an invalid current-user Host payload descriptor.", exception);
            return null;
        }
    }

    private UserHostPayload? LoadCurrentPayload(
        UserHostPayloadDescriptor? descriptor,
        UserHostPayloadActivationLease activationLease)
    {
        if (descriptor is null
            || !IsCompletePayload(descriptor.PayloadPath, descriptor.PayloadVersion))
        {
            return null;
        }
        var executablePath = ResolveSupervisorExecutable(descriptor.PayloadPath);
        return executablePath is null
            ? null
            : new UserHostPayload(
                descriptor.PayloadVersion,
                descriptor.PayloadPath,
                executablePath,
                Previous: null,
                ReplacesCurrent: false,
                activationLease);
    }

    private void StagePayload(string destination)
    {
        var staging = Path.Combine(_payloadRoot, $".stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        SetPrivateDirectoryMode(staging);
        try
        {
            CopyDirectory(_sourceDirectory, staging);
            if (ResolveSupervisorExecutable(staging) is null)
            {
                throw new InvalidDataException("The bundled Sunder Host payload does not contain the Supervisor executable.");
            }

            var contentSha256 = ComputePayloadContentSha256(staging);
            WriteJsonAtomically(
                Path.Combine(staging, MarkerFileName),
                new UserHostPayloadMarker(DescriptorVersion, _payloadVersion, contentSha256));
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        EnsureNotLink(sourceInfo);
        foreach (var entry in sourceInfo.EnumerateFileSystemInfos())
        {
            EnsureNotLink(entry);
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                Directory.CreateDirectory(target);
                SetPrivateDirectoryMode(target);
                CopyDirectory(directory.FullName, target);
                continue;
            }

            File.Copy(entry.FullName, target, overwrite: false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(entry.FullName));
            }
        }
    }

    private bool IsCompletePayload(string directory, string expectedVersion)
    {
        try
        {
            EnsurePathIsDirectChild(directory);
            EnsureSafePayloadDirectory(directory);
            var markerPath = Path.Combine(directory, MarkerFileName);
            var marker = JsonSerializer.Deserialize<UserHostPayloadMarker>(
                File.ReadAllText(markerPath),
                JsonOptions);
            return marker is { Version: DescriptorVersion }
                   && string.Equals(marker.PayloadVersion, expectedVersion, StringComparison.Ordinal)
                   && !string.IsNullOrWhiteSpace(marker.ContentSha256)
                   && string.Equals(
                       marker.ContentSha256,
                       ComputePayloadContentSha256(directory),
                       StringComparison.OrdinalIgnoreCase)
                   && ResolveSupervisorExecutable(directory) is not null;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or InvalidDataException)
        {
            return false;
        }
    }

    internal static string GetInstallationLockPath(string payloadRoot)
    {
        var root = new DirectoryInfo(Path.GetFullPath(payloadRoot));
        var parent = root.Parent
            ?? throw new InvalidOperationException("The Sunder Host payload root must have a parent directory.");
        return Path.Combine(parent.FullName, $".{root.Name}.install.lock");
    }

    private FileStream AcquireInstallationLock()
    {
        return TryAcquireInstallationLock(_payloadRoot, TimeSpan.Zero)
               ?? throw new InvalidOperationException(
                   "Another Sunder process is updating the current-user Host payload.");
    }

    private static FileStream? TryAcquireInstallationLock(string payloadRoot, TimeSpan wait)
    {
        var lockPath = GetInstallationLockPath(payloadRoot);
        var lockDirectory = Path.GetDirectoryName(lockPath)!;
        Directory.CreateDirectory(lockDirectory);
        SetPrivateDirectoryMode(lockDirectory);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.WriteThrough,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                var stream = new FileStream(lockPath, options);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                return stream;
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < wait)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private static TimeSpan GetRemaining(long started, TimeSpan budget)
    {
        var remaining = budget - Stopwatch.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private void EnsurePayloadRootMarker()
    {
        var markerPath = Path.Combine(_payloadRoot, RootMarkerFileName);
        if (File.Exists(markerPath))
        {
            try
            {
                var marker = JsonSerializer.Deserialize<UserHostPayloadRootMarker>(
                    File.ReadAllText(markerPath),
                    JsonOptions);
                if (marker is { Version: DescriptorVersion, Identity: RootMarkerIdentity })
                {
                    return;
                }
            }
            catch (JsonException)
            {
            }
            throw new InvalidDataException("The Sunder Host payload root identity is invalid.");
        }

        var unexpectedEntry = Directory.EnumerateFileSystemEntries(_payloadRoot)
            .FirstOrDefault(path => !string.Equals(
                Path.GetFileName(path),
                ".install.lock",
                StringComparison.Ordinal));
        if (unexpectedEntry is not null)
        {
            throw new InvalidDataException(
                "The configured Sunder Host payload root is non-empty and has no Sunder identity marker.");
        }
        WriteJsonAtomically(
            markerPath,
            new UserHostPayloadRootMarker(DescriptorVersion, RootMarkerIdentity));
    }

    private void EnsurePathIsDirectChild(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Directory.GetParent(fullPath)?.FullName;
        if (parent is null || !PathEquals(parent, _payloadRoot))
        {
            throw new InvalidDataException("The Sunder Host payload path is outside its managed root.");
        }
    }

    private static void EnsureSafePayloadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }
        EnsureNotLink(new DirectoryInfo(directory));
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            EnsureNotLink(entry);
        }
    }

    private static void EnsureNotLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null
            || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The Sunder Host payload contains an unsupported link at '{entry.FullName}'.");
        }
    }

    private static string? ResolveSupervisorExecutable(string directory)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "Sunder.Host.Supervisor.exe", "Sunder.Host.Supervisor.dll" }
            : new[] { "Sunder.Host.Supervisor", "Sunder.Host.Supervisor.dll" };
        return names.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
    }

    private static string ComputePayloadContentSha256(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var markerPath = Path.Combine(directory, MarkerFileName);
        var buffer = new byte[81920];
        foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => !PathEquals(path, markerPath))
                     .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(directory, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            AppendHashText(hash, relativePath);
            AppendHashText(
                hash,
                OperatingSystem.IsWindows()
                    ? "windows"
                    : ((int)File.GetUnixFileMode(filePath)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendHashText(
                hash,
                new FileInfo(filePath).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHashText(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private string GetDescriptorPath() => Path.Combine(_payloadRoot, "current.json");

    private static string CreatePayloadDirectoryName(string version)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(version));
        return $"host-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The JSON destination has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temporaryPath, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record UserHostPayloadMarker(int Version, string PayloadVersion, string ContentSha256);

    private sealed record UserHostPayloadRootMarker(int Version, string Identity);

    private sealed record UserHostPayloadDescriptor(int Version, string PayloadVersion, string PayloadPath);
}

internal sealed record UserHostPayload(
    string Version,
    string DirectoryPath,
    string ExecutablePath,
    UserHostPayload? Previous,
    bool ReplacesCurrent,
    UserHostPayloadActivationLease ActivationLease) : IDisposable
{
    public void Dispose() => ActivationLease.Dispose();
}

internal sealed class UserHostPayloadActivationLease(string installationLockPath, FileStream stream) : IDisposable
{
    private readonly string _installationLockPath = Path.GetFullPath(installationLockPath);
    private FileStream? _stream = stream;

    public bool IsActive => Volatile.Read(ref _stream) is not null;

    public void EnsureActiveFor(string installationLockPath)
    {
        EnsureFor(installationLockPath);
        if (!IsActive)
        {
            throw new InvalidOperationException(
                "The Sunder Host payload activation transaction is no longer active.");
        }
    }

    public void EnsureFor(string installationLockPath)
    {
        if (!string.Equals(
                _installationLockPath,
                Path.GetFullPath(installationLockPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Sunder Host payload belongs to a different activation transaction root.");
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
