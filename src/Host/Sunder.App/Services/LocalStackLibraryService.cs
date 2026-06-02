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

            existing.Add(item with { LocalPath = path });
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
    {
        if (!File.Exists(stackPath))
        {
            throw new FileNotFoundException($"Stack file '{stackPath}' was not found.", stackPath);
        }

        var manifest = await ReadManifestAsync(stackPath, cancellationToken);
        var stackId = manifest.StackId ?? throw new InvalidOperationException("Stack manifest is missing stackId.");
        var destinationPath = Path.Combine(GetLocalStacksDirectory(), SanitizeFileName(stackId) + ".sunderstack");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(stackPath, destinationPath, overwrite: true);

        var now = DateTimeOffset.UtcNow;
        var details = BuildDetailsFromManifest(manifest);
        var item = new LocalStackLibraryItem(
            stackId,
            manifest.Name ?? stackId,
            manifest.Summary,
            destinationPath,
            manifest.CreatedAtUtc ?? now,
            manifest.UpdatedAtUtc ?? now,
            PackageCount: manifest.Packages?.Count ?? 0,
            FragmentCount: manifest.Fragments?.Count ?? 0,
            RegistryUrl: null,
            PublishedStackId: null,
            PublishedAtUtc: null,
            PublishedUpdatedAtUtc: null,
            Details: details.Count == 0 ? null : details);

        var index = await LoadIndexAsync(cancellationToken);
        var items = index.Items
            .Where(existing => !string.Equals(existing.StackId, stackId, StringComparison.OrdinalIgnoreCase))
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
        => item with { LocalPath = Path.GetRelativePath(_stacksRoot, item.LocalPath).Replace('\\', '/') };

    private string ResolveItemPath(string path)
        => Path.IsPathRooted(path)
            ? path
            : Path.Combine(_stacksRoot, path.Replace('/', Path.DirectorySeparatorChar));

    private string GetIndexPath()
        => Path.Combine(_stacksRoot, "local-stacks.json");

    private string GetLocalStacksDirectory()
        => Path.Combine(_stacksRoot, "local");

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
            .Where(fragment => (fragment.DisplayDetails ?? []).Any(detail => !string.IsNullOrWhiteSpace(detail.Label) && !string.IsNullOrWhiteSpace(detail.Value)))
            .GroupBy(fragment => fragment.OwnerPackageId ?? "unknown package", StringComparer.OrdinalIgnoreCase)
            .Select(group => new LocalStackDetailPackage(
                group.Key,
                group.Key,
                BuildPackageGlyph(group.Key),
                group.Select(fragment => new LocalStackDetailItem(
                        fragment.FragmentId ?? fragment.SourceItemId ?? fragment.DisplayName ?? "setup-item",
                        fragment.DisplayName ?? fragment.FragmentId ?? "Setup item",
                        BuildFragmentSummary(fragment),
                        (fragment.DisplayDetails ?? [])
                            .Where(detail => !string.IsNullOrWhiteSpace(detail.Label) && !string.IsNullOrWhiteSpace(detail.Value))
                            .Select(detail => new LocalStackDetailValue(
                                detail.Label!,
                                detail.Value!,
                                string.IsNullOrWhiteSpace(detail.Behavior) ? "Include value" : detail.Behavior!))
                            .ToArray(),
                        string.IsNullOrWhiteSpace(fragment.Kind) ? null : fragment.Kind))
                    .ToArray()))
            .ToArray();

    private static string BuildFragmentSummary(SunderStackFragmentManifest fragment)
    {
        var detailLabels = (fragment.DisplayDetails ?? [])
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
    IReadOnlyList<LocalStackDetailPackage>? Details = null);

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
