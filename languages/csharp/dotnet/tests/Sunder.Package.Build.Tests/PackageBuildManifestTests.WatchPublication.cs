using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class PackageBuildManifestTests
{
    [Theory]
    [InlineData("superseded")]
    [InlineData("faulted")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public void AggregateTask_WatchRevisionFencePreservesCompleteLastGoodOutput(string failure)
    {
        var root = CreateTempDirectory();
        try
        {
            var leaf = CreateCanonicalLeaf(root, "runtime", "win-x64", "last-good");
            var project = Path.Combine(root, "project");
            var target = Path.Combine(project, "bin", "Debug", "net10.0");
            var output = Path.Combine(target, "sunder-dev");
            var revisionFile = Path.Combine(root, "revision");
            File.WriteAllText(revisionFile, "7\n");
            var engine = new TestBuildEngine();
            var task = new AggregateSunderPackageTask
            {
                BuildEngine = engine, TargetLeaves = [TargetLeafItem(leaf)], OutputPath = output,
                OutputDiscoveryRoot = target, ProjectDirectory = project, TargetDirectory = target,
                ExpectedPackageVersion = "1.2.3", DevelopmentRevisionFile = revisionFile, DevelopmentRevision = "7",
            };
            Assert.True(task.Execute(), string.Join(Environment.NewLine, engine.Errors));
            var before = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Append(output + AggregateSunderPackageTask.MarkerFileSuffix)
                .Append(output + Sunder.Package.Format.SunderPackageFormat.DevelopmentImportsSuffix)
                .ToDictionary(path => path, File.ReadAllBytes);
            if (failure == "missing") File.Delete(revisionFile);
            else File.WriteAllText(revisionFile, failure switch { "superseded" => "8\n", "faulted" => "-1\n", _ => "7\nextra" });
            Assert.False(task.Execute());
            Assert.All(before, entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target, "*.stage-*"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target, "*.backup-*"));
            // The same output can recover on the next valid revision. No lock/marker reset.
            File.WriteAllText(revisionFile, "9\n");
            var recovered = new AggregateSunderPackageTask
            {
                BuildEngine = new TestBuildEngine(), TargetLeaves = task.TargetLeaves, OutputPath = output,
                OutputDiscoveryRoot = target, ProjectDirectory = project, TargetDirectory = target,
                ExpectedPackageVersion = "1.2.3", DevelopmentRevisionFile = revisionFile, DevelopmentRevision = "9",
            };
            Assert.True(recovered.Execute());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void WatchPublicationFenceHoldsTheExclusiveHandleUntilTheTransactionEnds()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "revision");
            File.WriteAllText(path, "1\n");
            using (SunderDevelopmentPublication.Acquire(path, "1"))
                Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            File.WriteAllText(path, "2\n");
            Assert.Throws<InvalidDataException>(() => SunderDevelopmentPublication.Acquire(path, "1"));
            using var next = SunderDevelopmentPublication.Acquire(path, "2");
            Assert.NotNull(next);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
