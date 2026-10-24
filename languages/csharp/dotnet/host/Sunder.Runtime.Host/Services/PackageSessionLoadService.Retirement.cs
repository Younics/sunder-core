using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLoadService
{
    internal async Task<ActivePackageSession?> TryRetainOwnerWithdrawalAsync(ActivePackageSession previous,
        IReadOnlyCollection<PackageSessionDevOverlay> overlays, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        token.ThrowIfCancellationRequested();
        var before = previous.GetActivePackageSources();
        var states = previous.GetSessionPackages();
        var desired = overlays.ToDictionary(overlay => overlay.PackageId, StringComparer.Ordinal);
        // Pure subtraction only. Installed catalogs/fallbacks are excluded by the
        // reconciler; App-only, failed/disabled and partially loaded states use the
        // ordinary loader. Nothing can turn absence/uncertainty into readiness.
        if (desired.Count == 0 || desired.Count >= before.Count || before.Count != states.Count
            || before.Count != previous.LoadedPackageMap.Count || before.Any(source => source.Kind != PackageSourceKind.Dev)
            || states.Any(state => !state.IsEnabled || state.Readiness != PackageReadinessState.Ready)
            || desired.Keys.Any(id => before.All(source => source.PackageId != id))) return null;
        var remaining = before.Where(source => desired.ContainsKey(source.PackageId)).ToArray();
        foreach (var source in remaining)
        {
            if (!PathsEqual(source.SourceFolder, desired[source.PackageId].Folder)
                || !previous.LoadedPackageMap.TryGetValue(source.PackageId, out var active)
                || active.ActivationSessionGeneration is null || active.Worker.GenerationCompletion.IsCompleted
                 || AppActivationFingerprint.Create(Guid.Empty, source.PackageId, before, states, role: Sunder.Package.Format.SunderPackageFormat.RuntimeHostRole)
                     != AppActivationFingerprint.Create(Guid.Empty, source.PackageId, remaining, states, role: Sunder.Package.Format.SunderPackageFormat.RuntimeHostRole)) return null;
        }
        var valid = new bool[remaining.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, remaining.Length), new ParallelOptions
        {
            MaxDegreeOfParallelism = _concurrency.MaximumPreparationConcurrency,
            CancellationToken = token,
        }, async (index, cancellation) =>
        {
            var source = remaining[index];
            valid[index] = await _preparer.ValidateRetainedDevelopmentSourceAsync(source,
                previous.LoadedPackageMap[source.PackageId].ExecutionRuntimeBinding, cancellation).ConfigureAwait(false);
        }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (valid.Any(value => !value)) return null;
        PackageCompatibilityAuthority.ValidateGraph(remaining);
        if (remaining.Any(source => previous.LoadedPackageMap[source.PackageId].Worker.GenerationCompletion.IsCompleted
            || !previous.IsPackageEnabled(source.PackageId))) return null;
        var candidate = previous.CreateRetainedRemovalCandidate(remaining);
        _logger.LogInformation("Retained {PackageCount} development packages for owner withdrawal after full source and snapshot validation in {ElapsedMilliseconds} ms",
            remaining.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return candidate;

        static bool PathsEqual(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
