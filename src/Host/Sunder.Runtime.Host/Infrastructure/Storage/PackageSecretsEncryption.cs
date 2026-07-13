using System.Security.Cryptography;
using System.Text;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed class PackageSecretsEncryption(
    MasterKeyStore masterKeyStore,
    ISecretCipher cipher,
    PackageSecretsSerializer serializer)
{
    internal byte[] Decrypt(EncryptedPackageSecrets encrypted)
    {
        var keyMaterial = masterKeyStore.GetExisting();
        byte[]? plaintext = null;
        try
        {
            plaintext = cipher.Decrypt(
                keyMaterial.Key,
                encrypted.Nonce,
                encrypted.Ciphertext,
                encrypted.Tag,
                CreateAssociatedData(
                    PackageSecretsSerializer.EncryptedFormat,
                    encrypted.KeyScheme,
                    encrypted.KeyVersion));
            if (keyMaterial.NeedsReprotection)
            {
                masterKeyStore.Reprotect(keyMaterial.Key);
            }

            return plaintext;
        }
        catch
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial.Key);
        }
    }

    internal EncryptedPackageSecrets Encrypt(byte[] plaintext)
    {
        var keyMaterial = GetKeyForWrite();
        try
        {
            var encrypted = cipher.Encrypt(
                keyMaterial.Key,
                plaintext,
                CreateAssociatedData(
                    PackageSecretsSerializer.EncryptedFormat,
                    keyMaterial.ProtectionScheme,
                    keyMaterial.ProtectionVersion));
            return new EncryptedPackageSecrets(
                keyMaterial.ProtectionScheme,
                keyMaterial.ProtectionVersion,
                encrypted.Nonce,
                encrypted.Ciphertext,
                encrypted.Tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial.Key);
        }
    }

    internal bool TryEncryptQuarantine(byte[] contents, out byte[] encryptedQuarantine)
    {
        encryptedQuarantine = null!;
        MasterKeyMaterial? keyMaterial = null;
        SecretCiphertext? encrypted = null;
        try
        {
            keyMaterial = GetKeyForWrite();
            encrypted = cipher.Encrypt(
                keyMaterial.Key,
                contents,
                CreateAssociatedData(
                    PackageSecretsSerializer.QuarantineFormat,
                    keyMaterial.ProtectionScheme,
                    keyMaterial.ProtectionVersion));
            encryptedQuarantine = serializer.SerializeQuarantine(new EncryptedPackageSecrets(
                keyMaterial.ProtectionScheme,
                keyMaterial.ProtectionVersion,
                encrypted.Nonce,
                encrypted.Ciphertext,
                encrypted.Tag));
            return true;
        }
        catch (Exception exception) when (PackageStorageExceptionClassifier.IsQuarantineEncryptionFailure(exception))
        {
            return false;
        }
        finally
        {
            if (keyMaterial is not null)
            {
                CryptographicOperations.ZeroMemory(keyMaterial.Key);
            }

            if (encrypted is not null)
            {
                Zero(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag);
            }
        }
    }

    internal static void Zero(EncryptedPackageSecrets encrypted) =>
        Zero(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag);

    private MasterKeyMaterial GetKeyForWrite()
    {
        var keyMaterial = masterKeyStore.GetOrCreate();
        if (!keyMaterial.NeedsReprotection)
        {
            return keyMaterial;
        }

        try
        {
            masterKeyStore.Reprotect(keyMaterial.Key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial.Key);
        }

        return masterKeyStore.GetExisting();
    }

    private static byte[] CreateAssociatedData(string format, string keyScheme, int keyVersion) =>
        Encoding.UTF8.GetBytes(
            $"{format}|document={PackageSecretsSerializer.DocumentVersion}"
            + $"|cipher={PackageSecretsSerializer.CipherScheme}:{PackageSecretsSerializer.CipherVersion}"
            + $"|key={keyScheme}:{keyVersion}");

    private static void Zero(params byte[][] values)
    {
        foreach (var value in values)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
