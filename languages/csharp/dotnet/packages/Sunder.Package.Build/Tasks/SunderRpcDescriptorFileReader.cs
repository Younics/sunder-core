using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Tasks;

internal sealed record SunderRpcDescriptorFileSnapshot(
    string SourcePath,
    SunderRpcContractDescriptor Descriptor,
    byte[] CanonicalUtf8);

internal static class SunderRpcDescriptorFileReader
{
    public static SunderRpcDescriptorFileSnapshot Read(string path, string label, bool requireCanonical = false)
    {
        var sourcePath = Path.GetFullPath(path);
        var bytes = ReadStableBytes(sourcePath, SunderRpcProtocol.MaximumDescriptorBytes, label);
        var descriptor = SunderRpcContractDescriptor.Parse(bytes);
        var canonicalUtf8 = descriptor.GetCanonicalUtf8();
        if (requireCanonical && !bytes.AsSpan().SequenceEqual(canonicalUtf8))
        {
            throw new InvalidDataException($"The {label} '{sourcePath}' is not an exact canonical descriptor snapshot.");
        }
        return new SunderRpcDescriptorFileSnapshot(sourcePath, descriptor, canonicalUtf8);
    }

    public static byte[] ReadStableBytes(string path, int maximumBytes, string label)
    {
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var fullPath = Path.GetFullPath(path);
        GeneratedOutputPathSafety.EnsureRegularFile(fullPath, label);
        var before = Capture(fullPath, label);

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var openedLength = stream.Length;
        var afterOpen = Capture(fullPath, label);
        EnsureUnchanged(before, afterOpen, openedLength, fullPath, label);
        if (openedLength > maximumBytes)
        {
            throw new InvalidDataException($"The {label} '{fullPath}' exceeds the {maximumBytes}-byte limit.");
        }

        var bytes = new byte[checked((int)openedLength)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new IOException($"The {label} '{fullPath}' changed length while it was read.");
        }
        var afterRead = Capture(fullPath, label);
        EnsureUnchanged(before, afterRead, stream.Length, fullPath, label);
        return bytes;
    }

    private static FileState Capture(string path, string label)
    {
        GeneratedOutputPathSafety.EnsureRegularFile(path, label);
        var info = new FileInfo(path);
        info.Refresh();
        return new FileState(
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            info.CreationTimeUtc.Ticks,
            info.Attributes);
    }

    private static void EnsureUnchanged(
        FileState expected,
        FileState actual,
        long handleLength,
        string path,
        string label)
    {
        if (expected != actual || expected.Length != handleLength)
        {
            throw new IOException(
                $"The {label} '{path}' changed identity, length, attributes, or modification time while it was read.");
        }
    }

    private readonly record struct FileState(
        long Length,
        long LastWriteTimeUtcTicks,
        long CreationTimeUtcTicks,
        FileAttributes Attributes);
}
