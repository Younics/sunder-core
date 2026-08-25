using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Security.Cryptography;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Compatibility;

namespace Sunder.App.Services;

public sealed class PackageArchivePicker(Window owner) : IPackageArchivePicker
{
    internal const long MaxArchiveBytes = SunderPackageFormat.MaxArchiveBytes;

    private static readonly string ReviewSnapshotRootPath = Path.Combine(
        Path.GetTempPath(),
        "sunder-package-review");

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
            return new PackageArchiveSelection(
                snapshotPath,
                review.Sha256,
                DeleteAfterUse: true,
                review.CapabilityConsent);
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
                    null,
                    null);
            }
            return validation.Success
                ? new PackageArchiveReview(
                    LocalPackageInstallConsentViewModel.Valid(displayFileName, validation),
                    validatedHash,
                    CreateCapabilityConsent(validation))
                : new PackageArchiveReview(
                    LocalPackageInstallConsentViewModel.Invalid(displayFileName, validation.Errors),
                    null,
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
                    null,
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
        CancellationToken cancellationToken,
        string? snapshotRootPath = null)
    {
        await using var source = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await CreateReviewSnapshotAsync(
            source,
            source.Length,
            cancellationToken,
            snapshotRootPath);
    }

    internal static async Task<string> CreateReviewSnapshotAsync(
        Stream source,
        long? sourceLength,
        CancellationToken cancellationToken,
        string? snapshotRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rootPath = snapshotRootPath ?? ReviewSnapshotRootPath;
        CleanupStaleReviewSnapshots(rootPath);
        EnsureArchiveLengthWithinLimit(sourceLength);
        var snapshotDirectory = Path.Combine(
            rootPath,
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(snapshotDirectory, "package.sunderpkg");
        try
        {
            Directory.CreateDirectory(snapshotDirectory);
            await using var destination = new FileStream(
                snapshotPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyReviewSnapshotAsync(source, destination, sourceLength, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            return snapshotPath;
        }
        catch
        {
            DeleteReviewSnapshot(snapshotPath);
            throw;
        }
    }

    internal static async Task CopyReviewSnapshotAsync(
        Stream source,
        Stream destination,
        long? sourceLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        EnsureArchiveLengthWithinLimit(sourceLength);

        var buffer = new byte[81920];
        long copiedBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (read > MaxArchiveBytes - copiedBytes)
            {
                throw CreateArchiveTooLargeException();
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copiedBytes += read;
        }
    }

    internal static void CleanupStaleReviewSnapshots(string snapshotRootPath)
    {
        try
        {
            if (!Directory.Exists(snapshotRootPath)) return;
            foreach (var directory in Directory.EnumerateDirectories(snapshotRootPath))
            {
                if (TryReadOwnerProcessId(Path.GetFileName(directory), out var processId)
                    && IsProcessRunning(processId))
                {
                    continue;
                }
                TryDeleteDirectory(directory);
            }
        }
        catch
        {
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
            // A later review pass retries cleanup of snapshots left by terminated processes.
        }
    }

    private static bool TryReadOwnerProcessId(string directoryName, out int processId)
    {
        processId = 0;
        var separator = directoryName.IndexOf('-');
        return separator > 0 && int.TryParse(directoryName[..separator], out processId);
    }

    private static bool IsProcessRunning(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void EnsureArchiveLengthWithinLimit(long? sourceLength)
    {
        if (sourceLength is > MaxArchiveBytes)
        {
            throw CreateArchiveTooLargeException();
        }
    }

    private static InvalidDataException CreateArchiveTooLargeException()
        => new($"Package archive exceeds the {MaxArchiveBytes} byte limit.");

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

    private static PackageCapabilityConsent? CreateCapabilityConsent(
        SunderPackageArchiveValidationResult validation)
    {
        var manifest = validation.Manifest!;
        var capabilities = (manifest.Targets ?? [])
            .Where(static target => target is not null
                                    && string.Equals(target.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
                                    && string.Equals(target.Kind, SunderPackageFormat.WorkerTargetKind, StringComparison.Ordinal))
            .SelectMany(static target => target!.RequiredHostCapabilities ?? [])
            .Where(static capability => string.Equals(
                capability,
                SunderSdkCapabilities.DockerEnvironmentV1,
                StringComparison.Ordinal))
            .Select(static capability => capability!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (capabilities.Length == 0)
        {
            return null;
        }
        var manifestSha256 = validation.ContentIndex!.Files!.Single(entry => string.Equals(
            entry?.Path,
            SunderPackageFormat.ManifestPath,
            StringComparison.Ordinal)).Sha256!;
        return new PackageCapabilityConsent(
            manifest.Id!,
            manifest.Version!,
            manifestSha256,
            capabilities);
    }

    private sealed record PackageArchiveReview(
        LocalPackageInstallConsentViewModel ViewModel,
        string? Sha256,
        PackageCapabilityConsent? CapabilityConsent);
}
