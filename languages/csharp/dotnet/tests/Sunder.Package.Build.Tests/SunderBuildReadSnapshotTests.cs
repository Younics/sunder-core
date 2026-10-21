using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderBuildReadSnapshotTests
{
    [Fact]
    public void SameLengthAndTimestampEditCannotAuthorizeCapturedAnalysis()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-read-snapshot");
        var path = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(directory.GetPath("input.cs"));
        File.WriteAllText(path, "old");
        var written = File.GetLastWriteTimeUtc(path);
        using var snapshot = new SunderBuildReadSnapshot();
        Assert.Equal("old"u8.ToArray(), SunderRpcDescriptorFileReader.ReadStableBytes(path, 3, "source"));
        var inputs = new SunderRpcInputSnapshot();
        inputs.Read(path, 3, "source");
        inputs.Verify();
        File.WriteAllText(path, "new");
        File.SetLastWriteTimeUtc(path, written);
        Assert.Throws<IOException>(inputs.Verify);
        Assert.Equal("old"u8.ToArray(), SunderRpcDescriptorFileReader.ReadStableBytes(path, 3, "source"));
        Assert.Throws<IOException>(() => snapshot.Complete());
        Assert.Equal("new"u8.ToArray(), SunderRpcDescriptorFileReader.ReadStableBytes(path, 3, "source"));
    }

    [Fact]
    public void CacheCannotHideNewLimitsOrBorrowMutableCallerBytes()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-read-snapshot");
        var path = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(directory.GetPath("input.cs"));
        File.WriteAllText(path, "old");
        using var snapshot = new SunderBuildReadSnapshot();
        var first = SunderRpcDescriptorFileReader.ReadStableBytes(path, 3, "source");
        first[0] = (byte)'x';
        Assert.Equal("old"u8.ToArray(), SunderRpcDescriptorFileReader.ReadStableBytes(path, 3, "source"));
        Assert.Throws<InvalidDataException>(() => SunderRpcDescriptorFileReader.ReadStableBytes(path, 2, "source"));
        snapshot.Complete();
    }

    [Fact]
    public void ReplacingCapturedAncestorWithLinkRejectsTheHandoff()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-read-snapshot");
        var original = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(directory.GetPath("inputs"));
        Directory.CreateDirectory(original);
        var path = Path.Combine(original, "input.cs");
        File.WriteAllText(path, "old");
        using var snapshot = new SunderBuildReadSnapshot();
        _ = SunderRpcDescriptorFileReader.ReadStableBytes(path, 3, "source");
        var moved = directory.GetPath("moved");
        Directory.Move(original, moved);
        try { Directory.CreateSymbolicLink(original, moved); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Directory.Move(moved, original);
            return;
        }
        Assert.Throws<IOException>(() => snapshot.Complete());
        Directory.Delete(original);
    }

    [Fact]
    public void CancellationReleasesCapturedBytesWithoutReusingThemInTheNextOperation()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-read-snapshot");
        var path = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(directory.GetPath("input.cs"));
        File.WriteAllText(path, "old");
        using (var snapshot = new SunderBuildReadSnapshot())
        {
            _ = SunderRpcDescriptorFileReader.ReadStableBytes(path, 3, "source");
            Assert.Throws<OperationCanceledException>(() => snapshot.Complete(new CancellationToken(canceled: true)));
        }
        File.WriteAllText(path, "new");
        using var next = new SunderBuildReadSnapshot();
        Assert.Equal("new"u8.ToArray(), SunderRpcDescriptorFileReader.ReadStableBytes(path, 3, "source"));
        next.Complete();
    }
}
