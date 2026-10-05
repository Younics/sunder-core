using System.Text;

namespace Sunder.Package.Format;

public interface ISunderExecutionRuntimeArchiveSigner
{
    string KeyId { get; }

    byte[] Sign(ReadOnlySpan<byte> signatureInput);
}

public interface ISunderExecutionRuntimeArchiveSignatureVerifier
{
    bool Verify(
        ReadOnlySpan<byte> signatureInput,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> trustedPublicKey);
}

public sealed record SunderExecutionRuntimeTrustedKey(
    string KeyId,
    ReadOnlyMemory<byte> PublicKey);

internal static class SunderExecutionRuntimeSignatureInput
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes(SunderExecutionRuntimeFormat.ReleaseSignatureDomain);

    public static byte[] Create(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> contentIndexBytes)
    {
        var result = new byte[checked(Domain.Length + manifestBytes.Length + contentIndexBytes.Length)];
        var offset = 0;
        Domain.CopyTo(result, offset);
        offset += Domain.Length;
        manifestBytes.CopyTo(result.AsSpan(offset));
        offset += manifestBytes.Length;
        contentIndexBytes.CopyTo(result.AsSpan(offset));
        return result;
    }
}

public static class SunderExecutionRuntimeNativeAttestationSignatures
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes(
        SunderExecutionRuntimeFormat.NativeAttestationSignatureDomain);

    public static byte[] CreateSignatureInput(SunderExecutionRuntimeNativeAttestation attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        var statement = SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestationStatement(attestation);
        var result = new byte[checked(Domain.Length + statement.Length)];
        Domain.CopyTo(result, 0);
        statement.CopyTo(result, Domain.Length);
        return result;
    }
}
