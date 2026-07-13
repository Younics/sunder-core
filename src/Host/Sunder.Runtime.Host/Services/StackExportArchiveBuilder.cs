using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class StackExportArchiveBuilder(
    RuntimeContentTransferStore transfers,
    RuntimePackagePaths paths,
    TimeProvider timeProvider)
{
    public async Task<RuntimeStackExportResponse> BuildAsync(
        RuntimeStackExportRequest request,
        IReadOnlyList<StackPackageRequirement> packageRequirements,
        IReadOnlyList<StackOwnedFragment> fragments,
        IReadOnlyDictionary<string, SunderStackFragmentPreview> previews,
        long generation,
        IReadOnlyList<string> contributionWarnings,
        CancellationToken cancellationToken)
    {
        var warnings = contributionWarnings.ToList();
        var errors = new List<string>();
        var stagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "V1", "export", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(stagingPath, "output.sunderstack");
        string? unownedDownload = null;
        var mediaLeases = new List<RuntimeUploadLease>();
        try
        {
            var payloadFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var owned in fragments)
            {
                var payloadPath = Path.Combine(stagingPath, "fragments", owned.Fragment.FragmentId + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
                await File.WriteAllTextAsync(payloadPath, owned.Fragment.JsonPayload, cancellationToken);
                payloadFiles[$"payload/fragments/{owned.Fragment.FragmentId}.json"] = payloadPath;
                foreach (var file in owned.Fragment.Files ?? [])
                {
                    payloadFiles[$"payload/files/{owned.Fragment.FragmentId}/{file.RelativePath.Replace('\\', '/')}"] = file.SourcePath;
                }
            }

            var media = BuildMedia(request.Media ?? [], payloadFiles, mediaLeases, errors, generation);
            if (errors.Count > 0) return new RuntimeStackExportResponse(false, null, warnings, errors);
            var now = timeProvider.GetUtcNow();
            var manifest = new SunderStackManifest
            {
                SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
                MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
                StackId = request.StackId,
                Name = request.Name,
                Summary = request.Summary,
                ReadmeMarkdown = string.IsNullOrWhiteSpace(request.ReadmeMarkdown) ? null : request.ReadmeMarkdown,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Packages = RuntimeStackContractMapper.ToPackageRequirements(packageRequirements),
                Fragments = fragments.Select(owned => ToManifest(owned, previews.GetValueOrDefault(owned.Fragment.FragmentId))).ToArray(),
                Media = media.Count == 0 ? null : media,
            };
            await SunderStackArchiveWriter.WriteAsync(manifest, outputPath, payloadFiles, cancellationToken);
            var validation = await SunderStackArchiveInspector.ExtractAndValidateAsync(outputPath, Path.Combine(stagingPath, "validate"), cancellationToken);
            warnings.AddRange(validation.Warnings);
            if (!validation.Success) return new RuntimeStackExportResponse(false, null, warnings, validation.Errors);

            var downloadPath = Path.Combine(paths.TransferRootPath, Guid.NewGuid().ToString("N") + ".download");
            unownedDownload = downloadPath;
            Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);
            File.Move(outputPath, downloadPath);
            await using var stream = new FileStream(downloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            var download = transfers.RegisterDownload(downloadPath, hash, stream.Length, request.StackId + ".sunderstack", "application/vnd.sunder.stack", generation);
            unownedDownload = null;
            return new RuntimeStackExportResponse(true, download, warnings, []);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeStackExportResponse(false, null, warnings, ["Failed to write Stack archive."]);
        }
        finally
        {
            if (unownedDownload is not null) TryDeleteFile(unownedDownload);
            foreach (var mediaLease in mediaLeases) transfers.ReleaseUpload(mediaLease);
            TryDeleteDirectory(stagingPath);
        }
    }

    private IReadOnlyList<SunderStackMediaManifest> BuildMedia(
        IReadOnlyList<RuntimeStackMediaInput> media,
        IDictionary<string, string> payloadFiles,
        ICollection<RuntimeUploadLease> leases,
        ICollection<string> errors,
        long generation)
    {
        var manifests = new List<SunderStackMediaManifest>();
        var pathsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in media)
        {
            var archivePath = item.ContentPath.Replace('\\', '/');
            var upload = transfers.AcquireUpload(item.UploadId, RuntimeUploadKind.StackMedia, generation, consume: true);
            if (upload is null)
            {
                errors.Add("A Stack media upload was not found or is stale.");
                continue;
            }
            leases.Add(upload);
            if (string.IsNullOrWhiteSpace(archivePath)
                || !archivePath.StartsWith(SunderStackFormat.MediaPayloadRoot, StringComparison.OrdinalIgnoreCase)
                || archivePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == "..")
                || !pathsSeen.Add(archivePath))
            {
                errors.Add($"Stack media content path must be unique and under {SunderStackFormat.MediaPayloadRoot}.");
                continue;
            }
            payloadFiles[archivePath] = upload.FilePath;
            manifests.Add(new SunderStackMediaManifest
            {
                Path = archivePath,
                FileName = string.IsNullOrWhiteSpace(item.FileName) ? upload.FileName : Path.GetFileName(item.FileName),
                ContentType = string.IsNullOrWhiteSpace(item.ContentType) ? "application/octet-stream" : item.ContentType,
                Size = upload.Length,
                AltText = string.IsNullOrWhiteSpace(item.AltText) ? null : item.AltText.Trim(),
                SortOrder = item.SortOrder,
            });
        }
        return manifests;
    }

    private static SunderStackFragmentManifest ToManifest(StackOwnedFragment owned, SunderStackFragmentPreview? preview)
        => new()
        {
            FragmentId = owned.Fragment.FragmentId,
            OwnerPackageId = owned.OwnerPackageId,
            ContributorId = owned.Fragment.ContributorId,
            SchemaId = owned.Fragment.SchemaId,
            SchemaVersion = owned.Fragment.SchemaVersion,
            DisplayName = owned.Fragment.DisplayName,
            Description = owned.Fragment.Description,
            DefaultSelected = owned.Fragment.DefaultSelected,
            PayloadPath = $"payload/fragments/{owned.Fragment.FragmentId}.json",
            RequiredInputs = (owned.Fragment.RequiredInputs ?? []).Select(input => new SunderStackRequiredInputManifest
            {
                InputId = input.InputId,
                Label = input.Label,
                Description = input.Description,
                DefaultValue = input.DefaultValue,
                Required = input.Required,
            }).ToArray(),
            Preview = preview,
        };

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
}

internal sealed record StackOwnedFragment(string OwnerPackageId, StackFragmentExport Fragment);
