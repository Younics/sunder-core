using System.Buffers.Binary;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Sunder.App.Services;

internal static class RemoteImageDecoder
{
    internal const int MaxSourceDimension = 16_384;
    internal const long MaxSourcePixels = 64_000_000;

    public static bool TryReadMetadata(
        ReadOnlySpan<byte> content,
        string? contentType,
        out RemoteImageMetadata metadata)
    {
        metadata = default;
        if (!TryReadMetadata(content, out var detected)
            || !ContentTypeMatches(contentType, detected.Format)
            || detected.Width <= 0
            || detected.Height <= 0
            || detected.Width > MaxSourceDimension
            || detected.Height > MaxSourceDimension
            || (long)detected.Width * detected.Height > MaxSourcePixels)
        {
            return false;
        }

        metadata = detected;
        return true;
    }

    public static IImage Decode(
        byte[] content,
        RemoteImageMetadata metadata,
        RemoteImageDisplaySize displaySize)
    {
        using var stream = new MemoryStream(content, writable: false);
        Bitmap bitmap;
        if (metadata.Width <= displaySize.Width && metadata.Height <= displaySize.Height)
        {
            bitmap = new Bitmap(stream);
        }
        else if ((double)metadata.Width / displaySize.Width
                 >= (double)metadata.Height / displaySize.Height)
        {
            bitmap = Bitmap.DecodeToWidth(stream, displaySize.Width, BitmapInterpolationMode.HighQuality);
        }
        else
        {
            bitmap = Bitmap.DecodeToHeight(stream, displaySize.Height, BitmapInterpolationMode.HighQuality);
        }

        if (bitmap.PixelSize.Width <= 0
            || bitmap.PixelSize.Height <= 0
            || bitmap.PixelSize.Width > displaySize.Width
            || bitmap.PixelSize.Height > displaySize.Height
            || (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height > displaySize.PixelCount)
        {
            bitmap.Dispose();
            throw new InvalidDataException("Decoded image exceeds its display-size limit.");
        }

        return bitmap;
    }

    private static bool TryReadMetadata(ReadOnlySpan<byte> content, out RemoteImageMetadata metadata)
    {
        if (TryReadPng(content, out metadata)
            || TryReadJpeg(content, out metadata)
            || TryReadGif(content, out metadata)
            || TryReadWebP(content, out metadata)
            || TryReadBitmap(content, out metadata)
            || TryReadIcon(content, out metadata))
        {
            return true;
        }

        metadata = default;
        return false;
    }

    private static bool TryReadPng(ReadOnlySpan<byte> content, out RemoteImageMetadata metadata)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (content.Length >= 24 && content[..8].SequenceEqual(signature))
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(content[16..20]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(content[20..24]);
            if (width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue)
            {
                metadata = new RemoteImageMetadata((int)width, (int)height, RemoteImageFormat.Png);
                return true;
            }
        }

        metadata = default;
        return false;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> content, out RemoteImageMetadata metadata)
    {
        metadata = default;
        if (content.Length < 4 || content[0] != 0xff || content[1] != 0xd8)
        {
            return false;
        }

        var offset = 2;
        while (offset + 1 < content.Length)
        {
            while (offset < content.Length && content[offset] != 0xff)
            {
                offset++;
            }
            while (offset < content.Length && content[offset] == 0xff)
            {
                offset++;
            }
            if (offset >= content.Length)
            {
                return false;
            }

            var marker = content[offset++];
            if (marker is 0xd8 or 0x01 || marker is >= 0xd0 and <= 0xd7)
            {
                continue;
            }
            if (marker is 0xd9 or 0xda || offset + 2 > content.Length)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(content[offset..(offset + 2)]);
            if (segmentLength < 2 || offset + segmentLength > content.Length)
            {
                return false;
            }

            if (IsJpegStartOfFrame(marker) && segmentLength >= 7)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(content[(offset + 3)..(offset + 5)]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(content[(offset + 5)..(offset + 7)]);
                if (width > 0 && height > 0)
                {
                    metadata = new RemoteImageMetadata(width, height, RemoteImageFormat.Jpeg);
                    return true;
                }
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool TryReadGif(ReadOnlySpan<byte> content, out RemoteImageMetadata metadata)
    {
        metadata = default;
        if (content.Length < 13
            || !(content[..6].SequenceEqual("GIF87a"u8) || content[..6].SequenceEqual("GIF89a"u8)))
        {
            return false;
        }

        var maxWidth = (int)BinaryPrimitives.ReadUInt16LittleEndian(content[6..8]);
        var maxHeight = (int)BinaryPrimitives.ReadUInt16LittleEndian(content[8..10]);
        if (maxWidth <= 0 || maxHeight <= 0)
        {
            return false;
        }

        var offset = 13;
        if ((content[10] & 0x80) != 0)
        {
            offset += 3 * (1 << ((content[10] & 0x07) + 1));
        }

        var frameCount = 0;
        while (offset < content.Length)
        {
            switch (content[offset++])
            {
                case 0x3b:
                    metadata = new RemoteImageMetadata(maxWidth, maxHeight, RemoteImageFormat.Gif);
                    return true;
                case 0x21:
                    if (offset >= content.Length)
                    {
                        return false;
                    }
                    offset++;
                    if (!TrySkipGifSubBlocks(content, ref offset))
                    {
                        return false;
                    }
                    break;
                case 0x2c:
                    if (++frameCount > 256 || offset + 9 > content.Length)
                    {
                        return false;
                    }
                    var left = BinaryPrimitives.ReadUInt16LittleEndian(content[offset..(offset + 2)]);
                    var top = BinaryPrimitives.ReadUInt16LittleEndian(content[(offset + 2)..(offset + 4)]);
                    var width = BinaryPrimitives.ReadUInt16LittleEndian(content[(offset + 4)..(offset + 6)]);
                    var height = BinaryPrimitives.ReadUInt16LittleEndian(content[(offset + 6)..(offset + 8)]);
                    var packed = content[offset + 8];
                    if (width == 0 || height == 0)
                    {
                        return false;
                    }
                    maxWidth = Math.Max(maxWidth, left + width);
                    maxHeight = Math.Max(maxHeight, top + height);
                    offset += 9;
                    if ((packed & 0x80) != 0)
                    {
                        offset += 3 * (1 << ((packed & 0x07) + 1));
                    }
                    if (offset >= content.Length)
                    {
                        return false;
                    }
                    offset++;
                    if (!TrySkipGifSubBlocks(content, ref offset))
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> content, out RemoteImageMetadata metadata)
    {
        metadata = default;
        if (content.Length < 30
            || !content[..4].SequenceEqual("RIFF"u8)
            || !content[8..12].SequenceEqual("WEBP"u8))
        {
            return false;
        }

        if (content[12..16].SequenceEqual("VP8X"u8))
        {
            var width = 1 + ReadUInt24LittleEndian(content[24..27]);
            var height = 1 + ReadUInt24LittleEndian(content[27..30]);
            metadata = new RemoteImageMetadata(width, height, RemoteImageFormat.WebP);
            return true;
        }

        if (content[12..16].SequenceEqual("VP8 "u8)
            && content[23] == 0x9d
            && content[24] == 0x01
            && content[25] == 0x2a)
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(content[26..28]) & 0x3fff;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(content[28..30]) & 0x3fff;
            metadata = new RemoteImageMetadata(width, height, RemoteImageFormat.WebP);
            return width > 0 && height > 0;
        }

        if (content[12..16].SequenceEqual("VP8L"u8) && content[20] == 0x2f)
        {
            var dimensions = BinaryPrimitives.ReadUInt32LittleEndian(content[21..25]);
            var width = (int)(dimensions & 0x3fff) + 1;
            var height = (int)((dimensions >> 14) & 0x3fff) + 1;
            metadata = new RemoteImageMetadata(width, height, RemoteImageFormat.WebP);
            return true;
        }

        return false;
    }

    private static bool TryReadBitmap(ReadOnlySpan<byte> content, out RemoteImageMetadata metadata)
    {
        metadata = default;
        if (content.Length < 26 || content[0] != (byte)'B' || content[1] != (byte)'M')
        {
            return false;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(content[14..18]);
        if (headerSize == 12)
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(content[18..20]);
            var height = BinaryPrimitives.ReadUInt16LittleEndian(content[20..22]);
            metadata = new RemoteImageMetadata(width, height, RemoteImageFormat.Bitmap);
            return width > 0 && height > 0;
        }
        if (headerSize < 40)
        {
            return false;
        }

        var signedWidth = BinaryPrimitives.ReadInt32LittleEndian(content[18..22]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(content[22..26]);
        if (signedWidth <= 0 || signedHeight is 0 or int.MinValue)
        {
            return false;
        }
        metadata = new RemoteImageMetadata(signedWidth, Math.Abs(signedHeight), RemoteImageFormat.Bitmap);
        return true;
    }

    private static bool TryReadIcon(ReadOnlySpan<byte> content, out RemoteImageMetadata metadata)
    {
        metadata = default;
        if (content.Length < 22
            || BinaryPrimitives.ReadUInt16LittleEndian(content[..2]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(content[2..4]) != 1)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(content[4..6]);
        if (count is 0 or > 256 || 6 + (count * 16) > content.Length)
        {
            return false;
        }

        var maxWidth = 0;
        var maxHeight = 0;
        for (var index = 0; index < count; index++)
        {
            var entry = content[(6 + (index * 16))..];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..12]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..16]);
            if (length == 0 || offset > int.MaxValue || length > int.MaxValue
                || (long)offset + length > content.Length)
            {
                return false;
            }

            var payload = content.Slice((int)offset, (int)length);
            if (!TryReadIconPayload(payload, out var width, out var height))
            {
                return false;
            }
            maxWidth = Math.Max(maxWidth, width);
            maxHeight = Math.Max(maxHeight, height);
        }

        metadata = new RemoteImageMetadata(maxWidth, maxHeight, RemoteImageFormat.Icon);
        return maxWidth > 0 && maxHeight > 0;
    }

    private static bool TryReadIconPayload(ReadOnlySpan<byte> payload, out int width, out int height)
    {
        if (TryReadPng(payload, out var png))
        {
            width = png.Width;
            height = png.Height;
            return true;
        }

        width = 0;
        height = 0;
        if (payload.Length < 12)
        {
            return false;
        }
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..6]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..8]) / 2;
            return width > 0 && height > 0;
        }
        if (headerSize < 40 || payload.Length < 12)
        {
            return false;
        }

        var signedWidth = BinaryPrimitives.ReadInt32LittleEndian(payload[4..8]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(payload[8..12]);
        if (signedWidth <= 0 || signedHeight is 0 or int.MinValue)
        {
            return false;
        }
        width = signedWidth;
        height = Math.Abs(signedHeight) / 2;
        return height > 0;
    }

    private static bool ContentTypeMatches(string? contentType, RemoteImageFormat format)
        => contentType?.Trim().ToLowerInvariant() switch
        {
            "image/png" => format == RemoteImageFormat.Png,
            "image/jpeg" or "image/jpg" => format == RemoteImageFormat.Jpeg,
            "image/gif" => format == RemoteImageFormat.Gif,
            "image/webp" => format == RemoteImageFormat.WebP,
            "image/bmp" or "image/x-ms-bmp" => format == RemoteImageFormat.Bitmap,
            "image/x-icon" or "image/vnd.microsoft.icon" => format == RemoteImageFormat.Icon,
            _ => false,
        };

    private static bool IsJpegStartOfFrame(byte marker)
        => marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7
            or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private static bool TrySkipGifSubBlocks(ReadOnlySpan<byte> content, ref int offset)
    {
        while (offset < content.Length)
        {
            var length = content[offset++];
            if (length == 0)
            {
                return true;
            }
            if (offset + length > content.Length)
            {
                return false;
            }
            offset += length;
        }
        return false;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> value)
        => value[0] | (value[1] << 8) | (value[2] << 16);
}

internal readonly record struct RemoteImageMetadata(
    int Width,
    int Height,
    RemoteImageFormat Format);

internal enum RemoteImageFormat
{
    Png,
    Jpeg,
    Gif,
    WebP,
    Bitmap,
    Icon,
}

internal readonly record struct RemoteImageDisplaySize(int Width, int Height)
{
    public long PixelCount => (long)Width * Height;
}
