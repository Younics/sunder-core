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

internal interface IOsMasterKeyProtector
{
    string Scheme { get; }

    bool IsAvailable { get; }

    byte[] Protect(byte[] key);

    byte[] Unprotect(byte[] payload);

    bool TryDelete(byte[] payload);
}

internal sealed class PlatformMasterKeyProtection : IMasterKeyProtection
{
    private readonly IOsMasterKeyProtector? _preferredProtector;
    private readonly IReadOnlyDictionary<string, IOsMasterKeyProtector> _protectors;

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
        var protectors = new IOsMasterKeyProtector[]
        {
            new WindowsDpapiMasterKeyProtector(
                platform == StoragePlatform.Windows,
                windowsDataProtection ?? new WindowsDataProtection()),
            new MacOsKeychainMasterKeyProtector(platform == StoragePlatform.MacOs, keychain),
            new LinuxSecretServiceMasterKeyProtector(platform == StoragePlatform.Linux, secretService),
        };
        _protectors = protectors.ToDictionary(protector => protector.Scheme, StringComparer.Ordinal);
        _preferredProtector = platform switch
        {
            StoragePlatform.Windows => _protectors[MasterKeyProtectionSchemes.Dpapi],
            StoragePlatform.MacOs => _protectors[MasterKeyProtectionSchemes.MacOsKeychain],
            StoragePlatform.Linux => _protectors[MasterKeyProtectionSchemes.LinuxSecretService],
            _ => null,
        };
    }

    public ProtectedMasterKey Protect(byte[] masterKey)
    {
        if (IsAvailable(_preferredProtector))
        {
            try
            {
                return new ProtectedMasterKey(
                    _preferredProtector!.Scheme,
                    MasterKeyProtectionSchemes.CurrentVersion,
                    _preferredProtector.Protect(masterKey));
            }
            catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
            {
                // New keys remain recoverable when an optional OS provider is unavailable.
            }
        }

        return new ProtectedMasterKey(
            MasterKeyProtectionSchemes.RestrictedFile,
            MasterKeyProtectionSchemes.CurrentVersion,
            masterKey.ToArray());
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

        if (!_protectors.TryGetValue(scheme, out var protector))
        {
            throw new PackageStorageNotSupportedException($"Master key protection scheme '{scheme}' is not supported.");
        }

        if (!IsAvailable(protector))
        {
            throw new PackageStorageKeyUnavailableException(
                $"The '{scheme}' master-key provider is unavailable; key metadata and ciphertext were preserved.");
        }

        try
        {
            return protector.Unprotect(payload);
        }
        catch (PackageStorageKeyUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
        {
            throw new PackageStorageKeyUnavailableException(
                $"The '{scheme}' provider could not open the package secrets master key.",
                exception);
        }
    }

    public bool ShouldReprotect(string scheme) =>
        string.Equals(scheme, MasterKeyProtectionSchemes.RestrictedFile, StringComparison.Ordinal)
        && IsAvailable(_preferredProtector);

    public bool TryDelete(string scheme, int version, byte[] payload)
    {
        if (version != MasterKeyProtectionSchemes.CurrentVersion
            || !_protectors.TryGetValue(scheme, out var protector))
        {
            return false;
        }

        try
        {
            return protector.TryDelete(payload);
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
        {
            return false;
        }
    }

    private static bool IsAvailable(IOsMasterKeyProtector? protector)
    {
        if (protector is null)
        {
            return false;
        }

        try
        {
            return protector.IsAvailable;
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsProviderFailure(exception))
        {
            return false;
        }
    }

    private static StoragePlatform GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return StoragePlatform.Windows;
        if (OperatingSystem.IsMacOS()) return StoragePlatform.MacOs;
        if (OperatingSystem.IsLinux()) return StoragePlatform.Linux;
        return StoragePlatform.Other;
    }
}

internal abstract class ExternalStoreMasterKeyProtector(
    bool isCurrentPlatform,
    IExternalMasterKeyStore? store) : IOsMasterKeyProtector
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public abstract string Scheme { get; }

    public bool IsAvailable => isCurrentPlatform && store?.IsAvailable == true;

    public byte[] Protect(byte[] key)
    {
        var identifier = Guid.NewGuid().ToString("N");
        if (store is null || !store.TryStore(identifier, key))
        {
            throw new PackageStorageKeyUnavailableException($"The '{Scheme}' provider could not store a master key.");
        }

        return StrictUtf8.GetBytes(identifier);
    }

    public byte[] Unprotect(byte[] payload) => store!.Load(DecodeIdentifier(payload));

    public bool TryDelete(byte[] payload) => store is not null && store.TryDelete(DecodeIdentifier(payload));

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
}

internal sealed class MacOsKeychainMasterKeyProtector(bool isCurrentPlatform, IExternalMasterKeyStore? store)
    : ExternalStoreMasterKeyProtector(isCurrentPlatform, store)
{
    public override string Scheme => MasterKeyProtectionSchemes.MacOsKeychain;
}

internal sealed class LinuxSecretServiceMasterKeyProtector(bool isCurrentPlatform, IExternalMasterKeyStore? store)
    : ExternalStoreMasterKeyProtector(isCurrentPlatform, store)
{
    public override string Scheme => MasterKeyProtectionSchemes.LinuxSecretService;
}
