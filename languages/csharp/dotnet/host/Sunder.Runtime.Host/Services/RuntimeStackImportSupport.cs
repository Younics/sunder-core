using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeStackImportSupport
{
    public static string? ValidateSelectedFragments(IReadOnlyList<string> requested, IReadOnlyList<string> planned)
    {
        var plannedSet = planned.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = requested
            .Where(id => string.IsNullOrWhiteSpace(id) || !plannedSet.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length > 0)
        {
            return $"Stack import selected unknown fragment id(s): {string.Join(", ", unknown)}.";
        }

        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requestedSet.SetEquals(plannedSet)
            ? null
            : "Stack import fragment selection does not match the previewed plan.";
    }

    public static RuntimeStackImportOutcome GetOutcome(IReadOnlyList<RuntimeStackImportContributorResultDescriptor> results)
    {
        if (results.Count > 0 && results.All(result => result.Outcome == RuntimeStackImportOutcome.Completed))
        {
            return RuntimeStackImportOutcome.Completed;
        }

        return results.Any(result => result.Outcome is RuntimeStackImportOutcome.Completed or RuntimeStackImportOutcome.Partial)
            ? RuntimeStackImportOutcome.Partial
            : RuntimeStackImportOutcome.Failed;
    }

    public static RuntimeStackImportPreviewResponse PreviewFailure(string error)
        => PreviewFailure([], [error]);

    public static RuntimeStackImportPreviewResponse PreviewFailure(
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
        => new(false, null, null, [], [], [], warnings, errors);

    public static RuntimeStackImportResponse ImportFailure(
        string error,
        IReadOnlyDictionary<string, string>? idRemaps = null)
        => new(
            RuntimeStackImportOutcome.Failed,
            [],
            idRemaps ?? new Dictionary<string, string>(),
            [],
            [],
            [error]);

    public static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase));

    public static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    public static string CreatePlanId()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string CreateStagingPath()
        => Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "V1", "runtime", Guid.NewGuid().ToString("N"));

    public static void TryDeleteDirectory(string path)
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
        }
    }
}
