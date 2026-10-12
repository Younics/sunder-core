using System.Net;
using System.Security.Cryptography;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.OfficialRuntimeSourcePolicy;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeArtifactDownloader(
    OfficialRuntimeHttpClient http, ExecutionRuntimeStore store, TimeProvider clock,
    ExecutionRuntimePhaseTelemetry? telemetry = null)
{
    private readonly ExecutionRuntimePhaseTelemetry _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;

    internal Task<ExecutionRuntimeEnsureResult> DownloadAsync(RuntimeExecutionRuntimeVendorArtifact artifact, CancellationToken token)
        => DownloadAsync(new ExecutionRuntimeAcquisitionPlan(artifact), token);

    internal async Task<ExecutionRuntimeEnsureResult> DownloadAsync(ExecutionRuntimeAcquisitionPlan plan, CancellationToken token)
    {
        var artifact = plan.Artifact;
        ValidateArtifact(artifact);
        using var operation = _telemetry.BeginOperation();
        // Store owns singleflight/cancellation, exact-version conflict locks and the recovery fence.
        // A healthy local receipt wins before invoking this factory; no vendor call at launch.
        return await store.EnsureArchiveAsync(plan, cancellationToken => DownloadOwnedAsync(artifact, cancellationToken), token).ConfigureAwait(false);
    }

    internal async Task<ExecutionRuntimeEnsureResult> RequireInstalledAsync(RuntimeExecutionRuntimeVendorArtifact artifact, CancellationToken token)
    {
        ValidateArtifact(artifact);
        var identity = ExecutionRuntimeIdentity.FromContract(artifact.Identity);
        var receipt = await store.GetReceiptAsync(identity, token).ConfigureAwait(false);
        if (receipt is null || receipt.Provenance.Distribution.MaterializationProfile != artifact.Distribution.MaterializationProfile
            || !(await store.VerifyAsync(identity, deep: false, token).ConfigureAwait(false)).Healthy)
            throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.Missing,
                "Install the required official runtime first; package operations never download it implicitly.");
        return new(receipt, true);
    }

    private async Task<ExecutionRuntimeOwnedArchive> DownloadOwnedAsync(RuntimeExecutionRuntimeVendorArtifact artifact, CancellationToken token)
    {
        using var phase = _telemetry.BeginPhase(ExecutionRuntimePhaseNames.Download,
            ExecutionRuntimeIdentity.FromContract(artifact.Identity), bytes: 0, cacheHit: false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await DownloadAttemptAsync(artifact, phase, token).ConfigureAwait(false);
                    phase.Succeeded();
                    return result;
                }
                catch (Exception error) when (attempt < 2 && IsTransient(error))
                { await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), clock, token).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) { phase.Canceled(); throw; }
        catch (Exception error) { phase.Failed(error); throw; }
    }

    private async Task<ExecutionRuntimeOwnedArchive> DownloadAttemptAsync(RuntimeExecutionRuntimeVendorArtifact artifact,
        ExecutionRuntimePhaseTelemetry.PhaseScope phase, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var directory = store.Layout.GetStagingPath(Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "official-download.archive");
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var response = await http.OpenAsync(new(artifact.Distribution.DownloadUrl), artifact.Identity.SourceId,
                deadline.Token, artifact.Identity.VendorVersion, artifact.Identity.Rid).ConfigureAwait(false);
            var declared = response.Content.Headers.ContentLength;
            if (declared is <= 0 or > MaxArchiveBytes || declared is { } length
                && artifact.Distribution.DownloadSize is { } expected && length != expected)
                throw new InvalidDataException("Invalid official archive Content-Length.");
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var vendor = IncrementalHash.CreateHash(VendorHash(artifact.Identity));
                using var local = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    total = checked(total + count);
                    if (total > MaxArchiveBytes || declared is { } declaredSize && total > declaredSize
                        || artifact.Distribution.DownloadSize is { } metadataSize && total > metadataSize)
                        throw new InvalidDataException("Official archive exceeds its byte limit.");
                    vendor.AppendData(buffer, 0, count);
                    local.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    phase.SetBytes(total);
                }
                if (total == 0 || declared is { } end && total != end
                    || artifact.Distribution.DownloadSize is { } advertised && total != advertised
                    || !CryptographicOperations.FixedTimeEquals(vendor.GetHashAndReset(), Convert.FromHexString(artifact.Identity.VendorChecksumDigest)))
                    throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.IntegrityFailed, "Official runtime size or vendor checksum does not match.");
                // Materialization independently checks these same original bytes and records this local SHA-256.
                _ = local.GetHashAndReset();
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            var final = response.RequestMessage!.RequestUri!.AbsoluteUri;
            return new(path, () => ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(directory), final);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(directory);
            throw new TimeoutException("Official runtime download exceeded its timeout.");
        }
        catch
        {
            ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(directory);
            throw;
        }
    }

    private static bool IsTransient(Exception error) => error is TimeoutException
        || error is HttpRequestException request && (request.StatusCode is null
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)request.StatusCode >= 500);
}
