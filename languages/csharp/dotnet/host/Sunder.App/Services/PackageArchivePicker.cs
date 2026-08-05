using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Security.Cryptography;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Package.Format;

namespace Sunder.App.Services;

public sealed class PackageArchivePicker(Window owner) : IPackageArchivePicker
{
    public async Task<PackageArchiveSelection?> PickPackageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel is null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install Sunder Package",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Sunder package")
                {
                    Patterns = ["*.sunderpkg"],
                },
            ],
        });

        cancellationToken.ThrowIfCancellationRequested();
        var packagePath = files.Count == 0 ? null : files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return null;
        }

        var snapshotPath = await CreateReviewSnapshotAsync(packagePath, cancellationToken);
        var retainSnapshot = false;
        try
        {
            var review = await InspectAsync(snapshotPath, Path.GetFileName(packagePath), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new LocalPackageInstallConsentWindow(review.ViewModel);
            if (!await dialog.ShowDialog<bool>(owner) || review.Sha256 is null)
            {
                return null;
            }

            retainSnapshot = true;
            return new PackageArchiveSelection(snapshotPath, review.Sha256, DeleteAfterUse: true);
        }
        finally
        {
            if (!retainSnapshot)
            {
                DeleteReviewSnapshot(snapshotPath);
            }
        }
    }

    private static async Task<PackageArchiveReview> InspectAsync(
        string packagePath,
        string displayFileName,
        CancellationToken cancellationToken)
    {
        var stagingPath = Path.Combine(Path.GetDirectoryName(packagePath)!, "extracted");
        try
        {
            var originalHash = await ComputeSha256Async(packagePath, cancellationToken);
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
                packagePath,
                stagingPath,
                cancellationToken);
            var validatedHash = await ComputeSha256Async(packagePath, cancellationToken);
            if (!string.Equals(originalHash, validatedHash, StringComparison.Ordinal))
            {
                return new PackageArchiveReview(
                    LocalPackageInstallConsentViewModel.Invalid(
                        displayFileName,
                        ["The package archive changed while it was being validated."]),
                    null);
            }
            return validation.Success
                ? new PackageArchiveReview(
                    LocalPackageInstallConsentViewModel.Valid(displayFileName, validation),
                    validatedHash)
                : new PackageArchiveReview(
                    LocalPackageInstallConsentViewModel.Invalid(displayFileName, validation.Errors),
                    null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new PackageArchiveReview(
                LocalPackageInstallConsentViewModel.Invalid(
                    displayFileName,
                    [exception.Message]),
                null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingPath))
                {
                    Directory.Delete(stagingPath, recursive: true);
                }
            }
            catch
            {
                // Runtime performs authoritative validation and cleans its own staging area.
            }
        }
    }

    internal static async Task<string> CreateReviewSnapshotAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        var snapshotDirectory = Path.Combine(
            Path.GetTempPath(),
            "sunder-package-review",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);
        var snapshotPath = Path.Combine(snapshotDirectory, "package.sunderpkg");
        try
        {
            await using var source = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                snapshotPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            return snapshotPath;
        }
        catch
        {
            DeleteReviewSnapshot(snapshotPath);
            throw;
        }
    }

    internal static void DeleteReviewSnapshot(string packagePath)
    {
        try
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
            var directory = Path.GetDirectoryName(packagePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Stale review snapshots remain confined to the operating-system temp directory.
        }
    }

    private static async Task<string> ComputeSha256Async(
        string packagePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private sealed record PackageArchiveReview(
        LocalPackageInstallConsentViewModel ViewModel,
        string? Sha256);
}
