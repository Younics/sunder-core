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
                "sunder_native_test");

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
                    "sunder_native_test"));

            Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void SharedContracts_RemoveUnloadedDirectory_RebindsToRemainingIdenticalCandidate()
    {
        var root = CreateTempDirectory();
        try
        {
            var firstDirectory = CopyRuntimeContracts(root, "first");
            var secondDirectory = CopyRuntimeContracts(root, "second");
            using var registry = new SharedContractAssemblyRegistryCore("Test", []);
            registry.AddProbeDirectories([firstDirectory, secondDirectory]);
            var assemblyName = typeof(PackageHostRoles).Assembly.GetName().Name!;
            var selectedPath = registry.SharedAssemblyPaths[assemblyName];
            var selectedDirectory = Path.GetDirectoryName(selectedPath)!;
            var remainingDirectory = SharedContractAssemblyPolicy.PathsEqual(selectedDirectory, firstDirectory)
                ? secondDirectory
                : firstDirectory;

            var removed = registry.TryRemoveProbeDirectories([selectedDirectory]);

            Assert.True(removed);
            Assert.True(SharedContractAssemblyPolicy.PathsEqual(
                Path.Combine(remainingDirectory, Path.GetFileName(selectedPath)),
                registry.SharedAssemblyPaths[assemblyName]));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void SharedContracts_RemoveLoadedOnlyCandidate_IsRefusedAndMappingIsPreserved()
    {
        var root = CreateTempDirectory();
        try
        {
            var directory = CopyRuntimeContracts(root, "only");
            using var registry = new SharedContractAssemblyRegistryCore("Test", []);
            registry.AddProbeDirectories([directory]);
            var requested = typeof(PackageHostRoles).Assembly.GetName();
            var selectedPath = registry.SharedAssemblyPaths[requested.Name!];
            Assert.NotNull(registry.ResolveSharedAssembly(requested));

            var removed = registry.TryRemoveProbeDirectories([directory]);

            Assert.False(removed);
            Assert.Equal(selectedPath, registry.SharedAssemblyPaths[requested.Name!]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void SharedContracts_FailedCandidateRefresh_RollsBackSelection()
    {
        var root = CreateTempDirectory();
        try
        {
            var firstDirectory = CopyRuntimeContracts(root, "first");
            var conflictingDirectory = CopyRuntimeContracts(root, "conflicting");
            var conflictingPath = Path.Combine(
                conflictingDirectory,
                Path.GetFileName(typeof(PackageHostRoles).Assembly.Location));
            using (var stream = new FileStream(conflictingPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x42);
            }

            using var registry = new SharedContractAssemblyRegistryCore("Test", []);
            registry.AddProbeDirectories([firstDirectory]);
            var assemblyName = typeof(PackageHostRoles).Assembly.GetName().Name!;
            var selectedPath = registry.SharedAssemblyPaths[assemblyName];

            Assert.Throws<InvalidOperationException>(() =>
                registry.AddProbeDirectories([conflictingDirectory]));

            Assert.Equal(selectedPath, registry.SharedAssemblyPaths[assemblyName]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void SharedContracts_LoadedCandidateOverwrittenInPlace_RejectsRefresh()
    {
        var root = CreateTempDirectory();
        try
        {
            var directory = CopyRuntimeContracts(root, "loaded");
            using var registry = new SharedContractAssemblyRegistryCore("Test", []);
            registry.AddProbeDirectories([directory]);
            var requested = typeof(PackageHostRoles).Assembly.GetName();
            Assert.NotNull(registry.ResolveSharedAssembly(requested));
            var path = registry.SharedAssemblyPaths[requested.Name!];
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x42);
            }

            Assert.Throws<InvalidOperationException>(() => registry.AddProbeDirectories([directory]));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void SharedContractPaths_UseOperatingSystemCaseSemantics()
    {
        var root = CreateTempDirectory();
        try
        {
            var upper = Path.Combine(root, "Contracts.dll");
            var lower = Path.Combine(root, "contracts.dll");

            Assert.Equal(
                OperatingSystem.IsWindows(),
                SharedContractAssemblyPolicy.PathsEqual(upper, lower));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CopyRuntimeContracts(string root, string folderName)
    {
        var directory = Path.Combine(root, folderName);
        Directory.CreateDirectory(directory);
        File.Copy(
            typeof(PackageHostRoles).Assembly.Location,
            Path.Combine(directory, Path.GetFileName(typeof(PackageHostRoles).Assembly.Location)));
        return directory;
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
