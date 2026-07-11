using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal static class MasterKeyProtectionSchemes
{
    internal const string Dpapi = "dpapi-current-user";
    internal const string MacOsKeychain = "macos-keychain";
    internal const string LinuxSecretService = "linux-secret-service";
    internal const string RestrictedFile = "restricted-user-file";
    internal const int CurrentVersion = 1;

    internal static bool IsKnown(string scheme) => scheme is
        Dpapi or MacOsKeychain or LinuxSecretService or RestrictedFile;
}

internal interface IMasterKeyProtection
{
    ProtectedMasterKey Protect(byte[] masterKey);

    byte[] Unprotect(string scheme, int version, byte[] payload);

    bool ShouldReprotect(string scheme);

    bool TryDelete(string scheme, int version, byte[] payload);
}

internal sealed class RestrictedFileMasterKeyProtection : IMasterKeyProtection
{
    public ProtectedMasterKey Protect(byte[] masterKey) => new(
        MasterKeyProtectionSchemes.RestrictedFile,
        MasterKeyProtectionSchemes.CurrentVersion,
        masterKey.ToArray());

    public byte[] Unprotect(string scheme, int version, byte[] payload)
    {
        if (!string.Equals(scheme, MasterKeyProtectionSchemes.RestrictedFile, StringComparison.Ordinal)
            || version != MasterKeyProtectionSchemes.CurrentVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"Restricted-file protection cannot open scheme '{scheme}' version {version}.");
        }

        return payload.ToArray();
    }

    public bool ShouldReprotect(string scheme) => false;

    public bool TryDelete(string scheme, int version, byte[] payload) => false;
}

internal enum StoragePlatform
{
    Windows,
    MacOs,
    Linux,
    Other,
}

internal interface IExternalMasterKeyStore
{
    string Scheme { get; }

    bool IsAvailable { get; }

    bool TryStore(string identifier, byte[] key);

    byte[] Load(string identifier);

    bool TryDelete(string identifier);
}

internal interface IWindowsDataProtection
{
    byte[] Protect(byte[] key, byte[] entropy);

    byte[] Unprotect(byte[] protectedKey, byte[] entropy);
}

internal sealed class PlatformMasterKeyProtection : IMasterKeyProtection
{
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("Sunder.Runtime.V1.PackageSecrets.MasterKey");
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly StoragePlatform _platform;
    private readonly IExternalMasterKeyStore? _keychain;
    private readonly IExternalMasterKeyStore? _secretService;
    private readonly IWindowsDataProtection _windowsDataProtection;

    internal PlatformMasterKeyProtection()
        : this(
            GetCurrentPlatform(),
            new MacOsKeychainMasterKeyStore(),
            new LinuxSecretServiceMasterKeyStore(),
            new WindowsDataProtection())
    {
    }

    internal PlatformMasterKeyProtection(
        StoragePlatform platform,
        IExternalMasterKeyStore? keychain,
        IExternalMasterKeyStore? secretService,
        IWindowsDataProtection? windowsDataProtection)
    {
        _platform = platform;
        _keychain = keychain;
        _secretService = secretService;
        _windowsDataProtection = windowsDataProtection ?? new WindowsDataProtection();
    }

    public ProtectedMasterKey Protect(byte[] masterKey)
    {
        if (_platform == StoragePlatform.Windows)
        {
            try
            {
                return new ProtectedMasterKey(
                    MasterKeyProtectionSchemes.Dpapi,
                    MasterKeyProtectionSchemes.CurrentVersion,
                    _windowsDataProtection.Protect(masterKey, DpapiEntropy));
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            {
                return CreateRestricted(masterKey);
            }
        }

        var externalStore = GetPreferredExternalStore();
        if (IsAvailable(externalStore))
        {
            var identifier = Guid.NewGuid().ToString("N");
            try
            {
                if (externalStore!.TryStore(identifier, masterKey))
                {
                    return new ProtectedMasterKey(
                        externalStore.Scheme,
                        MasterKeyProtectionSchemes.CurrentVersion,
                        StrictUtf8.GetBytes(identifier));
                }
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            {
                // Optional providers must not prevent creation of a recoverable restricted key.
            }
        }

        return CreateRestricted(masterKey);
    }

    public byte[] Unprotect(string scheme, int version, byte[] payload)
    {
        if (version != MasterKeyProtectionSchemes.CurrentVersion)
        {
            throw new PackageStorageNotSupportedException(
                $"Master key protection version {version} is not supported.");
        }

        if (string.Equals(scheme, MasterKeyProtectionSchemes.RestrictedFile, StringComparison.Ordinal))
        {
            return payload.ToArray();
        }

        if (string.Equals(scheme, MasterKeyProtectionSchemes.Dpapi, StringComparison.Ordinal))
        {
            if (_platform != StoragePlatform.Windows)
            {
                throw new PackageStorageKeyUnavailableException(
                    "A DPAPI-protected package master key is only available to its Windows user.");
            }

            try
            {
                return _windowsDataProtection.Unprotect(payload, DpapiEntropy);
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            {
                throw new PackageStorageKeyUnavailableException(
                    "Windows DPAPI could not open the package secrets master key; ciphertext was preserved.",
                    exception);
            }
        }

        var identifier = DecodeIdentifier(payload);
        var externalStore = GetStoreForScheme(scheme);
        if (!IsAvailable(externalStore))
        {
            throw new PackageStorageKeyUnavailableException(
                $"The '{scheme}' master-key provider is unavailable; key metadata and ciphertext were preserved.");
        }

        try
        {
            return externalStore!.Load(identifier);
        }
        catch (PackageStorageKeyUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PackageStorageKeyUnavailableException(
                $"The '{scheme}' provider could not open the package secrets master key.",
                exception);
        }
    }

    public bool ShouldReprotect(string scheme)
    {
        if (!string.Equals(scheme, MasterKeyProtectionSchemes.RestrictedFile, StringComparison.Ordinal))
        {
            return false;
        }

        return _platform == StoragePlatform.Windows || IsAvailable(GetPreferredExternalStore());
    }

    public bool TryDelete(string scheme, int version, byte[] payload)
    {
        if (version != MasterKeyProtectionSchemes.CurrentVersion
            || string.Equals(scheme, MasterKeyProtectionSchemes.RestrictedFile, StringComparison.Ordinal)
            || string.Equals(scheme, MasterKeyProtectionSchemes.Dpapi, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var store = GetStoreForScheme(scheme);
            return store is not null && store.TryDelete(DecodeIdentifier(payload));
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return false;
        }
    }

    private IExternalMasterKeyStore? GetPreferredExternalStore() => _platform switch
    {
        StoragePlatform.MacOs => _keychain,
        StoragePlatform.Linux => _secretService,
        _ => null,
    };

    private IExternalMasterKeyStore? GetStoreForScheme(string scheme)
    {
        if (string.Equals(scheme, MasterKeyProtectionSchemes.MacOsKeychain, StringComparison.Ordinal))
        {
            return _keychain;
        }

        if (string.Equals(scheme, MasterKeyProtectionSchemes.LinuxSecretService, StringComparison.Ordinal))
        {
            return _secretService;
        }

        throw new PackageStorageNotSupportedException($"Master key protection scheme '{scheme}' is not supported.");
    }

    private static StoragePlatform GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return StoragePlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return StoragePlatform.MacOs;
        }

        if (OperatingSystem.IsLinux())
        {
            return StoragePlatform.Linux;
        }

        return StoragePlatform.Other;
    }

    private static ProtectedMasterKey CreateRestricted(byte[] masterKey) => new(
        MasterKeyProtectionSchemes.RestrictedFile,
        MasterKeyProtectionSchemes.CurrentVersion,
        masterKey.ToArray());

    private static string DecodeIdentifier(byte[] payload)
    {
        string identifier;
        try
        {
            identifier = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PackageStorageKeyUnavailableException(
                "The external master-key reference is not valid UTF-8.",
                exception);
        }

        if (identifier.Length != 32 || !Guid.TryParseExact(identifier, "N", out _))
        {
            throw new PackageStorageKeyUnavailableException(
                "The external master-key reference is not a valid GUID.");
        }

        return identifier;
    }

    private static bool IsAvailable(IExternalMasterKeyStore? store)
    {
        if (store is null)
        {
            return false;
        }

        try
        {
            return store.IsAvailable;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return false;
        }
    }

    private static bool IsProviderFailure(Exception exception) => exception is not (
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);
}

internal sealed class WindowsDataProtection : IWindowsDataProtection
{
    [SupportedOSPlatform("windows")]
    public byte[] Protect(byte[] key, byte[] entropy) =>
        ProtectedData.Protect(key, entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    public byte[] Unprotect(byte[] protectedKey, byte[] entropy) =>
        ProtectedData.Unprotect(protectedKey, entropy, DataProtectionScope.CurrentUser);
}

internal sealed class MacOsKeychainMasterKeyStore : IExternalMasterKeyStore
{
    private const string SecurityPath = "/usr/bin/security";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    internal const string Service = "io.sunder.runtime.v1.package-storage.master-key";

    private readonly ICredentialCommandRunner _commandRunner;
    private readonly string _securityPath;
    private readonly Func<string, bool> _fileExists;
    private readonly bool _isMacOs;

    internal MacOsKeychainMasterKeyStore()
        : this(new CredentialCommandRunner(), SecurityPath, File.Exists, OperatingSystem.IsMacOS())
    {
    }

    internal MacOsKeychainMasterKeyStore(
        ICredentialCommandRunner commandRunner,
        string securityPath,
        Func<string, bool> fileExists,
        bool isMacOs)
    {
        _commandRunner = commandRunner;
        _securityPath = securityPath;
        _fileExists = fileExists;
        _isMacOs = isMacOs;
    }

    public string Scheme => MasterKeyProtectionSchemes.MacOsKeychain;

    public bool IsAvailable => _isMacOs
        && TryProviderOperation(() => _fileExists(_securityPath)
            && Run(["default-keychain", "-d", "user"]).ExitCode == 0);

    public bool TryStore(string identifier, byte[] key)
    {
        var encodedKey = Convert.ToBase64String(key);
        try
        {
            return TryProviderOperation(() => Run([
                "add-generic-password",
                "-a",
                identifier,
                "-s",
                Service,
                "-U",
                "-w",
                encodedKey,
            ]).ExitCode == 0);
        }
        finally
        {
            encodedKey = string.Empty;
        }
    }

    public byte[] Load(string identifier)
    {
        CommandResult result;
        try
        {
            result = Run([
                "find-generic-password",
                "-a",
                identifier,
                "-s",
                Service,
                "-w",
            ]);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PackageStorageKeyUnavailableException(
                "macOS Keychain could not perform the package master-key lookup.",
                exception);
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new PackageStorageKeyUnavailableException(
                $"macOS Keychain did not return package master key '{identifier}': "
                    + result.StandardError.Trim());
        }

        try
        {
            return Convert.FromBase64String(result.StandardOutput.Trim());
        }
        catch (FormatException exception)
        {
            throw new PackageStorageKeyUnavailableException(
                "macOS Keychain returned malformed package master-key material.",
                exception);
        }
    }

    public bool TryDelete(string identifier) => TryProviderOperation(() => Run([
        "delete-generic-password",
        "-a",
        identifier,
        "-s",
        Service,
    ]).ExitCode == 0);

    private static bool TryProviderOperation(Func<bool> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return false;
        }
    }

    private CommandResult Run(IReadOnlyList<string> arguments) =>
        _commandRunner.Run(_securityPath, arguments, standardInput: null, CommandTimeout);

    private static bool IsProviderFailure(Exception exception) => exception is not (
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);
}

internal interface ICredentialCommandRunner
{
    CommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout);
}

internal sealed class CredentialCommandRunner : ICredentialCommandRunner
{
    public CommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout) => CommandRunner.Run(fileName, arguments, standardInput, timeout);
}

internal sealed class LinuxSecretServiceMasterKeyStore : IExternalMasterKeyStore
{
    private readonly string? _secretToolPath;

    internal LinuxSecretServiceMasterKeyStore()
        : this(CommandRunner.FindExecutable("secret-tool"))
    {
    }

    internal LinuxSecretServiceMasterKeyStore(string? secretToolPath)
    {
        _secretToolPath = secretToolPath;
    }

    public string Scheme => MasterKeyProtectionSchemes.LinuxSecretService;

    public bool IsAvailable
    {
        get
        {
            try
            {
                return OperatingSystem.IsLinux()
                    && _secretToolPath is not null
                    && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            {
                return false;
            }
        }
    }

    public bool TryStore(string identifier, byte[] key)
    {
        if (_secretToolPath is null)
        {
            return false;
        }

        var encodedKey = Convert.ToBase64String(key);
        try
        {
            try
            {
                var result = CommandRunner.Run(
                    _secretToolPath,
                    ["store", "--label=Sunder Runtime V1 package storage master key", "sunder-runtime-v1-master-key", identifier],
                    encodedKey + Environment.NewLine);
                return result.ExitCode == 0;
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            {
                return false;
            }
        }
        finally
        {
            encodedKey = string.Empty;
        }
    }

    public byte[] Load(string identifier)
    {
        if (_secretToolPath is null)
        {
            throw new PackageStorageKeyUnavailableException("Linux Secret Service tooling is unavailable.");
        }

        CommandResult result;
        try
        {
            result = CommandRunner.Run(
                _secretToolPath,
                ["lookup", "sunder-runtime-v1-master-key", identifier],
                null);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            throw new PackageStorageKeyUnavailableException(
                "Linux Secret Service could not perform the package master-key lookup.",
                exception);
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new PackageStorageKeyUnavailableException(
                $"Linux Secret Service did not return package master key '{identifier}': {result.StandardError.Trim()}");
        }

        try
        {
            return Convert.FromBase64String(result.StandardOutput.Trim());
        }
        catch (FormatException exception)
        {
            throw new PackageStorageKeyUnavailableException(
                "Linux Secret Service returned malformed package master-key material.",
                exception);
        }
    }

    public bool TryDelete(string identifier)
    {
        if (_secretToolPath is null)
        {
            return false;
        }

        try
        {
            return CommandRunner.Run(
                _secretToolPath,
                ["clear", "sunder-runtime-v1-master-key", identifier],
                null).ExitCode == 0;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return false;
        }
    }

    private static bool IsProviderFailure(Exception exception) => exception is not (
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException);
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal static class CommandRunner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    internal static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                // Ignore malformed or inaccessible PATH entries.
            }
        }

        return null;
    }

    internal static CommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => AppendLine(standardOutput, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => AppendLine(standardError, eventArgs.Data);
        if (!process.Start())
        {
            throw new IOException($"Could not start credential provider command '{fileName}'.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            try
            {
                if (standardInput is not null)
                {
                    process.StandardInput.Write(standardInput);
                }
            }
            finally
            {
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(timeout ?? CommandTimeout))
            {
                StopAndDrain(process);
                throw new PackageStorageKeyUnavailableException(
                    $"Credential provider command '{fileName}' timed out.");
            }

            process.WaitForExit();
            return new CommandResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
        }
        catch
        {
            if (!process.HasExited)
            {
                StopAndDrain(process);
            }

            throw;
        }
    }

    private static void StopAndDrain(Process process)
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
            // The process exited between the status check and kill.
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            // Cleanup is best effort; the caller still receives the provider timeout or I/O failure.
        }

        try
        {
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                return;
            }

            process.WaitForExit();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // Preserve the primary command failure after attempting to wait and drain redirected streams.
        }
    }

    private static void AppendLine(StringBuilder target, string? line)
    {
        if (line is not null)
        {
            target.AppendLine(line);
        }
    }
}
