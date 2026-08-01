using System.Runtime.InteropServices;
using System.Text;
using Sunder.Package.Format;
using Sunder.Package.Hosting;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageHostingPolicyTests
{
    [Fact]
    public void ModuleShape_UsesInheritedSdkRolesForBothHosts()
    {
        var shape = PackageModuleShapeReader.Read(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);

        Assert.Empty(shape.Validate());
        Assert.Equal(
            typeof(PackageSessionOverlayTestPackageModule).FullName,
            shape.Resolve(PackageHostRoleMetadataValue.App).TypeName);
        Assert.Equal(
            typeof(PackageSessionOverlayTestPackageModule).FullName,
            shape.Resolve(PackageHostRoleMetadataValue.Runtime).TypeName);
    }

    [Fact]
    public void ModuleShape_RejectsLookalikeModuleInterfacesFromAnotherAssemblyIdentity()
    {
        var root = CreateTempDirectory();
        try
        {
            var assemblyPath = Path.Combine(root, "SpoofedModule.dll");
            var assemblyBytes = File.ReadAllBytes(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
            var expectedIdentity = Encoding.UTF8.GetBytes("Sunder.Sdk");
            var spoofedIdentity = Encoding.UTF8.GetBytes("Spoofr.Sdk");
            var replacements = 0;
            for (var index = 0; index <= assemblyBytes.Length - expectedIdentity.Length; index++)
            {
                if (index + expectedIdentity.Length >= assemblyBytes.Length
                    || assemblyBytes[index + expectedIdentity.Length] != 0
                    || !assemblyBytes.AsSpan(index, expectedIdentity.Length).SequenceEqual(expectedIdentity))
                {
                    continue;
                }
                spoofedIdentity.CopyTo(assemblyBytes, index);
                replacements++;
            }
            Assert.True(replacements > 0);
            File.WriteAllBytes(assemblyPath, assemblyBytes);

            var shape = PackageModuleShapeReader.Read(assemblyPath);

            Assert.Contains(shape.Validate(), error =>
                error.Contains("Module contract", StringComparison.Ordinal)
                && error.Contains("Spoofr.Sdk", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void NativeFallback_SelectsOnlyCurrentRidCandidate()
    {
        var root = CreateTempDirectory();
        try
        {
            var libraryFolder = Path.Combine(root, "lib");
            var currentNative = Path.Combine(
                libraryFolder,
                "runtimes",
                RuntimeInformation.RuntimeIdentifier,
                "native");
            var foreignNative = Path.Combine(libraryFolder, "runtimes", "foreign-x64", "native");
            Directory.CreateDirectory(currentNative);
            Directory.CreateDirectory(foreignNative);
            var fileName = NativeLibraryFallbackResolver.GetPlatformLibraryFileName("sunder_native_test");
            var expectedPath = Path.Combine(currentNative, fileName);
            File.WriteAllText(expectedPath, "current");
            File.WriteAllText(Path.Combine(foreignNative, fileName), "foreign");

            var resolved = NativeLibraryFallbackResolver.Resolve(
                Path.Combine(libraryFolder, "Package.dll"),
                "sunder_native_test",
                RuntimeInformation.RuntimeIdentifier);

            Assert.Equal(expectedPath, resolved);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void NativeFallback_WhenCurrentRidHasDuplicateCandidates_RejectsAmbiguity()
    {
        var root = CreateTempDirectory();
        try
        {
            var libraryFolder = Path.Combine(root, "lib");
            var nativeFolder = Path.Combine(
                libraryFolder,
                "runtimes",
                RuntimeInformation.RuntimeIdentifier,
                "native");
            Directory.CreateDirectory(Path.Combine(nativeFolder, "nested"));
            var fileName = NativeLibraryFallbackResolver.GetPlatformLibraryFileName("sunder_native_test");
            File.WriteAllText(Path.Combine(nativeFolder, fileName), "first");
            File.WriteAllText(Path.Combine(nativeFolder, "nested", fileName), "second");

            var error = Assert.Throws<InvalidOperationException>(() =>
                NativeLibraryFallbackResolver.Resolve(
                    Path.Combine(libraryFolder, "Package.dll"),
                    "sunder_native_test",
                    RuntimeInformation.RuntimeIdentifier));

            Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-hosting-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
