using Sunder.Protocol;

namespace Sunder.App.Services;

internal sealed class AppPackageSourceLoader(AppPackageSourcePreparer sourcePreparer)
{
    public async Task<AppPackageSourceLoadResult> LoadAsync(
        ActivePackageDescriptor package,
        PackageSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preparedSource = await Task.Run(
            () => sourcePreparer.Prepare(source),
            cancellationToken);
        if (preparedSource is null)
        {
            return AppPackageSourceLoadResult.Failure($"Failed to prepare app-side package source '{source.Folder}'.");
        }

        if (!string.Equals(preparedSource.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            AppPackageSourcePreparer.TryDeleteDirectory(preparedSource.Folder);
            return AppPackageSourceLoadResult.Failure(
                $"Runtime package source '{source.Folder}' resolved to package '{preparedSource.PackageId}'.");
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
