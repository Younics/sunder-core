using System.Collections;

namespace Sunder.App.Services;

internal sealed class AppPackageQuarantineRetryIntent : IReadOnlyCollection<string>
{
    private readonly string[] _packageIds;

    private AppPackageQuarantineRetryIntent(
        IEnumerable<string> packageIds,
        bool preserveKnownViewQuarantines)
    {
        _packageIds = packageIds
            .Where(static packageId => !string.IsNullOrWhiteSpace(packageId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PreserveKnownViewQuarantines = preserveKnownViewQuarantines;
    }

    public int Count => _packageIds.Length;

    public bool PreserveKnownViewQuarantines { get; }

    public static AppPackageQuarantineRetryIntent ForAutomaticPackageRecovery(string packageId)
        => new([packageId], preserveKnownViewQuarantines: true);

    public static AppPackageQuarantineRetryIntent ForAutomaticPackageRecovery(
        IEnumerable<string> packageIds)
        => new(packageIds, preserveKnownViewQuarantines: true);

    public AppPackageQuarantineRetryIntent Snapshot()
        => new(_packageIds, PreserveKnownViewQuarantines);

    public AppPackageQuarantineRetryIntent WithPackageIds(IEnumerable<string> packageIds)
        => new(_packageIds.Concat(packageIds), PreserveKnownViewQuarantines);

    public IEnumerator<string> GetEnumerator()
        => ((IEnumerable<string>)_packageIds).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
