using System.Buffers.Binary;

namespace Sunder.Package.Format;

internal static class ImageFileInspector
{
    public static bool TryRead(string path, out ImageFileInfo image, out string error)
    {
        image = default;
        error = "the file signature is not a supported image type";
        var bytes = File.ReadAllBytes(path);
        if (TryReadPng(bytes, out image)
            || TryReadGif(bytes, out image)
            || TryReadJpeg(bytes, out image)
            || TryReadWebP(bytes, out image)
            || TryReadBmp(bytes, out image)
            || TryReadIcon(bytes, out image))
        {
            if (image.Width is <= 0 || image.Height is <= 0)
            {
                error = "the image declares invalid dimensions";
                image = default;
                return false;
            }
            return true;
        }
        return false;
    }

    public static bool ExtensionMatches(string path, string contentType)
        => (Path.GetExtension(path).ToLowerInvariant(), contentType) switch
        {
            (".png", "image/png") => true,
            (".gif", "image/gif") => true,
            (".jpg" or ".jpeg", "image/jpeg") => true,
            (".webp", "image/webp") => true,
            (".bmp", "image/bmp") => true,
            (".ico", "image/x-icon") => true,
            _ => false,
        };

    private static bool TryReadPng(ReadOnlySpan<byte> bytes, out ImageFileInfo image)
    {
        image = default;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(signature) || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }
        image = new("image/png", BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]));
        return true;
    }

    private static bool TryReadGif(ReadOnlySpan<byte> bytes, out ImageFileInfo image)
    {
        image = default;
        if (bytes.Length < 10 || (!bytes[..6].SequenceEqual("GIF87a"u8) && !bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return false;
        }
        image = new("image/gif", BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]));
        return true;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> bytes, out ImageFileInfo image)
    {
        image = default;
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
        {
            return false;
        }

        for (var offset = 2; offset + 4 < bytes.Length;)
        {
            if (bytes[offset++] != 0xff)
            {
                continue;
            }
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || offset + length > bytes.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (length < 7) break;
                image = new(
                    "image/jpeg",
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2)));
                return true;
            }
            offset += length;
        }
        return false;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> bytes, out ImageFileInfo image)
    {
        image = default;
        if (bytes.Length < 30 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }
        if (bytes.Slice(12, 4).SequenceEqual("VP8X"u8))
        {
            image = new("image/webp", ReadUInt24LittleEndian(bytes[24..27]) + 1, ReadUInt24LittleEndian(bytes[27..30]) + 1);
            return true;
        }
        if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8) && bytes[20] == 0x2f)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..25]);
            image = new("image/webp", (int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
            return true;
        }
        if (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) && bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
        {
            image = new(
                "image/webp",
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..28]) & 0x3fff,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..30]) & 0x3fff);
            return true;
        }
        return false;
    }

    private static bool TryReadBmp(ReadOnlySpan<byte> bytes, out ImageFileInfo image)
    {
        image = default;
        if (bytes.Length < 26 || !bytes[..2].SequenceEqual("BM"u8))
        {
            return false;
        }
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..22]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..26]);
        if (width <= 0 || height == 0 || height == int.MinValue)
        {
            return false;
        }
        image = new("image/bmp", width, Math.Abs(height));
        return true;
    }

    private static bool TryReadIcon(ReadOnlySpan<byte> bytes, out ImageFileInfo image)
    {
        image = default;
        if (bytes.Length < 8 || !bytes[..4].SequenceEqual(new byte[] { 0, 0, 1, 0 }))
        {
            return false;
        }
        image = new("image/x-icon", bytes[6] == 0 ? 256 : bytes[6], bytes[7] == 0 ? 256 : bytes[7]);
        return true;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes)
        => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
}

internal readonly record struct ImageFileInfo(string ContentType, int? Width, int? Height);
