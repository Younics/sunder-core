using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageSourceLoader(
    AppPackageSourcePreparer sourcePreparer,
    Func<PackageUiSnapshotDescriptor, Stream, CancellationToken, Task> downloadSnapshotAsync)
{
    public async Task<AppPackageSourceLoadResult> LoadAsync(
        ActivePackageDescriptor package,
        PackageUiSnapshotDescriptor source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preparedSource = await sourcePreparer.PrepareAsync(source, downloadSnapshotAsync, cancellationToken);
        if (preparedSource is null)
        {
            return AppPackageSourceLoadResult.Failure($"Failed to materialize package UI snapshot '{source.SnapshotId}'.");
        }

        if (!string.Equals(preparedSource.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            AppPackageSourcePreparer.TryDeleteDirectory(preparedSource.Folder);
            return AppPackageSourceLoadResult.Failure(
                $"Package UI snapshot '{source.SnapshotId}' resolved to package '{preparedSource.PackageId}'.");
        }

        return AppPackageSourceLoadResult.Success(preparedSource);
    }
}

internal sealed record AppPackageSourceLoadResult(AppPreparedPackageSource? PreparedSource, string? FailureMessage)
{
    public bool IsSuccess => PreparedSource is not null && FailureMessage is null;

    public static AppPackageSourceLoadResult Success(AppPreparedPackageSource preparedSource)
        => new(preparedSource, FailureMessage: null);

    public static AppPackageSourceLoadResult Failure(string failureMessage)
        => new(PreparedSource: null, failureMessage);
}
