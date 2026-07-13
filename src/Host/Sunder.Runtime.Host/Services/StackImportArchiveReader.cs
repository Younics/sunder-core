using Sunder.Package.Format;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class StackImportArchiveReader
{
    public async Task<StackFragmentLoadResult> LoadAsync(
        string stackPath,
        IReadOnlyList<string> selectedIds,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var validation = await SunderStackArchiveInspector.ExtractAndValidateAsync(stackPath, stagingPath, cancellationToken);
        if (!validation.Success || validation.Manifest is null)
        {
            return new StackFragmentLoadResult(false, [], [], validation.Warnings, validation.Errors);
        }

        var availableIds = (validation.Manifest.Fragments ?? [])
            .Where(fragment => !string.IsNullOrWhiteSpace(fragment.FragmentId))
            .Select(fragment => fragment.FragmentId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownIds = selectedIds
            .Where(id => string.IsNullOrWhiteSpace(id) || !availableIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownIds.Length > 0)
        {
            return new StackFragmentLoadResult(false, [], [], validation.Warnings, [$"Stack import selected unknown fragment id(s): {string.Join(", ", unknownIds)}."]);
        }

        var selected = selectedIds.Count == 0
            ? (validation.Manifest.Fragments ?? [])
                .Where(fragment => fragment.DefaultSelected != false)
                .Select(fragment => fragment.FragmentId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fragments = new List<StackFragmentImport>();
        foreach (var fragment in validation.Manifest.Fragments ?? [])
        {
            if (string.IsNullOrWhiteSpace(fragment.FragmentId) || !selected.Contains(fragment.FragmentId)) continue;
            var payloadPath = SunderArchive.ResolveFile(stagingPath, ArchiveRelativePath.Parse(fragment.PayloadPath!));
            fragments.Add(new StackFragmentImport(
                fragment.FragmentId,
                fragment.OwnerPackageId!,
                fragment.ContributorId!,
                fragment.SchemaId!,
                fragment.SchemaVersion!.Value,
                fragment.DisplayName!,
                await File.ReadAllTextAsync(payloadPath, cancellationToken),
                fragment.Description,
                ResolveFiles(stagingPath, fragment.FragmentId)));
        }
        return new StackFragmentLoadResult(true, fragments.AsReadOnly(), selected.ToArray(), validation.Warnings, []);
    }

    private static IReadOnlyList<StackImportPayloadFile> ResolveFiles(string stagingPath, string fragmentId)
    {
        var root = Path.Combine(stagingPath, "payload", "files", fragmentId);
        return Directory.Exists(root)
            ? Array.AsReadOnly(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => new StackImportPayloadFile(Path.GetRelativePath(root, path).Replace('\\', '/'), path))
                .ToArray())
            : [];
    }
}

internal sealed record StackFragmentLoadResult(
    bool Success,
    IReadOnlyList<StackFragmentImport> Fragments,
    IReadOnlyList<string> SelectedFragmentIds,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
