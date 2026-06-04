using System.Text.Json;
using Sunder.PackageManagement;

namespace Sunder.App.Services;

public sealed class LocalStackLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _stacksRoot;

    public LocalStackLibraryService(string? stacksRoot = null)
    {
        _stacksRoot = stacksRoot ?? GetDefaultStacksRoot();
    }

    public async Task<IReadOnlyList<LocalStackLibraryItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var index = await LoadIndexAsync(cancellationToken);
        var existing = new List<LocalStackLibraryItem>();
        var changed = false;

        foreach (var item in index.Items)
        {
            var path = ResolveItemPath(item.LocalPath);
            if (!File.Exists(path))
            {
                changed = true;
                continue;
            }

            existing.Add(ResolveStoredItem(item, path));
        }

        if (changed)
        {
            await SaveIndexAsync(new LocalStackLibraryIndex(existing.Select(ToStoredPath).ToArray()), cancellationToken);
        }

        return existing
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<LocalStackLibraryItem> ImportAsync(string stackPath, CancellationToken cancellationToken = default)
        => await StoreAsync(stackPath, expectedStackId: null, preservePublishState: false, cancellationToken);

    public async Task<LocalStackLibraryItem> ReplaceAsync(
        string stackPath,
        string expectedStackId,
        CancellationToken cancellationToken = default)
        => await StoreAsync(stackPath, expectedStackId, preservePublishState: true, cancellationToken);

    private async Task<LocalStackLibraryItem> StoreAsync(
        string stackPath,
        string? expectedStackId,
        bool preservePublishState,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(stackPath))
        {
            throw new FileNotFoundException($"Stack file '{stackPath}' was not found.", stackPath);
        }

        var manifest = await ReadManifestAsync(stackPath, cancellationToken);
        var stackId = manifest.StackId ?? throw new InvalidOperationException("Stack manifest is missing stackId.");
        if (!string.IsNullOrWhiteSpace(expectedStackId) && !string.Equals(stackId, expectedStackId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Edited Stack id '{stackId}' does not match local Stack '{expectedStackId}'.");
        }

        var index = await LoadIndexAsync(cancellationToken);
        var existing = index.Items.FirstOrDefault(item => string.Equals(item.StackId, stackId, StringComparison.OrdinalIgnoreCase));
        var destinationPath = Path.Combine(GetLocalStacksDirectory(), SanitizeFileName(stackId) + ".sunderstack");
        var temporaryDestinationPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(stackPath, temporaryDestinationPath, overwrite: false);

        try
        {
            await ReadManifestAsync(temporaryDestinationPath, cancellationToken);
            File.Move(temporaryDestinationPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryDestinationPath);
        }

        var now = DateTimeOffset.UtcNow;
        var details = BuildDetailsFromManifest(manifest);
        var media = await ExtractMediaAsync(destinationPath, stackId, manifest.Media ?? [], cancellationToken);
        var item = new LocalStackLibraryItem(
            stackId,
            manifest.Name ?? stackId,
            manifest.Summary,
            destinationPath,
            manifest.CreatedAtUtc ?? now,
            manifest.UpdatedAtUtc ?? now,
            PackageCount: manifest.Packages?.Count ?? 0,
            FragmentCount: manifest.Fragments?.Count ?? 0,
            RegistryUrl: preservePublishState ? existing?.RegistryUrl : null,
            PublishedStackId: preservePublishState ? existing?.PublishedStackId : null,
            PublishedAtUtc: preservePublishState ? existing?.PublishedAtUtc : null,
            PublishedUpdatedAtUtc: preservePublishState ? existing?.PublishedUpdatedAtUtc : null,
            Details: details.Count == 0 ? null : details,
            ReadmeMarkdown: manifest.ReadmeMarkdown,
            Media: media.Count == 0 ? null : media);

        var items = index.Items
            .Where(item => !string.Equals(item.StackId, stackId, StringComparison.OrdinalIgnoreCase))
            .Append(ToStoredPath(item))
            .ToArray();
        await SaveIndexAsync(new LocalStackLibraryIndex(items), cancellationToken);
        return item;
    }

    public async Task UpdateDetailsAsync(
        string stackId,
        IReadOnlyList<LocalStackDetailPackage> details,
        CancellationToken cancellationToken = default)
    {
        var index = await LoadIndexAsync(cancellationToken);
        var items = index.Items
            .Select(item => string.Equals(item.StackId, stackId, StringComparison.OrdinalIgnoreCase)
                ? item with { Details = details }
                : item)
            .ToArray();
        await SaveIndexAsync(new LocalStackLibraryIndex(items), cancellationToken);
    }

    public async Task UpdatePublishStateAsync(
        string stackId,
        string registryUrl,
        string publishedStackId,
        DateTimeOffset publishedAtUtc,
        DateTimeOffset publishedUpdatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var index = await LoadIndexAsync(cancellationToken);
        var items = index.Items
            .Select(item => string.Equals(item.StackId, stackId, StringComparison.OrdinalIgnoreCase)
                ? item with
                {
                    RegistryUrl = registryUrl,
                    PublishedStackId = publishedStackId,
                    PublishedAtUtc = publishedAtUtc,
                    PublishedUpdatedAtUtc = publishedUpdatedAtUtc,
                }
                : item)
            .ToArray();
        await SaveIndexAsync(new LocalStackLibraryIndex(items), cancellationToken);
    }

    public async Task ClearPublishStateAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var index = await LoadIndexAsync(cancellationToken);
        var items = index.Items
            .Select(item => string.Equals(item.StackId, stackId, StringComparison.OrdinalIgnoreCase)
                ? item with
                {
                    RegistryUrl = null,
                    PublishedStackId = null,
                    PublishedAtUtc = null,
                    PublishedUpdatedAtUtc = null,
                }
                : item)
            .ToArray();
        await SaveIndexAsync(new LocalStackLibraryIndex(items), cancellationToken);
    }

    public async Task ExportAsync(LocalStackLibraryItem item, string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = ResolveItemPath(item.LocalPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Local Stack file '{sourcePath}' was not found.", sourcePath);
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
        await Task.CompletedTask;
    }

    public async Task DeleteAsync(LocalStackLibraryItem item, CancellationToken cancellationToken = default)
    {
        var index = await LoadIndexAsync(cancellationToken);
        var items = index.Items
            .Where(existing => !string.Equals(existing.StackId, item.StackId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        await SaveIndexAsync(new LocalStackLibraryIndex(items), cancellationToken);

        var path = ResolveItemPath(item.LocalPath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public async Task<SunderStackManifest> ReadManifestAsync(string stackPath, CancellationToken cancellationToken = default)
    {
        var stagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "inspect", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(stackPath, stagingPath, cancellationToken);
            if (!result.Success || result.Manifest is null)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, result.Errors.DefaultIfEmpty("Stack validation failed.")));
            }

            return result.Manifest;
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private async Task<LocalStackLibraryIndex> LoadIndexAsync(CancellationToken cancellationToken)
    {
        var path = GetIndexPath();
        if (!File.Exists(path))
        {
            return new LocalStackLibraryIndex([]);
        }

        try
        {
            return JsonSerializer.Deserialize<LocalStackLibraryIndex>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
                   ?? new LocalStackLibraryIndex([]);
        }
        catch
        {
            return new LocalStackLibraryIndex([]);
        }
    }

    private async Task SaveIndexAsync(LocalStackLibraryIndex index, CancellationToken cancellationToken)
    {
        var path = GetIndexPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(index, JsonOptions), cancellationToken);
    }

    private LocalStackLibraryItem ToStoredPath(LocalStackLibraryItem item)
        => item with
        {
            LocalPath = Path.GetRelativePath(_stacksRoot, item.LocalPath).Replace('\\', '/'),
            Media = item.Media?
                .Select(media => media with { LocalPath = Path.GetRelativePath(_stacksRoot, media.LocalPath).Replace('\\', '/') })
                .ToArray(),
        };

    private LocalStackLibraryItem ResolveStoredItem(LocalStackLibraryItem item, string localPath)
        => item with
        {
            LocalPath = localPath,
            Media = item.Media?
                .Select(media => media with { LocalPath = ResolveItemPath(media.LocalPath) })
                .Where(media => File.Exists(media.LocalPath))
                .ToArray(),
        };

    private string ResolveItemPath(string path)
        => Path.IsPathRooted(path)
            ? path
            : Path.Combine(_stacksRoot, path.Replace('/', Path.DirectorySeparatorChar));

    private string GetIndexPath()
        => Path.Combine(_stacksRoot, "local-stacks.json");

    private string GetLocalStacksDirectory()
        => Path.Combine(_stacksRoot, "local");

    private string GetLocalMediaDirectory(string stackId)
        => Path.Combine(_stacksRoot, "media", SanitizeFileName(stackId));

    private static string GetDefaultStacksRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sunder", "stacks");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Sunder",
                "stacks");
        }

        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configRoot, "sunder", "stacks");
    }

    public static IReadOnlyList<LocalStackDetailPackage> BuildDetailsFromManifest(SunderStackManifest manifest)
        => (manifest.Fragments ?? [])
            .Where(fragment => (fragment.Preview?.DisplayDetails ?? []).Any(detail => !string.IsNullOrWhiteSpace(detail.Label) && !string.IsNullOrWhiteSpace(detail.Value)))
            .GroupBy(fragment => fragment.OwnerPackageId ?? "unknown package", StringComparer.OrdinalIgnoreCase)
            .Select(group => new LocalStackDetailPackage(
                group.Key,
                group.Key,
                BuildPackageGlyph(group.Key),
                group.Select(fragment => new LocalStackDetailItem(
                        fragment.FragmentId ?? fragment.Preview?.SourceItemId ?? fragment.DisplayName ?? "setup-item",
                        fragment.DisplayName ?? fragment.FragmentId ?? "Setup item",
                        BuildFragmentSummary(fragment),
                        (fragment.Preview?.DisplayDetails ?? [])
                            .Where(detail => !string.IsNullOrWhiteSpace(detail.Label) && !string.IsNullOrWhiteSpace(detail.Value))
                            .Select(detail => new LocalStackDetailValue(
                                detail.Label!,
                                detail.Value!,
                                string.IsNullOrWhiteSpace(detail.Behavior) ? "Include value" : detail.Behavior!))
                            .ToArray(),
                        string.IsNullOrWhiteSpace(fragment.Preview?.Kind) ? null : fragment.Preview.Kind))
                    .ToArray()))
            .ToArray();

    private async Task<IReadOnlyList<LocalStackMedia>> ExtractMediaAsync(
        string stackPath,
        string stackId,
        IReadOnlyList<SunderStackMediaManifest> media,
        CancellationToken cancellationToken)
    {
        var mediaDirectory = GetLocalMediaDirectory(stackId);
        TryDeleteDirectory(mediaDirectory);
        if (media.Count == 0)
        {
            return [];
        }

        var stagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "media", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(stackPath, stagingPath, cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, result.Errors.DefaultIfEmpty("Stack validation failed.")));
            }

            Directory.CreateDirectory(mediaDirectory);
            var copied = new List<LocalStackMedia>();
            foreach (var item in media.OrderBy(item => item.SortOrder ?? 0))
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                {
                    continue;
                }

                var sourcePath = Path.Combine(stagingPath, item.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(item.FileName) ? Path.GetFileName(item.Path) : item.FileName!);
                var destinationPath = Path.Combine(mediaDirectory, $"{copied.Count:D3}-{fileName}");
                File.Copy(sourcePath, destinationPath, overwrite: true);
                copied.Add(new LocalStackMedia(
                    item.Path,
                    item.FileName ?? fileName,
                    item.ContentType ?? "application/octet-stream",
                    item.Size ?? new FileInfo(destinationPath).Length,
                    item.AltText,
                    item.SortOrder ?? copied.Count,
                    destinationPath));
            }

            return copied;
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private static string BuildFragmentSummary(SunderStackFragmentManifest fragment)
    {
        var detailLabels = (fragment.Preview?.DisplayDetails ?? [])
            .Select(detail => detail.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Take(3)
            .ToArray();
        if (detailLabels.Length > 0)
        {
            return string.Join(", ", detailLabels);
        }

        var schema = string.IsNullOrWhiteSpace(fragment.SchemaId) ? null : fragment.SchemaId;
        var owner = string.IsNullOrWhiteSpace(fragment.OwnerPackageId) ? null : fragment.OwnerPackageId;
        return string.Join(" - ", new[] { schema, owner }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildPackageGlyph(string packageId)
    {
        var first = packageId.FirstOrDefault(char.IsLetterOrDigit);
        return first == default ? "?" : char.ToUpperInvariant(first).ToString();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for temporary Stack inspection.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup for temporary Stack replacement.
        }
    }
}

public sealed record LocalStackLibraryIndex(IReadOnlyList<LocalStackLibraryItem> Items);

public sealed record LocalStackLibraryItem(
    string StackId,
    string Name,
    string? Summary,
    string LocalPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int PackageCount,
    int FragmentCount,
    string? RegistryUrl,
    string? PublishedStackId,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? PublishedUpdatedAtUtc,
    IReadOnlyList<LocalStackDetailPackage>? Details = null,
    string? ReadmeMarkdown = null,
    IReadOnlyList<LocalStackMedia>? Media = null);

public sealed record LocalStackMedia(
    string ArchivePath,
    string FileName,
    string ContentType,
    long Size,
    string? AltText,
    int SortOrder,
    string LocalPath);

public sealed record LocalStackDetailPackage(
    string PackageId,
    string DisplayName,
    string Glyph,
    IReadOnlyList<LocalStackDetailItem> Items,
    string? IconAssetPath = null);

public sealed record LocalStackDetailItem(
    string ItemId,
    string DisplayName,
    string Summary,
    IReadOnlyList<LocalStackDetailValue> Values,
    string? Kind = null);

public sealed record LocalStackDetailValue(
    string Label,
    string Value,
    string Behavior);
