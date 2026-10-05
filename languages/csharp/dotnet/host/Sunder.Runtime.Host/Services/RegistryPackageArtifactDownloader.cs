using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackageArtifactDownloader(
    RegistryHttpClient registryClient,
    RuntimeContentTransferStore transfers,
    PackageSessionLifecycleService packageSessions,
    RuntimeTransportPolicyOptions policy,
    TimeProvider timeProvider)
{
    private const long MaxArtifactBytes = RuntimeContentTransferStore.MaxPackageUploadBytes;
    private const long MaxAggregateProjectionBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<ContentUploadDescriptor> DownloadAsync(
        Uri origin,
        RegistryPackageInstallPlanItem item,
        IReadOnlyList<RegistryPackageTargetRequest> selectedTargets,
        IReadOnlyList<string> trustedArtifactOrigins,
        CancellationToken cancellationToken)
    {
        ValidateArtifactInventory(
            item,
            selectedTargets,
            PackageTargetSelection.GetCurrentRuntimeIdentifier());
        var allowedOrigins = BuildAllowedOrigins(origin, trustedArtifactOrigins);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Sunder.Registry.Projections",
            Guid.NewGuid().ToString("N"));
        var projectionUploadIds = new List<string>(item.Artifacts.Count);
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var projections = new List<SunderPackageProjectionArchiveValidationResult>(item.Artifacts.Count);
            long aggregateBytes = 0;
            for (var index = 0; index < item.Artifacts.Count; index++)
            {
                var artifact = item.Artifacts[index];
                var upload = await DownloadProjectionAsync(
                    origin,
                    allowedOrigins,
                    item,
                    artifact,
                    cancellationToken);
                projectionUploadIds.Add(upload.UploadId);
                if (upload.Length > MaxAggregateProjectionBytes - aggregateBytes)
                {
                    throw new RegistryArtifactTooLargeException(
                        $"Package '{item.PackageId}' projections exceed the {MaxAggregateProjectionBytes} byte aggregate limit.");
                }
                aggregateBytes += upload.Length;

                var lease = transfers.AcquireUpload(
                    upload.UploadId,
                    RuntimeUploadKind.Package,
                    packageSessions.Generation,
                    consume: false)
                    ?? throw new InvalidDataException($"Downloaded projection for package '{item.PackageId}' expired before inspection.");
                var inspectionPath = Path.Combine(temporaryRoot, $"projection-{index:D2}");
                var inspection = await SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(
                    lease.FilePath,
                    inspectionPath,
                    cancellationToken);
                ValidateProjection(item, artifact, inspection);
                projections.Add(inspection);
            }

            ValidateProjectionSourceIdentity(item, projections);
            ValidatePlanTargets(item, projections[0].Manifest!);
            var composedPath = await ComposePackageAsync(
                temporaryRoot,
                item,
                projections,
                cancellationToken);
            await using var source = File.OpenRead(composedPath);
            return await transfers.CreateUploadAsync(
                RuntimeUploadKind.Package,
                source,
                source.Length,
                expectedHash: null,
                $"{item.PackageId}.{item.Version}.sunderpkg",
                "application/vnd.sunder.package",
                packageSessions.Generation,
                cancellationToken);
        }
        finally
        {
            foreach (var uploadId in projectionUploadIds)
            {
                transfers.DiscardUpload(uploadId);
            }
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private async Task<ContentUploadDescriptor> DownloadProjectionAsync(
        Uri registryOrigin,
        IReadOnlySet<string> allowedOrigins,
        RegistryPackageInstallPlanItem item,
        RegistryPackageProjectionArtifact artifact,
        CancellationToken cancellationToken)
    {
        var artifactUri = ValidateArtifactUri(registryOrigin, artifact.DownloadUrl, allowedOrigins);
        if (artifact.Size < 0 || artifact.Size > MaxArtifactBytes)
        {
            throw new RegistryArtifactTooLargeException(
                $"Package '{item.PackageId}' projection '{ProjectionKey(artifact)}' exceeds the {MaxArtifactBytes} byte download limit.");
        }
        using var timeout = new CancellationTokenSource(policy.RegistryRequestTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await registryClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, artifactUri),
                linked.Token);
            ValidateArtifactUri(registryOrigin, response.RequestMessage?.RequestUri, allowedOrigins);
            await registryClient.EnsureSuccessAsync(response, linked.Token);
            if (response.Content.Headers.ContentLength is > MaxArtifactBytes)
            {
                throw new RegistryArtifactTooLargeException(
                    $"Package '{item.PackageId}' projection '{ProjectionKey(artifact)}' exceeds the {MaxArtifactBytes} byte download limit.");
            }
            if (response.Content.Headers.ContentLength is { } contentLength
                && contentLength != artifact.Size)
            {
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' projection '{ProjectionKey(artifact)}' has an invalid content length.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(linked.Token);
            await using var bounded = new BoundedReadStream(
                source,
                artifact.Size,
                $"Package '{item.PackageId}' projection '{ProjectionKey(artifact)}' exceeds its declared {artifact.Size} byte size.");
            var upload = await transfers.CreateUploadAsync(
                RuntimeUploadKind.Package,
                bounded,
                response.Content.Headers.ContentLength,
                artifact.Sha256,
                $"{item.PackageId}.{item.Version}.{artifact.Kind}.{artifact.Rid ?? "none"}.sunderpkg",
                "application/vnd.sunder.package",
                packageSessions.Generation,
                linked.Token);
            if (upload.Length != artifact.Size)
            {
                transfers.DiscardUpload(upload.UploadId);
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' projection '{ProjectionKey(artifact)}' size verification failed.");
            }
            return upload;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Registry artifact transfer exceeded the {policy.RegistryRequestTimeout} timeout.",
                exception);
        }
    }

    private static async Task<string> ComposePackageAsync(
        string temporaryRoot,
        RegistryPackageInstallPlanItem item,
        IReadOnlyList<SunderPackageProjectionArchiveValidationResult> projections,
        CancellationToken cancellationToken)
    {
        var sourceManifest = projections[0].Manifest!;
        // Keep the complete source graph. The private installation handoff carries
        // acquired targets separately; this is not a new publishable full archive.
        PackageManifestAdmissionPolicy.EnsureSupported(sourceManifest);
        var selectedTargets = item.Artifacts
            .Where(artifact => artifact.Rid is not null)
            .Select(artifact => (Role: artifact.Kind, Rid: artifact.Rid!))
            .ToHashSet();

        var sourcePath = Path.Combine(temporaryRoot, "composed-source");
        Directory.CreateDirectory(sourcePath);
        foreach (var projection in projections)
        {
            foreach (var payloadPath in projection.ExtractedProjection!.PayloadPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = payloadPath.ToPlatformPath(projection.ExtractedProjection.RootPath);
                var destination = payloadPath.ToPlatformPath(sourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    if (!await FilesEqualAsync(source, destination, cancellationToken))
                    {
                        throw new InvalidDataException(
                            $"Package '{item.PackageId}' projections disagree on shared payload '{payloadPath}'.");
                    }
                    continue;
                }
                File.Copy(source, destination, overwrite: false);
            }
        }

        var manifestPath = Path.Combine(sourcePath, "manifest", "sunder-package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.Copy(ArchiveRelativePath.Parse(SunderPackageFormat.ManifestPath).ToPlatformPath(projections[0].ExtractedProjection!.RootPath), manifestPath);
        await using (var manifestStream = File.OpenRead(manifestPath))
        {
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(manifestStream, cancellationToken)) != item.Artifacts[0].ManifestSha256)
                throw new InvalidDataException("Projection source manifest changed before composition.");
        }
        var contentIndexPath = Path.Combine(sourcePath, "manifest", "content-index.json");
        var entries = new List<SunderPackageContentIndexEntry>();
        foreach (var path in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                     .Where(path => !PathsEqual(path, contentIndexPath))
                     .OrderBy(path => Path.GetRelativePath(sourcePath, path), StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(path);
            entries.Add(new SunderPackageContentIndexEntry(
                Path.GetRelativePath(sourcePath, path).Replace(Path.DirectorySeparatorChar, '/'),
                Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant(),
                stream.Length));
        }
        await File.WriteAllTextAsync(
            contentIndexPath,
            JsonSerializer.Serialize(
                new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, entries),
                JsonOptions),
            cancellationToken);

        var validation = await SunderPackageArchiveInspector.ValidateExtractedProjectionSetAsync(
            sourcePath, selectedTargets.Select(target => new SunderPackageTargetKey(target.Role, target.Rid)).ToArray(), cancellationToken);
        if (!validation.Success)
        {
            throw new InvalidDataException(
                $"Package '{item.PackageId}' projection composition failed validation: {string.Join(" | ", validation.Errors)}");
        }

        var archivePath = Path.Combine(temporaryRoot, "composed.sunderpkg");
        await SunderArchive.WriteDeterministicAsync(
            sourcePath,
            archivePath,
            SunderArchiveExtractionOptions.Default with { CancellationToken = cancellationToken });
        return archivePath;
    }

    private static void ValidateArtifactInventory(
        RegistryPackageInstallPlanItem item,
        IReadOnlyList<RegistryPackageTargetRequest> selectedTargets,
        string currentRid)
    {
        if (item.Targets is null
            || item.Targets.Count > SunderPackageFormat.MaxTargets)
        {
            throw new InvalidDataException(
                $"Package '{item.PackageId}' has an invalid install-plan target inventory.");
        }

        var targetKeys = new List<SunderPackageTargetKey>(item.Targets.Count);
        var uniqueTargetKeys = new HashSet<SunderPackageTargetKey>();
        foreach (var target in item.Targets)
        {
            if (target is null
                || !SunderPackageTargetKey.TryCreate(target.Role, target.Rid, out var key)
                || target.RequiredHostCapabilities is null
                || !uniqueTargetKeys.Add(key))
            {
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' install-plan targets must be a complete inventory of unique exact targets.");
            }
            targetKeys.Add(key);
        }

        var selectedKeys = new HashSet<SunderPackageTargetKey>();
        foreach (var selected in selectedTargets)
        {
            if (selected is null
                || !SunderPackageTargetKey.TryCreate(selected.Role, selected.Rid, out var key)
                || !string.Equals(key.Rid, currentRid, StringComparison.Ordinal)
                || !selectedKeys.Add(key))
            {
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' selected install-plan targets must be unique exact targets for current RID '{currentRid}'.");
            }

            if (!targetKeys.Any(target => string.Equals(target.Role, key.Role, StringComparison.Ordinal)))
            {
                continue;
            }
            if (!uniqueTargetKeys.Contains(key))
            {
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' install-plan selected target '{key}' is missing from its target metadata inventory.");
            }
        }

        var selectedProjectionKeys = selectedKeys
            .Where(key => targetKeys.Any(target => string.Equals(target.Role, key.Role, StringComparison.Ordinal)))
            .OrderBy(static key => ProjectionOrder(key.Role))
            .ThenBy(static key => key.Rid, StringComparer.Ordinal)
            .Select(static key => new RegistryPackageProjectionKey(key.Role, key.Rid));
        var expectedKeys = selectedProjectionKeys
            .Prepend(new RegistryPackageProjectionKey(SunderPackageProjectionFormat.SharedKind, null))
            .ToArray();
        if (item.Artifacts is null
            || item.Artifacts.Count != expectedKeys.Length
            || item.Artifacts.Count > SunderPackageFormat.MaxTargets + 1
            || item.Artifacts.Any(static artifact => artifact is null))
        {
            throw new InvalidDataException(
                $"Package '{item.PackageId}' install-plan projection artifacts must contain exactly shared plus every selected current-RID role projection.");
        }

        var actualKeys = item.Artifacts
            .Select(static artifact => new RegistryPackageProjectionKey(artifact.Kind, artifact.Rid))
            .ToArray();
        var ordered = item.Artifacts
            .OrderBy(artifact => ProjectionOrder(artifact.Kind))
            .ThenBy(artifact => artifact.Rid, StringComparer.Ordinal)
            .ToArray();
        if (!item.Artifacts.SequenceEqual(ordered)
            || !actualKeys.SequenceEqual(expectedKeys))
        {
            throw new InvalidDataException(
                $"Package '{item.PackageId}' install-plan projection artifacts must exactly match shared plus its canonically ordered selected role projections for current RID '{currentRid}'.");
        }
    }

    private static void ValidateProjection(
        RegistryPackageInstallPlanItem item,
        RegistryPackageProjectionArtifact artifact,
        SunderPackageProjectionArchiveValidationResult validation)
    {
        if (!validation.Success || validation.Descriptor is null || validation.Manifest is null)
        {
            throw new InvalidDataException(
                $"Package '{item.PackageId}' projection '{ProjectionKey(artifact)}' failed strict inspection: {string.Join(" | ", validation.Errors)}");
        }
        var descriptor = validation.Descriptor;
        if (!string.Equals(descriptor.PackageId, item.PackageId, StringComparison.Ordinal)
            || !string.Equals(descriptor.PackageVersion, item.Version, StringComparison.Ordinal)
            || !string.Equals(descriptor.Kind, artifact.Kind, StringComparison.Ordinal)
            || !string.Equals(descriptor.Rid, artifact.Rid, StringComparison.Ordinal)
            || !string.Equals(descriptor.SourceArchiveSha256, artifact.SourceArchiveSha256, StringComparison.Ordinal)
            || !string.Equals(descriptor.ManifestSha256, artifact.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(descriptor.ProjectionSha256, artifact.ProjectionContentIdentity, StringComparison.Ordinal)
            || descriptor.ProjectionFormatVersion != artifact.ProjectionFormatVersion)
        {
            throw new InvalidDataException(
                $"Package '{item.PackageId}' projection '{ProjectionKey(artifact)}' does not match the Registry descriptor.");
        }
    }

    private static void ValidateProjectionSourceIdentity(
        RegistryPackageInstallPlanItem item,
        IReadOnlyList<SunderPackageProjectionArchiveValidationResult> projections)
    {
        var expected = projections[0].Descriptor!;
        foreach (var projection in projections.Skip(1))
        {
            if (!string.Equals(
                    projection.Descriptor!.SourceArchiveSha256,
                    expected.SourceArchiveSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' projections do not share the same sourceArchiveSha256.");
            }
            if (!string.Equals(
                    projection.Descriptor.ManifestSha256,
                    expected.ManifestSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' projections do not share the same manifestSha256.");
            }
        }
    }

    private static void ValidatePlanTargets(
        RegistryPackageInstallPlanItem item,
        SunderPackageManifest manifest)
    {
        var sourceTargets = SunderPackageTargetResolver.EnumerateTargets(manifest);
        if (sourceTargets.Count != item.Targets.Count)
        {
            throw new InvalidDataException(
                $"Package '{item.PackageId}' install-plan target inventory does not exactly match the projection source manifest.");
        }

        foreach (var planned in item.Targets)
        {
            var key = new SunderPackageTargetKey(planned.Role, planned.Rid);
            if (!SunderPackageTargetResolver.TryResolveTarget(manifest, key, out var source)
                || source is null
                || !TargetMatches(planned, source))
            {
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' install-plan target '{key}' does not exactly match the projection source manifest.");
            }
        }
    }

    private static bool TargetMatches(
        RegistryPackageTarget planned,
        SunderPackageTargetManifest source)
        => string.Equals(planned.Role, source.Role, StringComparison.Ordinal)
           && string.Equals(planned.Rid, source.Rid, StringComparison.Ordinal)
           && string.Equals(planned.Kind, source.Kind, StringComparison.Ordinal)
           && string.Equals(planned.EntryPoint, source.EntryPoint, StringComparison.Ordinal)
           && string.Equals(planned.TargetFramework, source.TargetFramework, StringComparison.Ordinal)
           && string.Equals(planned.SdkVersion, source.SdkVersion, StringComparison.Ordinal)
           && ExecutionRuntimeMatches(planned.ExecutionRuntime, source.ExecutionRuntime)
           && (planned.RequiredHostCapabilities ?? []).SequenceEqual(
               (source.RequiredHostCapabilities ?? []).Select(static capability => capability!),
               StringComparer.Ordinal)
           && planned.Views.SequenceEqual(
               (source.Views ?? []).Where(static view => view is not null).Select(static view => new RegistryPackageWebView(
                   view!.ViewId!,
                   view.DisplayName!,
                   view.Route!,
                   view.Icon,
                   view.DefaultPlacement!,
                   view.ShowInHotbar!.Value)));

    private static bool ExecutionRuntimeMatches(
        RegistryExecutionRuntimeRequirement? planned,
        SunderPackageExecutionRuntimeManifest? source)
        => planned is null && source is null
           || planned is not null
           && source is not null
           && string.Equals(planned.RuntimeId, source.Id, StringComparison.Ordinal)
           && string.Equals(planned.VersionRange, source.VersionRange, StringComparison.Ordinal)
           && string.Equals(planned.ModuleAbi, source.ModuleAbi, StringComparison.Ordinal);

    private static IReadOnlySet<string> BuildAllowedOrigins(
        Uri registryOrigin,
        IReadOnlyList<string> advertisedOrigins)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeOrigin(registryOrigin),
        };
        foreach (var advertised in advertisedOrigins)
        {
            if (!Uri.TryCreate(advertised, UriKind.Absolute, out var candidate)
                || !IsOriginOnly(candidate)
                || registryOrigin.Scheme == Uri.UriSchemeHttps && candidate.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Registry advertised an invalid trusted artifact origin.");
            }
            allowed.Add(NormalizeOrigin(candidate));
        }
        return allowed;
    }

    private static Uri ValidateArtifactUri(
        Uri registryOrigin,
        string downloadUrl,
        IReadOnlySet<string> allowedOrigins)
        => ValidateArtifactUri(registryOrigin, new Uri(registryOrigin, downloadUrl), allowedOrigins);

    private static Uri ValidateArtifactUri(
        Uri registryOrigin,
        Uri? artifact,
        IReadOnlySet<string> allowedOrigins)
    {
        if (artifact is null
            || artifact.Scheme != Uri.UriSchemeHttps && artifact.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(artifact.UserInfo)
            || !allowedOrigins.Contains(NormalizeOrigin(artifact)))
        {
            throw new InvalidDataException(
                "Registry artifact URL must remain on the Registry origin or an explicitly advertised trusted artifact origin.");
        }
        return artifact;
    }

    private static bool IsOriginOnly(Uri origin)
        => (origin.Scheme == Uri.UriSchemeHttps || origin.Scheme == Uri.UriSchemeHttp)
           && string.IsNullOrEmpty(origin.UserInfo)
           && origin.AbsolutePath == "/"
           && string.IsNullOrEmpty(origin.Query)
           && string.IsNullOrEmpty(origin.Fragment);

    private static string NormalizeOrigin(Uri origin)
        => origin.GetLeftPart(UriPartial.Authority).TrimEnd('/');

    private static string ProjectionKey(RegistryPackageProjectionArtifact artifact)
        => artifact.Rid is null ? artifact.Kind : $"{artifact.Kind}/{artifact.Rid}";

    private static int ProjectionOrder(string kind)
        => kind switch
        {
            SunderPackageProjectionFormat.SharedKind => 0,
            SunderPackageProjectionFormat.RuntimeKind => 1,
            SunderPackageProjectionFormat.AppKind => 2,
            _ => 3,
        };

    private static async Task<bool> FilesEqualAsync(
        string first,
        string second,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length)
        {
            return false;
        }
        await using var firstStream = File.OpenRead(first);
        await using var secondStream = File.OpenRead(second);
        var firstHash = await SHA256.HashDataAsync(firstStream, cancellationToken);
        var secondHash = await SHA256.HashDataAsync(secondStream, cancellationToken);
        return firstHash.AsSpan().SequenceEqual(secondHash);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDeleteDirectory(string path)
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

    private sealed class BoundedReadStream(Stream inner, long maxLength, string limitMessage) : Stream
    {
        private long _length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            var remaining = maxLength - _length;
            var read = await inner.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, remaining + 1)],
                cancellationToken);
            if (read > remaining)
            {
                throw new RegistryArtifactTooLargeException(limitMessage);
            }
            _length += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

internal sealed class RegistryArtifactTooLargeException(string message) : Exception(message);
