using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed class WindowsDpapiMasterKeyProtector(
    bool isCurrentPlatform,
    IWindowsDataProtection dataProtection) : IOsMasterKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "Sunder.Runtime.V1.PackageSecrets.MasterKey");

    public string Scheme => MasterKeyProtectionSchemes.Dpapi;

    public bool IsAvailable => isCurrentPlatform;

    public byte[] Protect(byte[] key) => dataProtection.Protect(key, Entropy);

    public byte[] Unprotect(byte[] payload) => dataProtection.Unprotect(payload, Entropy);

    public bool TryDelete(byte[] payload) => false;
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
