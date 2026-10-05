using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

// Explicit local/Registry import preparation. Local artifacts never trigger a
// Registry fallback or acquire a publication/installation identity.
internal sealed class DevelopmentImportAuthorityPreparation(RegistryHttpClient http, DevelopmentImportAuthorityStore store,
    RegistryPackagePolicySynchronizer synchronizer, PackageLifecyclePolicyCache policies, TimeProvider timeProvider)
{
    internal async Task<RuntimeDevPackageImportsPrepared> PrepareAsync(RuntimePrepareDevPackageImportsRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Folder) || request.Folder.Length > 4096
            || request.RegistryOrigin is not null && (string.IsNullOrWhiteSpace(request.RegistryOrigin) || request.RegistryOrigin.Length > 2048)
            || request.CandidateArchives.Count > 128 || request.CandidateArchives.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 4096)
            || request.PlatformCatalogPath is not null && (string.IsNullOrWhiteSpace(request.PlatformCatalogPath) || request.PlatformCatalogPath.Length > 4096))
            throw new RuntimeValidationException("Development import preparation requires bounded, nonempty explicit input paths and any selected Registry origin.");
        if (request.RegistryOrigin is not null && (request.CandidateArchives.Count != 0 || request.PlatformCatalogPath is not null))
            throw new RuntimeValidationException("Select local candidate/catalog evidence or Registry preparation, not both.");
        var origin = request.RegistryOrigin is null ? null : RegistryActivePolicyClient.Origin(request.RegistryOrigin);
        var folder = Path.GetFullPath(request.Folder); var manifestPath = Path.Combine(folder, SunderPackageFormat.ManifestPath);
        PackageStorePathGuard.EnsureSafe(manifestPath, folder);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(folder, token);
        if (!validation.Success) throw DevelopmentImportAuthorityStore.Invalid("The development folder is not a complete canonical package: " + string.Join(" | ", validation.Errors));
        var manifest = validation.Manifest!; var hash = RuntimeRpcPackageMetadata.GetManifestSha256(validation.ContentIndex!);
        await VerifyManifest();
        var artifacts = (manifest.PackageArtifactImports ?? []).OfType<SunderPackageArtifactImportManifest>().OrderBy(value => value.PackageId, StringComparer.Ordinal).ToArray();
        var subjects = DevelopmentImportAuthorityStore.Platforms(manifest);
        var inventories = new List<DevelopmentPackageImport>(); var catalogs = new List<string>(); long remaining = 64 * 1024 * 1024;
        if (origin is null)
        {
            long archiveBytes = 0;
            var selected = new Dictionary<string, RegistryPackagePublicApiInventoryResponseV1>(StringComparer.Ordinal);
            foreach (var candidate in request.CandidateArchives)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(candidate);
                PackageStorePathGuard.EnsureSafe(path, Path.GetDirectoryName(path)!);
                var length = new FileInfo(path).Length;
                if (length is < 1 or > 536_870_912 || (archiveBytes += length) > 1_073_741_824)
                    throw DevelopmentImportAuthorityStore.Invalid("Local candidate archives exceed the preparation byte limit.");
                var extracted = Path.Combine(Path.GetTempPath(), "sunder-development-import-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var inspected = await SunderPackageArchiveInspector.ExtractAndValidateAsync(path, extracted, token);
                    if (!inspected.Success || inspected.Manifest is not { } candidateManifest)
                        throw DevelopmentImportAuthorityStore.Invalid("A local candidate is not a complete canonical package archive.");
                    var inventory = SunderPackagePublicApiInspection.ReadValidated(extracted, candidateManifest).Inventory;
                    remaining -= inventory.ToCanonicalUtf8().Length;
                    if (remaining < 0) throw DevelopmentImportAuthorityStore.Invalid("Local inventories exceed the evidence byte limit.");
                    var response = new RegistryPackagePublicApiInventoryResponseV1(
                        inventory.Payload.Deserialize<RegistryPackagePublicApiInventoryV1>(DevelopmentImportAuthorityStore.Json)!, inventory.PublicInventorySha256);
                    if (!selected.TryAdd(candidateManifest.Id!, response) || !artifacts.Any(value => value.PackageId == candidateManifest.Id))
                        throw DevelopmentImportAuthorityStore.Invalid("Each local candidate must identify one distinct selected package artifact.");
                }
                finally { if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true); }
            }
            foreach (var artifact in artifacts)
                AddInventory(artifact, selected.GetValueOrDefault(artifact.PackageId!)
                    ?? throw DevelopmentImportAuthorityStore.Invalid("A selected package needs its exact local --candidate archive."));
            if (request.PlatformCatalogPath is not null)
            {
                var path = Path.GetFullPath(request.PlatformCatalogPath);
                PackageStorePathGuard.EnsureSafe(path, Path.GetDirectoryName(path)!);
                var bytes = await DevelopmentImportAuthorityStore.ReadAsync(path, (int)Math.Min(67_108_864, remaining), token);
                _ = SunderPlatformContractCatalog.Verify(bytes);
                catalogs.Add(Convert.ToBase64String(bytes));
            }
        }
        else
        {
            foreach (var artifact in artifacts)
            {
                var bytes = await Read($"api/v1/packages/{Uri.EscapeDataString(artifact.PackageId!)}/versions/{Uri.EscapeDataString(artifact.SelectedPackageVersion!)}/public-api", 1_048_576);
                AddInventory(artifact, JsonSerializer.Deserialize<RegistryPackagePublicApiInventoryResponseV1>(bytes, DevelopmentImportAuthorityStore.Json)
                    ?? throw DevelopmentImportAuthorityStore.Invalid("Registry inventory is empty."));
            }
            foreach (var subject in subjects)
            {
                var route = $"api/v1/platform-contracts/{Uri.EscapeDataString(subject.AuthorityId)}/releases/{Uri.EscapeDataString(subject.ReleaseVersion)}";
                var metadata = JsonSerializer.Deserialize<RegistryPlatformContractReleaseV1>(await Read(route, 1_048_576), DevelopmentImportAuthorityStore.Json)
                    ?? throw DevelopmentImportAuthorityStore.Invalid("Registry catalog metadata is empty.");
                var uri = new Uri(new Uri(origin + "/"), route + "/catalog");
                if (metadata.AuthorityId != subject.AuthorityId || metadata.ReleaseVersion != subject.ReleaseVersion || metadata.CatalogSha256 != subject.CatalogSha256
                    || metadata.CatalogDownloadUrl != uri.AbsoluteUri || metadata.CatalogBytes is < 2 or > 67_108_864 || metadata.LifecycleState is not ("Supported" or "Revoked"))
                    throw DevelopmentImportAuthorityStore.Invalid("Registry catalog metadata differs from the exact authority.");
                var bytes = await Read(route + "/catalog", (int)metadata.CatalogBytes, "\"" + subject.CatalogSha256 + "\"", (int)metadata.CatalogBytes);
                var catalog = SunderPlatformContractCatalog.Verify(bytes);
                if (catalog.ContentIdentity != subject.CatalogSha256 || catalog.ReleaseVersion != subject.ReleaseVersion
                    || !catalog.Contracts.Select(value => new RegistryPlatformContractDescriptorV1(value.ContractId, value.Version, value.GetCanonicalUtf8().Length, value.Sha256, value.AbiSha256)).SequenceEqual(metadata.Contracts))
                    throw DevelopmentImportAuthorityStore.Invalid("Registry catalog metadata differs from its canonical content.");
                catalogs.Add(Convert.ToBase64String(bytes));
            }
        }
        var evidence = new DevelopmentImportAuthority(1, origin is null ? "local" : "registry", manifest.Id!, manifest.Version!, hash, origin, inventories, catalogs);
        DevelopmentImportAuthorityStore.Validate(evidence, manifest, hash);
        if (subjects.Length > 0 && origin is not null)
        {
            await synchronizer.RefreshAsync([new(origin, [], subjects, [])], token);
            policies.RequirePlatformActivation(origin, subjects);
        }
        await VerifyManifest(); await store.SaveAsync(evidence, manifest, token); await VerifyManifest();
        return new(manifest.Id!, manifest.Version!, hash, artifacts.Length, subjects.Length);

        void AddInventory(SunderPackageArtifactImportManifest artifact, RegistryPackagePublicApiInventoryResponseV1 response)
        {
            DevelopmentImportAuthorityStore.ValidateInventory(artifact, response);
            var authorities = response.Inventory.Contracts.Where(value => value.AuthorityKind == "package" && value.AuthorityId != artifact.PackageId)
                .Select(value => value.AuthorityId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(id => artifacts.SingleOrDefault(value => value.PackageId == id) ?? throw DevelopmentImportAuthorityStore.Invalid("An ultimate authority lacks a reviewed selection."))
                .Select(value => new RegistryPackageAuthorityRequirement(value.PackageId!, value.RequestedVersionRange!, value.IncludePrerelease!.Value)).ToArray();
            inventories.Add(new(response, artifact.RequestedVersionRange!, artifact.IncludePrerelease!.Value, authorities));
        }

        async Task VerifyManifest()
        {
            PackageStorePathGuard.EnsureSafe(manifestPath, folder);
            var bytes = await DevelopmentImportAuthorityStore.ReadAsync(manifestPath, 4 * 1024 * 1024, token);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) throw DevelopmentImportAuthorityStore.Invalid("The development manifest changed during preparation.");
        }
        async Task<byte[]> Read(string route, int maximum, string? etag = null, int? length = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(RuntimePolicyTiming.RequestDeadlineSeconds), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var requestToken = linked.Token;
            try
            {
                var uri = new Uri(new Uri(origin + "/"), route);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await http.SendAsync(request, requestToken);
                if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri != uri
                    || response.Content.Headers.ContentType?.MediaType != "application/json"
                    || etag is not null && response.Headers.ETag?.ToString() != etag
                    || length is not null && response.Content.Headers.ContentLength != length)
                    throw DevelopmentImportAuthorityStore.Invalid("Registry evidence must be an exact successful same-origin JSON response.");
                maximum = (int)Math.Min(maximum, remaining);
                if (maximum < 1 || response.Content.Headers.ContentLength > maximum) throw DevelopmentImportAuthorityStore.Invalid("Registry evidence exceeds its total byte budget.");
                await using var input = await response.Content.ReadAsStreamAsync(requestToken); using var output = new MemoryStream(); var buffer = new byte[16 * 1024];
                while (true)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length)), requestToken);
                    if (count == 0) break;
                    output.Write(buffer, 0, count); if (output.Length > maximum) throw DevelopmentImportAuthorityStore.Invalid("Registry evidence exceeds its byte budget.");
                }
                if (response.Content.Headers.ContentLength is { } declared && output.Length != declared) throw DevelopmentImportAuthorityStore.Invalid("Registry evidence length differs.");
                remaining -= output.Length; return output.ToArray();
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            { throw new TimeoutException("Development Registry evidence exceeded its request/body deadline."); }
        }
    }
}
