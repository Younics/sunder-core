using System.Security.Cryptography;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed record SecretCiphertext(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

internal interface ISecretCipher
{
    SecretCiphertext Encrypt(byte[] key, byte[] plaintext, byte[] associatedData);

    byte[] Decrypt(
        byte[] key,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag,
        byte[] associatedData);
}

internal sealed class AesGcmSecretCipher : ISecretCipher
{
    internal const int NonceSize = 12;
    internal const int TagSize = 16;

    public SecretCiphertext Encrypt(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return new SecretCiphertext(nonce, ciphertext, tag);
    }

    public byte[] Decrypt(
        byte[] key,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag,
        byte[] associatedData)
    {
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }
}
