using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class StackExportArchiveBuilder(
    RuntimeContentTransferStore transfers,
    RuntimePackagePaths paths,
    TimeProvider timeProvider,
    RuntimeStackPolicyOptions? policy = null)
{
    private readonly RuntimeStackPolicyOptions _policy = policy ?? new RuntimeStackPolicyOptions();

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
            ValidateFragments(fragments, errors);
            if (errors.Count > 0)
            {
                return new RuntimeStackExportResponse(false, null, warnings, errors);
            }

            var payloadFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long copiedPayloadBytes = 0;
            foreach (var owned in fragments)
            {
                var payloadPath = Path.Combine(stagingPath, "fragments", owned.Fragment.FragmentId + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
                await File.WriteAllTextAsync(payloadPath, owned.Fragment.JsonPayload, cancellationToken);
                payloadFiles[$"payload/fragments/{owned.Fragment.FragmentId}.json"] = payloadPath;
                foreach (var file in owned.Fragment.Files ?? [])
                {
                    var materializedPath = Path.Combine(
                        stagingPath,
                        "payload-sources",
                        owned.Fragment.FragmentId,
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.GetDirectoryName(materializedPath)!);
                    var contentLease = transfers.AcquireRpcContent(
                        file.Content,
                        owned.Provider.PackageId,
                        RuntimeRpcHostCallerActivation.StackPrincipalId,
                        generation);
                    if (contentLease is null)
                    {
                        throw new InvalidDataException(
                            $"Stack payload '{file.RelativePath}' content is stale, exhausted, or unavailable to the Host.");
                    }
                    try
                    {
                        await using var source = new FileStream(
                            contentLease.FilePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read | FileShare.Delete,
                            64 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var destination = new FileStream(
                            materializedPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            64 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        copiedPayloadBytes += await CopyBoundedAsync(
                            source,
                            destination,
                            file.RelativePath,
                            copiedPayloadBytes,
                            cancellationToken);
                        await destination.FlushAsync(cancellationToken);
                    }
                    finally
                    {
                        transfers.ReleaseRpcContent(contentLease);
                    }
                    if (new FileInfo(materializedPath).Length != file.Content.Length)
                    {
                        throw new InvalidDataException(
                            $"Stack payload '{file.RelativePath}' length did not match its declared length.");
                    }
                    payloadFiles[$"payload/files/{owned.Fragment.FragmentId}/{file.RelativePath.Replace('\\', '/')}"] = materializedPath;
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
        catch (InvalidDataException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeStackExportResponse(false, null, warnings, [exception.Message]);
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

    private void ValidateFragments(IReadOnlyList<StackOwnedFragment> fragments, ICollection<string> errors)
    {
        var fragmentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        foreach (var owned in fragments)
        {
            var fragment = owned?.Fragment;
            if (fragment is null)
            {
                errors.Add("A Stack exporter returned a null fragment.");
                continue;
            }
            if (!IsPortableId(fragment.FragmentId))
            {
                errors.Add($"Stack fragment id '{fragment.FragmentId}' must use lowercase ASCII identifiers separated by dot, dash, or underscore.");
                continue;
            }
            if (!fragmentIds.Add(fragment.FragmentId))
            {
                errors.Add($"Stack fragment id '{fragment.FragmentId}' was returned more than once.");
                continue;
            }

            var jsonLength = System.Text.Encoding.UTF8.GetByteCount(fragment.JsonPayload ?? string.Empty);
            if (jsonLength > 4L * 1024 * 1024 || !IsJsonObject(fragment.JsonPayload))
            {
                errors.Add($"Stack fragment '{fragment.FragmentId}' must contain one strict JSON object no larger than 4 MiB.");
            }
            ValidateArchivePath($"payload/fragments/{fragment.FragmentId}.json", archivePaths, errors);

            var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in fragment.Files ?? [])
            {
                if (file is null)
                {
                    errors.Add($"Stack fragment '{fragment.FragmentId}' contains a null payload file.");
                    continue;
                }
                if (!ArchiveRelativePath.TryParse(file.RelativePath, 240, 32, out var relativePath, out var pathError))
                {
                    errors.Add($"Stack fragment '{fragment.FragmentId}' payload path '{file.RelativePath}' is unsafe: {pathError}.");
                    continue;
                }
                if (!relativePaths.Add(relativePath.ToString()))
                {
                    errors.Add($"Stack fragment '{fragment.FragmentId}' payload path '{relativePath}' is duplicated or case-colliding.");
                }
                ValidateArchivePath(
                    $"payload/files/{fragment.FragmentId}/{relativePath}",
                    archivePaths,
                    errors);
                if (file.Content.Length > 0)
                {
                    var length = file.Content.Length;
                    if (length > _policy.MaxExportPayloadFileBytes)
                    {
                        errors.Add($"Stack payload '{file.RelativePath}' exceeds the {_policy.MaxExportPayloadFileBytes} byte per-file limit.");
                    }
                    try
                    {
                        declaredTotal = checked(declaredTotal + length);
                    }
                    catch (OverflowException)
                    {
                        declaredTotal = long.MaxValue;
                    }
                }
            }
        }
        if (declaredTotal > _policy.MaxExportPayloadTotalBytes)
        {
            errors.Add($"Stack payload files exceed the {_policy.MaxExportPayloadTotalBytes} byte aggregate limit.");
        }
    }

    private static bool IsPortableId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                continue;
            }
            if (character is not ('.' or '-' or '_')
                || index == 0
                || index == value.Length - 1
                || !char.IsAsciiLetterOrDigit(value[index - 1])
                || !char.IsAsciiLetterOrDigit(value[index + 1]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsJsonObject(string? value)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(value ?? string.Empty);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static void ValidateArchivePath(
        string value,
        ISet<string> paths,
        ICollection<string> errors)
    {
        if (!ArchiveRelativePath.TryParse(value, 240, 32, out var path, out var error))
        {
            errors.Add($"Stack payload path '{value}' is unsafe: {error}.");
        }
        else if (!paths.Add(path.ToString()))
        {
            errors.Add($"Stack payload path '{value}' is duplicated or case-colliding.");
        }
    }

    private async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        string relativePath,
        long copiedTotal,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long copiedFile = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return copiedFile;
            }
            copiedFile += read;
            if (copiedFile > _policy.MaxExportPayloadFileBytes)
            {
                throw new InvalidDataException(
                    $"Stack payload '{relativePath}' exceeds the {_policy.MaxExportPayloadFileBytes} byte per-file limit.");
            }
            if (copiedTotal + copiedFile > _policy.MaxExportPayloadTotalBytes)
            {
                throw new InvalidDataException(
                    $"Stack payload files exceed the {_policy.MaxExportPayloadTotalBytes} byte aggregate limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
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
            ContributorId = owned.ContributorId,
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

internal sealed record StackOwnedFragment(
    string OwnerPackageId,
    string ContributorId,
    Sunder.Sdk.Rpc.SunderRpcProviderSnapshot Provider,
    StackRpcFragmentExport Fragment);
