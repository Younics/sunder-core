using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.ExecutionRuntime.Build.Tests;

public sealed class CoreStackTransportDirectoryMoveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentRid_ExistingEmptyOrNonemptyDestinationAndStageRetainIdentityAndMetadata(bool populated)
    {
        using var scope = TemporaryDirectoryScope.Create(nameof(CoreStackTransportDirectoryMoveTests));
        var stage = scope.CreateDirectory("stage");
        var destination = scope.CreateDirectory("destination");
        File.WriteAllText(Path.Combine(stage, "source"), "owned stage");
        if (populated) File.WriteAllText(Path.Combine(destination, "foreign"), "foreign content");
        Directory.SetLastWriteTimeUtc(destination, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var stageBefore = Snapshot(stage);
        var destinationBefore = Snapshot(destination);

        // This calls the real current-RID primitive, not the producer's managed pre-move observation hook.
        Assert.Throws<IOException>(() => CoreStackTransportDirectoryMove.Move(stage, destination));

        Assert.Equal(stageBefore, Snapshot(stage));
        Assert.Equal(destinationBefore, Snapshot(destination));
        Assert.Equal("owned stage", File.ReadAllText(Path.Combine(stage, "source")));
        Assert.Equal(populated ? new[] { "foreign" } : [], Directory.GetFileSystemEntries(destination).Select(Path.GetFileName));
        if (populated) Assert.Equal("foreign content", File.ReadAllText(Path.Combine(destination, "foreign")));
    }

    [Fact]
    public void CurrentRid_SameFilesystemRenamePreservesDirectoryIdentityAndUtf8Paths()
    {
        using var scope = TemporaryDirectoryScope.Create(nameof(CoreStackTransportDirectoryMoveTests));
        var stage = scope.CreateDirectory("stage-\u00e9");
        var destination = scope.GetPath("published-\u03bb");
        File.WriteAllText(Path.Combine(stage, "source"), "owned bytes");
        var identity = Identity(stage);
        var written = File.GetLastWriteTimeUtc(Path.Combine(stage, "source"));

        CoreStackTransportDirectoryMove.Move(stage, destination);

        Assert.False(Directory.Exists(stage));
        Assert.Equal(identity, Identity(destination));
        Assert.Equal("owned bytes", File.ReadAllText(Path.Combine(destination, "source")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(destination, "source")));
    }

    [Theory]
    [InlineData("entry-point")]
    [InlineData("library")]
    [InlineData("platform")]
    [InlineData("image")]
    [InlineData("errno-22")]
    [InlineData("errno-38")]
    [InlineData("errno-45")]
    [InlineData("errno-78")]
    [InlineData("errno-95")]
    public void UnavailableNativeOperationOrFilesystemSupport_NeverFallsBackToManagedMove(string failure)
    {
        using var scope = TemporaryDirectoryScope.Create(nameof(CoreStackTransportDirectoryMoveTests));
        var stage = scope.CreateDirectory("stage");
        var destination = scope.GetPath("absent destination");
        File.WriteAllText(Path.Combine(stage, "source"), "owned bytes");
        var before = Snapshot(stage);
        var calls = 0;
        var error = Assert.Throws<IOException>(() => CoreStackTransportDirectoryMove.Move(stage, destination, () =>
        {
            calls++;
            switch (failure)
            {
                case "entry-point": throw new EntryPointNotFoundException();
                case "library": throw new DllNotFoundException();
                case "platform": throw new PlatformNotSupportedException();
                case "image": throw new BadImageFormatException();
                default:
                    Marshal.SetLastPInvokeError(int.Parse(failure[6..], System.Globalization.CultureInfo.InvariantCulture));
                    return -1;
            }
        }));
        Assert.Equal(1, calls);
        Assert.Contains("no fallback", error.Message, StringComparison.Ordinal);
        if (failure.StartsWith("errno-", StringComparison.Ordinal))
            Assert.Equal(int.Parse(failure[6..], System.Globalization.CultureInfo.InvariantCulture), Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        Assert.Equal(before, Snapshot(stage));
        Assert.Equal("owned bytes", File.ReadAllText(Path.Combine(stage, "source")));
        Assert.False(Directory.Exists(destination));
    }

    private static (string Identity, DateTime Created, DateTime Written, FileAttributes Attributes, UnixFileMode? Mode) Snapshot(string path)
        => (Identity(path), Directory.GetCreationTimeUtc(path), Directory.GetLastWriteTimeUtc(path), File.GetAttributes(path),
            OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path));

    // Match the repository's read-only physical-identity probes, without a Host assembly reference.
    private static string Identity(string path)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.Copy(new byte[512], 0, buffer, 512);
            if (OperatingSystem.IsWindows())
            {
                using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                Assert.False(handle.IsInvalid);
                Assert.True(GetFileInformationByHandleEx(handle, 18, buffer, 24));
                return $"windows:{Marshal.ReadInt64(buffer, 0):x16}:{Marshal.ReadInt64(buffer, 8):x16}:{Marshal.ReadInt64(buffer, 16):x16}";
            }
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(0, Statx(-100, path, 0x100, 0x100, buffer));
                Assert.NotEqual(0, Marshal.ReadInt32(buffer) & 0x100);
                return $"linux:{Marshal.ReadInt32(buffer, 136):x8}:{Marshal.ReadInt32(buffer, 140):x8}:{Marshal.ReadInt64(buffer, 32):x16}";
            }
            Assert.True(OperatingSystem.IsMacOS(), "Physical directory identity requires a supported Core RID.");
            Assert.Equal(0, LStat64(path, buffer));
            return $"darwin:{Marshal.ReadInt32(buffer, 0):x8}:{Marshal.ReadInt64(buffer, 8):x16}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, IntPtr buffer);
    [DllImport("libc", EntryPoint = "lstat64", SetLastError = true)]
    private static extern int LStat64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, IntPtr information, uint size);
}
