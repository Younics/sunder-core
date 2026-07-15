using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Tasks;

internal sealed class PackageMetadataDecoder(
    string assemblyPath,
    PackageDependencyExtractor dependencyExtractor,
    PackageCapabilityInference capabilityInference,
    TaskLoggingHelper log,
    IReadOnlyList<string> dependencyPaths)
{
    public PackageManifestMetadata? Decode()
    {
        var loadContext = new PackageMetadataLoadContext(Path.GetDirectoryName(assemblyPath)!, dependencyPaths);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var attributes = assembly.GetCustomAttributesData();
            var packageAttributes = attributes
                .Where(attribute => attribute.AttributeType.FullName == typeof(SunderPackageAttribute).FullName)
                .ToArray();

            if (packageAttributes.Length == 0)
            {
                log.LogError($"Sunder package assembly '{assemblyPath}' must declare one SunderPackage attribute.");
                return null;
            }

            if (packageAttributes.Length > 1)
            {
                log.LogError($"Sunder package assembly '{assemblyPath}' declares multiple SunderPackage attributes.");
                return null;
            }

            var packageAttribute = packageAttributes[0];
            return new PackageManifestMetadata(
                GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Id)) ?? string.Empty,
                GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Name)) ?? string.Empty,
                GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Summary)),
                GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Icon)),
                PackageHostRoleMetadata.ReadManifestRoles(assemblyPath),
                dependencyExtractor.Extract(attributes),
                capabilityInference.Infer(assemblyPath));
        }
        catch (ReflectionTypeLoadException ex)
        {
            log.LogError($"Failed to inspect Sunder package metadata in '{assemblyPath}': {ex.Message}");
            foreach (var loaderException in ex.LoaderExceptions.Where(static exception => exception is not null))
            {
                log.LogError(loaderException!.Message);
            }
            return null;
        }
        catch (Exception ex)
        {
            log.LogErrorFromException(ex, showStackTrace: false);
            return null;
        }
        finally
        {
            loadContext.Unload();
        }
    }

    internal static string? GetNamedString(CustomAttributeData attribute, string name)
        => attribute.NamedArguments.FirstOrDefault(argument => argument.MemberName == name).TypedValue.Value as string;

    private sealed class PackageMetadataLoadContext(
        string assemblyDirectory,
        IReadOnlyList<string> dependencyPaths) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            if (IsSdkSharedAssembly(assemblyName.Name))
            {
                var sharedAssembly = ResolveSdkSharedAssembly(assemblyName);
                if (sharedAssembly is not null)
                {
                    return sharedAssembly;
                }
            }

            var candidatePath = Path.Combine(assemblyDirectory, assemblyName.Name + ".dll");
            if (File.Exists(candidatePath))
            {
                return LoadFromAssemblyPath(candidatePath);
            }

            candidatePath = dependencyPaths.FirstOrDefault(path =>
                ManagedAssemblyPath.IsCandidate(path)
                && File.Exists(path)
                && Path.GetFileNameWithoutExtension(path).Equals(assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(candidatePath))
            {
                return LoadFromAssemblyPath(Path.GetFullPath(candidatePath));
            }

            var taskDirectory = Path.GetDirectoryName(typeof(PackageManifestGenerator).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(taskDirectory))
            {
                candidatePath = Path.Combine(taskDirectory, assemblyName.Name + ".dll");
                if (File.Exists(candidatePath))
                {
                    return LoadFromAssemblyPath(candidatePath);
                }
            }

            return null;
        }

        private static bool IsSdkSharedAssembly(string assemblyName)
            => PackageCapabilityInference.IsSdkContractAssemblyName(assemblyName)
               || assemblyName.Equals("Microsoft.Extensions.DependencyInjection.Abstractions", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Microsoft.Extensions.DependencyInjection", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Microsoft.Extensions.Logging.Abstractions", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Avalonia", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("MicroCom.Runtime", StringComparison.OrdinalIgnoreCase);

        private static Assembly? ResolveSdkSharedAssembly(AssemblyName assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (AssemblyName.ReferenceMatchesDefinition(assemblyName, assembly.GetName()))
                {
                    return assembly;
                }
            }

            var taskDirectory = Path.GetDirectoryName(typeof(PackageManifestGenerator).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(taskDirectory))
            {
                var candidatePath = Path.Combine(taskDirectory, assemblyName.Name + ".dll");
                if (File.Exists(candidatePath))
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidatePath);
                }
            }

            try
            {
                return Assembly.Load(assemblyName);
            }
            catch
            {
                return null;
            }
        }
    }
}
