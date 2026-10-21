using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Package.Templates.Tests;

public sealed class SdkCorpusNpmTests
{
    [Theory]
    [InlineData("traversal")]
    [InlineData("link")]
    [InlineData("duplicate")]
    [InlineData("other-root")]
    public void UnsafeNpmArchiveCannotBindAnInstalledCandidate(string fault)
    {
        using var work = TemporaryDirectoryScope.Create("sunder-npm-binding");
        var archive = work.GetPath("input.tgz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        using (var writer = new TarWriter(gzip))
        {
            writer.WriteEntry(FileEntry("package/package.json", "{}"));
            if (fault == "link") writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "package/linked") { LinkName = "../../outside" });
            else writer.WriteEntry(FileEntry(fault switch { "traversal" => "package/../outside", "duplicate" => "package/PACKAGE.JSON", _ => "other/file.js" }, "bad"));
        }
        Assert.Throws<InvalidDataException>(() => SdkCorpusNpm.ExtractPackage(archive, work.GetPath("installed")));
        Assert.False(File.Exists(work.GetPath("outside")));
    }

    [Fact]
    public void LegacyDefinitelyTypedRootIsContainedAndByteExact()
    {
        using var work = TemporaryDirectoryScope.Create("sunder-npm-legacy-root");
        var archive = work.GetPath("node-types.tgz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        using (var writer = new TarWriter(gzip))
        {
            writer.WriteEntry(FileEntry("node v24.0.0/package.json", "{\"name\":\"@types/node\",\"version\":\"24.0.0\"}"));
            writer.WriteEntry(FileEntry("node v24.0.0/index.d.ts", "export {};"));
        }
        SdkCorpusNpm.ExtractPackage(archive, work.GetPath("installed"));
        Assert.Equal("export {};", File.ReadAllText(work.GetPath("installed/index.d.ts")));
        var integrity = SdkCorpusNpm.Integrity(archive);
        SdkCorpusNpm.RequireIntegrity(archive, integrity);
        // An otherwise readable tarball with altered transport bytes must not
        // masquerade as the lockfile's exact original dependency.
        using (var output = new FileStream(archive, FileMode.Append)) output.WriteByte(1);
        Assert.Throws<InvalidDataException>(() => SdkCorpusNpm.RequireIntegrity(archive, integrity));
    }

    [Theory]
    [InlineData("node_modules/../../elsewhere")]
    [InlineData("node_modules/outer/node_modules/inner")]
    [InlineData("other/path")]
    public void NpmLockCannotRedirectTheFlatSelectedDependencyTree(string path)
        => Assert.Throws<InvalidDataException>(() => SdkCorpusNpm.PackageName(path));

    [Fact]
    public void OptionalNativeDependenciesAreSelectedByActualOsAndArchitecture()
    {
        using var json = JsonDocument.Parse("""{"os":["darwin"],"cpu":["arm64"]}""");
        Assert.True(SdkCorpusNpm.Applies(json.RootElement, "darwin", "arm64"));
        Assert.False(SdkCorpusNpm.Applies(json.RootElement, "darwin", "x64"));
        Assert.False(SdkCorpusNpm.Applies(json.RootElement, "linux", "arm64"));
    }

    private static PaxTarEntry FileEntry(string name, string contents)
        => new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(contents)) };
}
