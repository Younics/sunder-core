using Sunder.Runtime.Client;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed class MacOsKeychainMasterKeyStore : IExternalMasterKeyStore
{
    private const string SecurityPath = "/usr/bin/security";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    internal const string Service = RuntimeV1StateDescriptor.MacOsMasterKeyService;

    private readonly ICredentialCommandRunner _commandRunner;
    private readonly string _securityPath;
    private readonly Func<string, bool> _fileExists;
    private readonly bool _isMacOs;

    internal MacOsKeychainMasterKeyStore()
        : this(new BoundedCredentialProcessAdapter(), SecurityPath, File.Exists, OperatingSystem.IsMacOS())
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
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
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
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
        {
            return false;
        }
    }

    private CommandResult Run(IReadOnlyList<string> arguments) =>
        _commandRunner.Run(_securityPath, arguments, standardInput: null, CommandTimeout);
}
