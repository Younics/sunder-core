using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageStorageArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void PublicSdk_DoesNotContainHostStorageOrPlatformCredentialImplementations()
    {
        var sdkRoot = Path.Combine(RepositoryRoot, "src", "Sdk", "Sunder.Sdk");
        var project = File.ReadAllText(Path.Combine(sdkRoot, "Sunder.Sdk.csproj"));
        Assert.DoesNotContain("System.Security.Cryptography.ProtectedData", project, StringComparison.Ordinal);

        var source = ReadSources(sdkRoot);
        Assert.DoesNotContain("ProtectedData.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-tool", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MacOsKeychainMasterKeyStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonPackageSecretsStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalPackageStorageContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FilePackageLogging", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppProductionCode_DoesNotReferenceRuntimeStorageOrCredentialImplementations()
    {
        var source = ReadSources(Path.Combine(RepositoryRoot, "src", "Host", "Sunder.App"));
        Assert.DoesNotContain("Sunder.Runtime.Host.Infrastructure.Storage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonPackageSecretsStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalPackageStorageContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeConnectionInfoStore", ReadAppPackageActivationSources(), StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedData.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-tool", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/bin/security", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeHost_IsOnlyProductionOwnerCreatingPersistentPackageStorage()
    {
        var productionRoot = Path.Combine(RepositoryRoot, "src");
        var creators = Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("new LocalPackageStorageContext(", StringComparison.Ordinal)
                    || source.Contains("new JsonPackageSecretsStore(", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .ToArray();

        Assert.NotEmpty(creators);
        Assert.All(creators, path => Assert.StartsWith("src/Host/Sunder.Runtime.Host/", path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sunder.agent", true)]
    [InlineData("Sunder-Agent_1", false)]
    [InlineData("../agent", false)]
    [InlineData("agent/child", false)]
    [InlineData("agent child", false)]
    public void PackageDataValidation_ConstrainsPackageIds(string packageId, bool expected)
        => Assert.Equal(expected, Endpoints.PackageDataInputValidator.IsPackageId(packageId));

    [Theory]
    [InlineData("folder/file.db", true)]
    [InlineData("folder\\file.db", true)]
    [InlineData("../file.db", false)]
    [InlineData("folder/../../file.db", false)]
    [InlineData("/absolute/file.db", false)]
    public void PackageDataValidation_ConstrainsRelativePaths(string path, bool expected)
        => Assert.Equal(expected, Endpoints.PackageDataInputValidator.IsRelativePath(path));

    private static string ReadSources(string root)
        => string.Join('\n', Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

    private static string ReadAppPackageActivationSources()
    {
        var servicesRoot = Path.Combine(RepositoryRoot, "src", "Host", "Sunder.App", "Services");
        return string.Join('\n', Directory.EnumerateFiles(servicesRoot, "AppPackage*.cs")
            .Select(File.ReadAllText));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
    }
}
