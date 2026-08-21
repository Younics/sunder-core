using System.Globalization;
using System.Text.Json.Serialization;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Rpc;

/// <summary>Preserves one validated RPC <c>date-time</c> value without losing fractional precision or offset text.</summary>
[JsonConverter(typeof(SunderRpcV2JsonConverterFactory))]
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public readonly record struct SunderRpcOffsetDateTime
{
    /// <summary>Creates an exact offset date-time from canonical RPC wire text.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> is not a valid RPC offset date-time.</exception>
    public SunderRpcOffsetDateTime(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!SunderRpcValueText.IsOffsetDateTime(value))
        {
            throw new FormatException("The value is not a valid RPC offset date-time.");
        }
        Value = value;
    }

    /// <summary>Gets the exact canonical wire text.</summary>
    public string Value { get; }

    /// <summary>Parses canonical RPC offset date-time text.</summary>
    public static SunderRpcOffsetDateTime Parse(string value) => new(value);

    /// <summary>Attempts to parse canonical RPC offset date-time text.</summary>
    public static bool TryParse(string? value, out SunderRpcOffsetDateTime result)
    {
        if (value is null || !SunderRpcValueText.IsOffsetDateTime(value))
        {
            result = default;
            return false;
        }
        result = new SunderRpcOffsetDateTime(value);
        return true;
    }

    /// <summary>Gets a <see cref="DateTimeOffset"/> only when its tick precision can represent the exact instant.</summary>
    public bool TryGetDateTimeOffset(out DateTimeOffset value)
        => SunderRpcValueText.TryGetDateTimeOffset(Value, out value);

    /// <summary>Returns the exact canonical wire text.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Preserves one validated canonical non-negative RPC duration.</summary>
[JsonConverter(typeof(SunderRpcV2JsonConverterFactory))]
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public readonly record struct SunderRpcDuration
{
    /// <summary>Creates a duration from canonical RPC wire text.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> is not a canonical RPC duration.</exception>
    public SunderRpcDuration(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!SunderRpcValueText.IsDuration(value))
        {
            throw new FormatException("The value is not a canonical RPC duration.");
        }
        Value = value;
    }

    /// <summary>Gets the exact canonical wire text.</summary>
    public string Value { get; }

    /// <summary>Parses canonical RPC duration text.</summary>
    public static SunderRpcDuration Parse(string value) => new(value);

    /// <summary>Attempts to parse canonical RPC duration text.</summary>
    public static bool TryParse(string? value, out SunderRpcDuration result)
    {
        if (value is null || !SunderRpcValueText.IsDuration(value))
        {
            result = default;
            return false;
        }
        result = new SunderRpcDuration(value);
        return true;
    }

    /// <summary>Returns the exact canonical wire text.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Preserves one validated absolute RPC URI without applying URI normalization.</summary>
[JsonConverter(typeof(SunderRpcV2JsonConverterFactory))]
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public readonly record struct SunderRpcUri
{
    /// <summary>Creates an exact URI from validated RPC wire text.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> is not a valid absolute RPC URI.</exception>
    public SunderRpcUri(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!SunderRpcValueText.IsAbsoluteUri(value))
        {
            throw new FormatException("The value is not a valid absolute RPC URI.");
        }
        Value = value;
    }

    /// <summary>Gets the exact wire text.</summary>
    public string Value { get; }

    /// <summary>Parses validated absolute RPC URI text.</summary>
    public static SunderRpcUri Parse(string value) => new(value);

    /// <summary>Attempts to parse validated absolute RPC URI text.</summary>
    public static bool TryParse(string? value, out SunderRpcUri result)
    {
        if (value is null || !SunderRpcValueText.IsAbsoluteUri(value))
        {
            result = default;
            return false;
        }
        result = new SunderRpcUri(value);
        return true;
    }

    /// <summary>Returns the exact wire text.</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Represents an arbitrary-precision canonical RPC decimal without <see cref="decimal"/> range limits.</summary>
[JsonConverter(typeof(SunderRpcV2JsonConverterFactory))]
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public readonly record struct SunderRpcDecimal
{
    /// <summary>Creates an exact decimal from canonical RPC wire text.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> is not a canonical RPC decimal.</exception>
    public SunderRpcDecimal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!SunderRpcValueText.IsCanonicalDecimal(value))
        {
            throw new FormatException("The value is not a canonical RPC decimal.");
        }
        Value = value;
    }

    /// <summary>Gets the canonical exact decimal text.</summary>
    public string Value { get; }

    /// <summary>Parses canonical RPC decimal text.</summary>
    public static SunderRpcDecimal Parse(string value) => new(value);

    /// <summary>Attempts to parse canonical RPC decimal text.</summary>
    public static bool TryParse(string? value, out SunderRpcDecimal result)
    {
        if (value is null || !SunderRpcValueText.IsCanonicalDecimal(value))
        {
            result = default;
            return false;
        }
        result = new SunderRpcDecimal(value);
        return true;
    }

    /// <summary>Returns the canonical exact decimal text.</summary>
    public override string ToString() => Value ?? string.Empty;
}

internal static class SunderRpcValueText
{
    public static bool IsOffsetDateTime(string value)
    {
        if (value.Length < 20
            || value[10] != 'T'
            || value[13] != ':'
            || value[16] != ':'
            || !IsDate(value.AsSpan(0, 10))
            || !TryReadDigits(value, 11, 2, out var hour)
            || !TryReadDigits(value, 14, 2, out var minute)
            || !TryReadDigits(value, 17, 2, out var second)
            || hour > 23
            || minute > 59
            || second > 59)
        {
            return false;
        }

        var zoneIndex = 19;
        if (value[zoneIndex] == '.')
        {
            var fractionStart = ++zoneIndex;
            while (zoneIndex < value.Length && value[zoneIndex] is >= '0' and <= '9') zoneIndex++;
            if (zoneIndex - fractionStart is < 1 or > 9) return false;
        }
        if (!TryReadOffset(value, zoneIndex, out var offset)) return false;

        try
        {
            _ = new DateTimeOffset(
                int.Parse(value.AsSpan(0, 4), CultureInfo.InvariantCulture),
                int.Parse(value.AsSpan(5, 2), CultureInfo.InvariantCulture),
                int.Parse(value.AsSpan(8, 2), CultureInfo.InvariantCulture),
                hour,
                minute,
                second,
                offset);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool TryGetDateTimeOffset(string? text, out DateTimeOffset value)
    {
        value = default;
        if (text is null || !IsOffsetDateTime(text)) return false;

        var zoneIndex = 19;
        var ticks = 0L;
        if (text[zoneIndex] == '.')
        {
            var fractionStart = ++zoneIndex;
            while (zoneIndex < text.Length && text[zoneIndex] is >= '0' and <= '9') zoneIndex++;
            var fractionLength = zoneIndex - fractionStart;
            for (var index = 7; index < fractionLength; index++)
            {
                if (text[fractionStart + index] != '0') return false;
            }
            for (var index = 0; index < Math.Min(fractionLength, 7); index++)
            {
                ticks = ticks * 10 + text[fractionStart + index] - '0';
            }
            for (var index = fractionLength; index < 7; index++) ticks *= 10;
        }
        _ = TryReadOffset(text, zoneIndex, out var offset);
        try
        {
            value = new DateTimeOffset(
                int.Parse(text.AsSpan(0, 4), CultureInfo.InvariantCulture),
                int.Parse(text.AsSpan(5, 2), CultureInfo.InvariantCulture),
                int.Parse(text.AsSpan(8, 2), CultureInfo.InvariantCulture),
                int.Parse(text.AsSpan(11, 2), CultureInfo.InvariantCulture),
                int.Parse(text.AsSpan(14, 2), CultureInfo.InvariantCulture),
                int.Parse(text.AsSpan(17, 2), CultureInfo.InvariantCulture),
                offset).AddTicks(ticks);
            return true;
        }
        catch (ArgumentException)
        {
            value = default;
            return false;
        }
    }

    public static bool IsDuration(string value)
    {
        if (value.Length < 3 || value[0] != 'P') return false;
        var index = 1;
        var components = 0;
        foreach (var designator in new[] { 'Y', 'M', 'D' })
        {
            var start = index;
            if (ReadDurationNumber(value, ref index, allowFraction: false)
                && index < value.Length
                && value[index] == designator)
            {
                index++;
                components++;
            }
            else
            {
                index = start;
            }
        }
        if (index < value.Length && value[index] == 'T')
        {
            index++;
            var timeComponents = 0;
            foreach (var designator in new[] { 'H', 'M', 'S' })
            {
                var start = index;
                if (ReadDurationNumber(value, ref index, allowFraction: designator == 'S')
                    && index < value.Length
                    && value[index] == designator)
                {
                    index++;
                    timeComponents++;
                    components++;
                }
                else
                {
                    index = start;
                }
            }
            if (timeComponents == 0) return false;
        }
        return components > 0 && index == value.Length;
    }

    public static bool IsAbsoluteUri(string value)
    {
        if (value.Length == 0
            || value.Any(static character => character >= 0x7f || character <= 0x20 || character == '\\'))
        {
            return false;
        }
        var colon = value.IndexOf(':');
        if (colon <= 0
            || value[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z')
            || value.AsSpan(1, colon - 1).IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-.".AsSpan()) >= 0)
        {
            return false;
        }
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !IsHex(value[index + 1]) || !IsHex(value[index + 2])) return false;
            index += 2;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out _);
    }

    public static bool IsCanonicalBigInteger(string value)
        => IsCanonicalExactNumber(value, allowFraction: false);

    public static bool IsCanonicalDecimal(string value)
        => IsCanonicalExactNumber(value, allowFraction: true);

    private static bool IsCanonicalExactNumber(string value, bool allowFraction)
    {
        if (value.Length == 0) return false;
        var negative = value[0] == '-';
        var offset = negative ? 1 : 0;
        if (offset == value.Length) return false;
        var decimalIndex = value.IndexOf('.', offset);
        if (!allowFraction && decimalIndex >= 0) return false;
        if (decimalIndex >= 0 && value.IndexOf('.', decimalIndex + 1) >= 0) return false;
        var integerEnd = decimalIndex >= 0 ? decimalIndex : value.Length;
        if (integerEnd == offset
            || value[offset] == '0' && integerEnd - offset > 1
            || value.AsSpan(offset, integerEnd - offset).IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            return false;
        }
        if (decimalIndex >= 0)
        {
            var fraction = value.AsSpan(decimalIndex + 1);
            if (fraction.Length == 0
                || fraction[^1] == '0'
                || fraction.IndexOfAnyExceptInRange('0', '9') >= 0)
            {
                return false;
            }
        }
        if (!negative) return true;
        for (var index = offset; index < value.Length; index++)
        {
            if (value[index] is >= '1' and <= '9') return true;
        }
        return false;
    }

    private static bool IsDate(ReadOnlySpan<char> value)
        => value.Length == 10
           && value[4] == '-'
           && value[7] == '-'
           && TryReadDigits(value, 0, 4, out var year)
           && TryReadDigits(value, 5, 2, out var month)
           && TryReadDigits(value, 8, 2, out var day)
           && year is >= 1 and <= 9999
           && month is >= 1 and <= 12
           && day >= 1
           && day <= DateTime.DaysInMonth(year, month);

    private static bool TryReadOffset(string value, int zoneIndex, out TimeSpan offset)
    {
        if (zoneIndex == value.Length - 1 && value[zoneIndex] == 'Z')
        {
            offset = TimeSpan.Zero;
            return true;
        }
        if (value.Length - zoneIndex != 6
            || value[zoneIndex] is not ('+' or '-')
            || value[zoneIndex + 3] != ':'
            || !TryReadDigits(value, zoneIndex + 1, 2, out var offsetHours)
            || !TryReadDigits(value, zoneIndex + 4, 2, out var offsetMinutes)
            || offsetHours > 14
            || offsetMinutes > 59
            || offsetHours == 14 && offsetMinutes != 0)
        {
            offset = default;
            return false;
        }
        offset = new TimeSpan(offsetHours, offsetMinutes, 0);
        if (value[zoneIndex] == '-') offset = -offset;
        return true;
    }

    private static bool ReadDurationNumber(string value, ref int index, bool allowFraction)
    {
        var start = index;
        while (index < value.Length && value[index] is >= '0' and <= '9') index++;
        if (index == start || value[start] == '0' && index - start > 1) return false;
        if (allowFraction && index < value.Length && value[index] == '.')
        {
            var fractionStart = ++index;
            while (index < value.Length && value[index] is >= '0' and <= '9') index++;
            if (index == fractionStart || value[index - 1] == '0') return false;
        }
        return true;
    }

    private static bool TryReadDigits(string value, int offset, int length, out int result)
        => TryReadDigits(value.AsSpan(), offset, length, out result);

    private static bool TryReadDigits(ReadOnlySpan<char> value, int offset, int length, out int result)
    {
        result = 0;
        for (var index = offset; index < offset + length; index++)
        {
            if (value[index] is not (>= '0' and <= '9')) return false;
            result = result * 10 + value[index] - '0';
        }
        return true;
    }

    private static bool IsHex(char value)
        => value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
}
