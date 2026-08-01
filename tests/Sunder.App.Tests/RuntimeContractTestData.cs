using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Tests;

internal static class RuntimeContractTestData
{
    private static readonly ConcurrentDictionary<string, byte[]> SnapshotContent = new(StringComparer.Ordinal);

    public static PackageUiSnapshotDescriptor Snapshot(
        string packageId,
        PackageSourceKind sourceKind,
        string revision)
    {
        var isFolder = Directory.Exists(revision);
        var content = isFolder ? CreateSnapshotArchive(revision) : Encoding.UTF8.GetBytes(revision);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var snapshotId = hash[..32];
        SnapshotContent[snapshotId] = content;
        return new PackageUiSnapshotDescriptor(
            packageId,
            sourceKind,
            1,
            isFolder ? ReadAppTarget(revision) : AppTarget(),
            hash,
            snapshotId,
            $"packages/ui-snapshots/{snapshotId}");
    }

    public static PackageTargetDescriptor AppTarget(string entryPoint = "lib/test.dll", string? rid = null)
        => new(
            "app",
            rid ?? RuntimeInformation.RuntimeIdentifier,
            "avalonia",
            entryPoint,
            "net10.0",
            "1.1.0",
            ["sdk-baseline-1-1.v1", "core.v1"]);

    public static string CreateAppManifestJson(
        string packageId,
        string entryPoint,
        IReadOnlyList<string>? runtimeIdentifiers = null)
        => JsonSerializer.Serialize(new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = packageId,
            Name = packageId,
            Version = "1.0.0",
            DependsOn = [],
            Targets = (runtimeIdentifiers ?? [RuntimeInformation.RuntimeIdentifier])
                .Select(rid => new SunderPackageTargetManifest
                {
                    Role = SunderPackageFormat.AppHostRole,
                    Rid = rid,
                    Kind = SunderPackageFormat.AvaloniaTargetKind,
                    EntryPoint = entryPoint,
                    TargetFramework = "net10.0",
                    SdkVersion = "1.1.0",
                    RequiredHostCapabilities = ["sdk-baseline-1-1.v1", "core.v1"],
                })
                .Cast<SunderPackageTargetManifest?>()
                .ToArray(),
        });

    public static void WriteAppProjectionManifest(string root, string packageId, string entryPoint)
    {
        var manifestFolder = Path.Combine(root, "manifest");
        Directory.CreateDirectory(manifestFolder);
        File.WriteAllText(
            Path.Combine(manifestFolder, "sunder-package.json"),
            CreateAppManifestJson(packageId, entryPoint));
    }

    public static async Task DownloadSnapshotAsync(
        PackageUiSnapshotDescriptor snapshot,
        Stream destination,
        CancellationToken cancellationToken)
    {
        if (!SnapshotContent.TryGetValue(snapshot.SnapshotId, out var content))
        {
            throw new InvalidOperationException($"Test snapshot '{snapshot.SnapshotId}' is unavailable.");
        }

        await destination.WriteAsync(content, cancellationToken);
    }

    private static byte[] CreateSnapshotArchive(string folder)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(folder, file).Replace('\\', '/'));
                using var input = File.OpenRead(file);
                using var destination = entry.Open();
                input.CopyTo(destination);
            }
        }

        return output.ToArray();
    }

    private static PackageTargetDescriptor ReadAppTarget(string folder)
    {
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(
                           File.ReadAllText(Path.Combine(folder, "manifest", "sunder-package.json")))
                       ?? throw new InvalidDataException("Test App package manifest could not be read.");
        var target = manifest.Targets?.Single(candidate =>
            candidate is not null
            && string.Equals(candidate.Role, SunderPackageFormat.AppHostRole, StringComparison.Ordinal)
            && string.Equals(candidate.Rid, RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Test App package manifest does not declare the current App target.");
        return new PackageTargetDescriptor(
            target.Role!,
            target.Rid!,
            target.Kind!,
            target.EntryPoint!,
            target.TargetFramework,
            target.SdkVersion,
            target.RequiredHostCapabilities?.Select(value => value!).ToArray() ?? []);
    }
}
