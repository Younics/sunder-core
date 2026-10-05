using System.Numerics;
using System.Security.Cryptography;

namespace Sunder.Package.Format;

// Verification operates only on public data. BigInteger's non-constant-time arithmetic
// therefore does not expose signing key material.
internal sealed class SunderExecutionRuntimeEd25519SignatureVerifier
    : ISunderExecutionRuntimeArchiveSignatureVerifier
{
    private static readonly BigInteger Prime = (BigInteger.One << 255) - 19;
    private static readonly BigInteger GroupOrder =
        (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger CurveD = Mod(
        -121665 * BigInteger.ModPow(121666, Prime - 2, Prime));
    private static readonly BigInteger SquareRootOfMinusOne = BigInteger.ModPow(
        2,
        (Prime - 1) / 4,
        Prime);
    private static readonly EdwardsPoint Identity = new(
        BigInteger.Zero,
        BigInteger.One,
        BigInteger.One,
        BigInteger.Zero);
    private static readonly EdwardsPoint BasePoint = FromAffine(
        BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202"),
        BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960"));

    public bool Verify(
        ReadOnlySpan<byte> signatureInput,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> trustedPublicKey)
    {
        if (signature.Length != SunderExecutionRuntimeFormat.Ed25519SignatureBytes
            || trustedPublicKey.Length != SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes)
        {
            return false;
        }

        var scalar = FromLittleEndian(signature[32..]);
        if (scalar >= GroupOrder
            || !TryDecodePoint(trustedPublicKey, out var publicPoint)
            || !TryDecodePoint(signature[..32], out var encodedR)
            || IsSmallOrder(publicPoint)
            || IsSmallOrder(encodedR))
        {
            return false;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        hash.AppendData(signature[..32]);
        hash.AppendData(trustedPublicKey);
        hash.AppendData(signatureInput);
        var challenge = FromLittleEndian(hash.GetHashAndReset()) % GroupOrder;

        return PointsEqual(
            ScalarMultiply(BasePoint, scalar),
            Add(encodedR, ScalarMultiply(publicPoint, challenge)));
    }

    private static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out EdwardsPoint point)
    {
        Span<byte> yBytes = stackalloc byte[SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes];
        encoded.CopyTo(yBytes);
        var xOdd = (yBytes[31] & 0x80) != 0;
        yBytes[31] &= 0x7f;
        var y = FromLittleEndian(yBytes);
        if (y >= Prime)
        {
            point = default;
            return false;
        }

        var ySquared = Mod(y * y);
        var denominator = Mod(CurveD * ySquared + 1);
        if (denominator.IsZero)
        {
            point = default;
            return false;
        }
        var xSquared = Mod((ySquared - 1) * BigInteger.ModPow(denominator, Prime - 2, Prime));
        var x = BigInteger.ModPow(xSquared, (Prime + 3) / 8, Prime);
        if (Mod(x * x - xSquared) != BigInteger.Zero)
        {
            x = Mod(x * SquareRootOfMinusOne);
        }
        if (Mod(x * x - xSquared) != BigInteger.Zero || x.IsZero && xOdd)
        {
            point = default;
            return false;
        }
        if (x.IsEven == xOdd)
        {
            x = Prime - x;
        }

        point = FromAffine(x, y);
        return IsOnCurve(x, y);
    }

    private static EdwardsPoint ScalarMultiply(EdwardsPoint point, BigInteger scalar)
    {
        var result = Identity;
        var addend = point;
        while (scalar > BigInteger.Zero)
        {
            if (!scalar.IsEven)
            {
                result = Add(result, addend);
            }
            addend = Double(addend);
            scalar >>= 1;
        }
        return result;
    }

    private static EdwardsPoint Add(EdwardsPoint first, EdwardsPoint second)
    {
        var a = Mod((first.Y - first.X) * (second.Y - second.X));
        var b = Mod((first.Y + first.X) * (second.Y + second.X));
        var c = Mod(2 * CurveD * first.T * second.T);
        var d = Mod(2 * first.Z * second.Z);
        var e = Mod(b - a);
        var f = Mod(d - c);
        var g = Mod(d + c);
        var h = Mod(b + a);
        return new EdwardsPoint(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static EdwardsPoint Double(EdwardsPoint point)
    {
        var a = Mod(point.X * point.X);
        var b = Mod(point.Y * point.Y);
        var c = Mod(2 * point.Z * point.Z);
        var d = Mod(-a);
        var e = Mod((point.X + point.Y) * (point.X + point.Y) - a - b);
        var g = Mod(d + b);
        var f = Mod(g - c);
        var h = Mod(d - b);
        return new EdwardsPoint(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static bool PointsEqual(EdwardsPoint first, EdwardsPoint second)
        => Mod(first.X * second.Z - second.X * first.Z).IsZero
           && Mod(first.Y * second.Z - second.Y * first.Z).IsZero;

    private static bool IsSmallOrder(EdwardsPoint point)
        => PointsEqual(ScalarMultiply(point, 8), Identity);

    private static bool IsOnCurve(BigInteger x, BigInteger y)
    {
        var xSquared = Mod(x * x);
        var ySquared = Mod(y * y);
        return Mod(-xSquared + ySquared - 1 - CurveD * xSquared * ySquared).IsZero;
    }

    private static EdwardsPoint FromAffine(BigInteger x, BigInteger y)
        => new(x, y, BigInteger.One, Mod(x * y));

    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> value)
        => new(value, isUnsigned: true, isBigEndian: false);

    private static BigInteger Mod(BigInteger value)
    {
        value %= Prime;
        return value.Sign < 0 ? value + Prime : value;
    }

    private readonly record struct EdwardsPoint(
        BigInteger X,
        BigInteger Y,
        BigInteger Z,
        BigInteger T);
}
