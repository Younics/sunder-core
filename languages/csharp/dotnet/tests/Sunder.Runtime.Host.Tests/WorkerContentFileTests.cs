using System.Runtime.InteropServices;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class WorkerContentFileTests
{
    [Theory]
    [InlineData(false, Architecture.X64, 0xA0800)]
    [InlineData(false, Architecture.Arm64, 0x88800)]
    [InlineData(true, Architecture.X64, 0x1000104)]
    [InlineData(true, Architecture.Arm64, 0x1000104)]
    public void UnixOpenFlagsUseTheSelectedAbi(bool macOS, Architecture architecture, int expected)
        => Assert.Equal(expected, WorkerContentFile.UnixOpenFlags(
            macOS ? WorkerProcessPlatform.MacOS : WorkerProcessPlatform.Linux, architecture));

    [Fact]
    public void UnsupportedUnixAbiIsRejected()
        => Assert.Throws<PlatformNotSupportedException>(() =>
            WorkerContentFile.UnixOpenFlags(WorkerProcessPlatform.Linux, Architecture.X86));

    [UnixFact]
    public void NativeOpenRejectsSymlinkEvenWithoutTheCallersAttributeCheck()
    {
        var root = Directory.CreateTempSubdirectory("sunder-worker-content-").FullName;
        try
        {
            var target = Path.Combine(root, "content");
            var link = Path.Combine(root, "alias");
            File.WriteAllText(target, "content");
            File.CreateSymbolicLink(link, target);
            Assert.Throws<IOException>(() => WorkerContentFile.OpenRead(link));
            using var stream = WorkerContentFile.OpenRead(target);
            Assert.Equal("content", new StreamReader(stream).ReadToEnd());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
