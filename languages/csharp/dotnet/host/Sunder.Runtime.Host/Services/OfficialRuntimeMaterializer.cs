using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.OfficialRuntimeSourcePolicy;

namespace Sunder.Runtime.Host.Services;

internal sealed record OfficialRuntimeMaterializationResult(string ArchiveSha256, long ArchiveSize,
    string ContentIdentity, string LauncherPath, IReadOnlyList<ExecutionRuntimeReceiptContentV1> Content);

internal sealed class OfficialRuntimeMaterializer(IOfficialRuntimePlatformProbe probe)
{
    internal const int ReadOnlyMode = (int)(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    internal const int ExecutableMode = ReadOnlyMode | (int)UnixFileMode.UserExecute;

    internal async Task<OfficialRuntimeMaterializationResult> MaterializeAsync(RuntimeExecutionRuntimeVendorArtifact artifact,
        string sourcePath, string verifiedPath, string rootPath, CancellationToken token)
    {
        ValidateArtifact(artifact);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        token = deadline.Token;
        if (Directory.Exists(rootPath) || File.Exists(verifiedPath))
            throw new InvalidDataException("Official materialization requires fresh private staging paths.");
        Directory.CreateDirectory(rootPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(rootPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var (archiveHash, archiveSize) = await CaptureVerifiedArchiveAsync(artifact, sourcePath, verifiedPath, token).ConfigureAwait(false);
        var launcher = LauncherPath(artifact.Identity);
        var extraction = new Extraction(artifact.Identity, rootPath, launcher);
        await using (var archive = new FileStream(verifiedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (artifact.Distribution.ArchiveFormat == RuntimeExecutionRuntimeArchiveFormat.Zip)
            {
                using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("Official ZIP has too many entries.");
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var kind = (entry.ExternalAttributes >> 16) & 0xf000;
                    if (kind is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Official ZIP contains a link or special entry.");
                    var directory = entry.FullName.EndsWith('/') || kind == 0x4000;
                    var relative = extraction.Admit(entry.FullName, directory, entry.Length);
                    if (relative is null || directory) continue;
                    await using var input = entry.Open();
                    await extraction.WriteAsync(relative, input, entry.Length, token).ConfigureAwait(false);
                }
            }
            else
            {
                using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
                using var bounded = new ExpandedStream(gzip, MaxExpandedBytes);
                using var reader = new TarReader(bounded, leaveOpen: true);
                TarEntry? entry;
                while ((entry = await reader.GetNextEntryAsync(copyData: false, token).ConfigureAwait(false)) is not null)
                {
                    var directory = entry.EntryType == TarEntryType.Directory;
                    var relative = extraction.Admit(entry.Name, directory, entry.Length);
                    if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                    {
                        if (!KnownSkippedNodeLink(artifact.Identity, entry))
                            throw new InvalidDataException("Official TAR contains an unapproved link.");
                        continue; // The closed Node runtime profile never materializes npm/npx/corepack links.
                    }
                    if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        throw new InvalidDataException("Official TAR contains a special entry.");
                    if (relative is null || directory) continue;
                    if (entry.DataStream is null && entry.Length != 0) throw new InvalidDataException("Missing TAR content.");
                    await extraction.WriteAsync(relative, entry.DataStream ?? Stream.Null, entry.Length, token).ConfigureAwait(false);
                }
                // Consume trailing data through the same expansion bound; never leave unchecked compressed tails.
                var buffer = new byte[8192];
                while (await bounded.ReadAsync(buffer, token).ConfigureAwait(false) != 0) { }
            }
        }
        var content = extraction.Complete();
        await probe.VerifyAsync(artifact, rootPath, launcher, token).ConfigureAwait(false);
        return new(archiveHash, archiveSize, ComputeContentIdentity(artifact.Distribution.MaterializationProfile, content), launcher, content);
    }

    internal static string LauncherPath(RuntimeExecutionRuntimeIdentity identity) => identity.SourceId == ConfiguredOfficialSourceId.DotNet
        ? identity.Rid.StartsWith("win-", StringComparison.Ordinal) ? "dotnet.exe" : "dotnet"
        : identity.Rid.StartsWith("win-", StringComparison.Ordinal) ? "node.exe" : "bin/node";

    internal static string ComputeContentIdentity(string profile, IReadOnlyList<ExecutionRuntimeReceiptContentV1> content)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            MaterializationProfile = profile,
            Files = content.Select(file => new { file.Path, file.Sha256, file.Size, file.UnixMode }).ToArray(),
        })));

    private static async Task<(string Hash, long Size)> CaptureVerifiedArchiveAsync(RuntimeExecutionRuntimeVendorArtifact artifact,
        string sourcePath, string verifiedPath, CancellationToken token)
    {
        PackageStorePathGuard.EnsureNotReparsePoint(sourcePath);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is <= 0 or > MaxArchiveBytes) throw new InvalidDataException("Official archive exceeds its byte bounds.");
        if (artifact.Distribution.DownloadSize is { } size && size != input.Length)
            throw new InvalidDataException("Official archive length differs from its metadata.");
        await using var output = new FileStream(verifiedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var vendor = IncrementalHash.CreateHash(VendorHash(artifact.Identity));
        using var local = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total = checked(total + count);
            if (total > MaxArchiveBytes) throw new InvalidDataException("Official archive exceeds its byte bound.");
            vendor.AppendData(buffer, 0, count);
            local.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (total != input.Length || !CryptographicOperations.FixedTimeEquals(vendor.GetHashAndReset(), Convert.FromHexString(artifact.Identity.VendorChecksumDigest)))
            throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.IntegrityFailed, "Official vendor checksum verification failed.");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return (Convert.ToHexStringLower(local.GetHashAndReset()), total);
    }

    internal static string ValidateRelativePath(string name, bool directory = false)
    {
        if (name.Length is 0 or > MaxPathLength || name.Contains('\\') || name.Any(char.IsControl) || name.StartsWith('/'))
            throw new InvalidDataException("Unsafe official archive entry path.");
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        if (directory) name = name.TrimEnd('/');
        if (name is "" or ".") return "";
        var parts = name.Split('/');
        if (parts.Length > 32 || parts.Any(part => part.Length is 0 or > 200 || part is "." or ".."
            || part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':') || part.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0
            || IsDeviceName(part))) throw new InvalidDataException("Unsafe official archive entry path.");
        return name;
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && stem[3] is >= '1' and <= '9'
            && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal));
    }

    private static bool KnownSkippedNodeLink(RuntimeExecutionRuntimeIdentity identity, TarEntry entry)
    {
        if (identity.SourceId != ConfiguredOfficialSourceId.Node || entry.EntryType != TarEntryType.SymbolicLink) return false;
        var prefix = $"node-v{identity.VendorVersion}-{NodePlatform(identity.Rid)}/";
        return (entry.Name, entry.LinkName) switch
        {
            var (name, link) when name == prefix + "bin/npm" && link == "../lib/node_modules/npm/bin/npm-cli.js" => true,
            var (name, link) when name == prefix + "bin/npx" && link == "../lib/node_modules/npm/bin/npx-cli.js" => true,
            var (name, link) when name == prefix + "bin/corepack" && link == "../lib/node_modules/corepack/dist/corepack.js" => true,
            _ => false,
        };
    }

    private sealed class Extraction(RuntimeExecutionRuntimeIdentity identity, string root, string launcher)
    {
        private readonly Dictionary<string, bool> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ExecutionRuntimeReceiptContentV1> _content = [];
        private long _expanded;
        private int _entries;

        internal string? Admit(string name, bool directory, long size)
        {
            if (++_entries > MaxEntries || size is < 0 or > MaxFileBytes || (_expanded = checked(_expanded + size)) > MaxExpandedBytes)
                throw new InvalidDataException("Official archive expansion bounds exceeded.");
            name = ValidateRelativePath(name, directory);
            if (name.Length == 0) return null;
            if (!_paths.TryAdd(name, directory)) throw new InvalidDataException("Duplicate or case-colliding official archive entry.");
            var components = name.Split('/');
            for (var index = 1; index < components.Length; index++)
            {
                var parent = string.Join('/', components.Take(index));
                if (_paths.TryGetValue(parent, out var parentIsDirectory) && !parentIsDirectory)
                    throw new InvalidDataException("Official archive file shadows a directory.");
                if (_directories.TryGetValue(parent, out var canonical) && canonical != parent)
                    throw new InvalidDataException("Case-colliding official archive directories.");
                _directories.TryAdd(parent, parent);
            }
            if (!directory && _directories.ContainsKey(name))
                throw new InvalidDataException("Official archive file shadows a directory.");
            if (directory)
            {
                if (_directories.TryGetValue(name, out var canonical) && canonical != name)
                    throw new InvalidDataException("Case-colliding official archive directories.");
                _directories.TryAdd(name, name);
            }
            if (identity.SourceId == ConfiguredOfficialSourceId.Node)
            {
                var prefix = $"node-v{identity.VendorVersion}-{NodePlatform(identity.Rid)}";
                if (name == prefix && directory) return null;
                if (!name.StartsWith(prefix + "/", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected Node archive root.");
                name = name[(prefix.Length + 1)..];
                return name == launcher || name == "LICENSE" ? name : null;
            }
            if (directory) return null; // Only create directories needed by the fixed content inventory.
            if (name != launcher && name is not ("LICENSE.txt" or "ThirdPartyNotices.txt")
                && !name.StartsWith($"host/fxr/{identity.VendorVersion}/", StringComparison.Ordinal)
                && !name.StartsWith($"shared/Microsoft.NETCore.App/{identity.VendorVersion}/", StringComparison.Ordinal))
                throw new InvalidDataException("The official .NET archive does not match the exact Microsoft.NETCore.App Runtime-only profile.");
            return name;
        }

        internal async Task WriteAsync(string relative, Stream input, long size, CancellationToken token)
        {
            var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Official archive escapes private root.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            PackageStorePathGuard.EnsureSafe(destination, root);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                written += read;
                if (written > size) throw new InvalidDataException("Official archive entry exceeds its declared size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (written != size) throw new InvalidDataException("Truncated official archive entry.");
            await output.FlushAsync(token).ConfigureAwait(false);
            var mode = relative == launcher ? ExecutableMode : ReadOnlyMode;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, (UnixFileMode)mode | UnixFileMode.UserWrite);
            _content.Add(new(relative, Convert.ToHexStringLower(hash.GetHashAndReset()), size, mode));
        }

        internal IReadOnlyList<ExecutionRuntimeReceiptContentV1> Complete()
        {
            if (!_content.Any(file => file.Path == launcher && file.Size > 0)) throw new InvalidDataException("Official archive has no native runtime executable.");
            if (identity.SourceId == ConfiguredOfficialSourceId.DotNet
                && !_content.Any(file => file.Path == $"shared/Microsoft.NETCore.App/{identity.VendorVersion}/System.Private.CoreLib.dll"))
                throw new InvalidDataException("Official .NET Runtime archive has no matching Microsoft.NETCore.App.");
            return Array.AsReadOnly(_content.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
        }
    }

    // Includes TAR headers/PAX/ignored content in the budget, not only extracted files.
    private sealed class ExpandedStream(Stream input, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Account(input.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Account(input.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Account(await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        private int Account(int count)
        {
            if ((_read = checked(_read + count)) > limit) throw new InvalidDataException("Official TAR expanded stream exceeds its byte limit.");
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
