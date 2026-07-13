using Sunder.Runtime.Client;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed class LinuxSecretServiceMasterKeyStore : IExternalMasterKeyStore
{
    private const string Attribute = RuntimeV1StateDescriptor.LinuxMasterKeyAttribute;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    private readonly string? _secretToolPath;
    private readonly ICredentialCommandRunner _commandRunner;
    private readonly Func<string?> _sessionBusAddress;
    private readonly bool _isLinux;

    internal LinuxSecretServiceMasterKeyStore()
        : this(
            CommandRunner.FindExecutable("secret-tool"),
            new BoundedCredentialProcessAdapter(),
            () => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"),
            OperatingSystem.IsLinux())
    {
    }

    internal LinuxSecretServiceMasterKeyStore(string? secretToolPath)
        : this(
            secretToolPath,
            new BoundedCredentialProcessAdapter(),
            () => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"),
            OperatingSystem.IsLinux())
    {
    }

    internal LinuxSecretServiceMasterKeyStore(
        string? secretToolPath,
        ICredentialCommandRunner commandRunner,
        Func<string?> sessionBusAddress,
        bool isLinux)
    {
        _secretToolPath = secretToolPath;
        _commandRunner = commandRunner;
        _sessionBusAddress = sessionBusAddress;
        _isLinux = isLinux;
    }

    public string Scheme => MasterKeyProtectionSchemes.LinuxSecretService;

    public bool IsAvailable
    {
        get
        {
            try
            {
                return _isLinux
                    && _secretToolPath is not null
                    && !string.IsNullOrWhiteSpace(_sessionBusAddress());
            }
            catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
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
                return Run(
                    ["store", "--label=Sunder Runtime V1 package storage master key", Attribute, identifier],
                    encodedKey + Environment.NewLine).ExitCode == 0;
            }
            catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
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
            result = Run(["lookup", Attribute, identifier], null);
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
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
            return Run(["clear", Attribute, identifier], null).ExitCode == 0;
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
        {
            return false;
        }
    }

    private CommandResult Run(IReadOnlyList<string> arguments, string? standardInput) =>
        _commandRunner.Run(_secretToolPath!, arguments, standardInput, CommandTimeout);
}
