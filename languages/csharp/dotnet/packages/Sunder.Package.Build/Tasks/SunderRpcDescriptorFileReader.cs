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
        => SunderBuildReadSnapshot.Read(path, maximumBytes, label);

    internal static byte[] ReadCurrentBytes(string path, int maximumBytes, string label)
    {
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var fullPath = Path.GetFullPath(path);
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
        // One refreshed leaf observation supplies both the regular-file check
        // and its state. Ancestors and the leaf are still checked before open,
        // after open and after the exact byte read.
        GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: false, label);
        var info = new FileInfo(path);
        info.Refresh();
        if (info.Attributes != (FileAttributes)(-1) && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The {label} '{path}' passes through symbolic link or reparse point '{path}'.");
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new IOException($"The {label} '{path}' must be a regular non-reparse file.");
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
