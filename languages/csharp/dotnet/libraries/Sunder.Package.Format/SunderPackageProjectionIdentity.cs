using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sunder.Package.Format;

internal static class SunderPackageProjectionIdentity
{
    private static readonly byte[] Domain = "sunder-registry-projection-v1"u8.ToArray();

    public static string Compute(
        int projectionFormatVersion,
        string packageId,
        string packageVersion,
        string sourceArchiveSha256,
        string kind,
        string? rid,
        string manifestSha256,
        ReadOnlySpan<byte> contentIndexBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Domain);
        Append(hash, projectionFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, packageId);
        Append(hash, packageVersion);
        Append(hash, sourceArchiveSha256);
        Append(hash, kind);
        AppendNullable(hash, rid);
        Append(hash, manifestSha256);
        Append(hash, contentIndexBytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
        => Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static void AppendNullable(IncrementalHash hash, string? value)
    {
        if (value is not null)
        {
            Append(hash, value);
            return;
        }

        Span<byte> marker = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(marker, -1);
        hash.AppendData(marker);
    }
}
