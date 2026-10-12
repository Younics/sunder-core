using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class RuntimeContentTransferStore
{
    internal RuntimePackageUploadRetentionResult RetainPackageUpload(RuntimePackageUploadRetentionRequest request, long generation)
    {
        ValidateUploadIdentity(request.UploadId, request.ExpectedContentHash, request.ExpectedLength);
        SweepExpired(_timeProvider.GetUtcNow());
        lock (_uploadsGate)
        {
            if (!_uploads.TryGetValue(request.UploadId, out var entry)) return RetentionUnavailable();
            if (entry.Kind != RuntimeUploadKind.Package || entry.Generation != generation
                || entry.ContentHash != request.ExpectedContentHash || entry.Length != request.ExpectedLength)
                return RetentionConflict();
            if (entry.Retention is not null) return RetentionConflict(); // A second owner cannot steal/extend a lease.
            if (_uploads.Values.Count(value => value.Retention is not null) >= RuntimePackageUploadRetentionLimits.MaximumLeases)
                return new(false, RuntimePackageUploadRetentionErrorCodes.Conflict, "The package-upload retention limit was reached.", null);
            var now = ReadUploadClock();
            if (now is null || !now.SameEpoch(entry.CreatedClock) || now.Ticks < entry.CreatedClock.Ticks
                || (now.Ticks - entry.CreatedClock.Ticks) / (double)now.Frequency >= _policy.ContentTransferLifetime.TotalSeconds
                || !File.Exists(entry.FilePath)) { RemoveUpload(request.UploadId); return RetentionUnavailable(); }
            var maximum = checked(entry.CreatedClock.Ticks + now.Frequency * RuntimePackageUploadRetentionLimits.MaximumLifetimeSeconds);
            if (now.Ticks >= maximum) { RemoveUpload(request.UploadId); return RetentionUnavailable(); }
            entry.Retention = new(Guid.NewGuid().ToString("N"), now,
                Math.Min(maximum, checked(now.Ticks + now.Frequency * RuntimePackageUploadRetentionLimits.RenewalSeconds)), maximum);
            return RetentionResult(request.UploadId, entry);
        }
    }

    internal RuntimePackageUploadRetentionResult GetPackageUploadRetention(RuntimePackageUploadRetentionHandle handle, bool renew = false)
    {
        ValidateRetentionHandle(handle);
        lock (_uploadsGate)
        {
            if (!_uploads.TryGetValue(handle.UploadId, out var entry)) return RetentionUnavailable();
            if (!MatchesRetention(handle.UploadId, entry, handle)) return RetentionConflict();
            var now = ReadUploadClock();
            if (!RetentionIsLive(entry, now) || !File.Exists(entry.FilePath))
            { RemoveUpload(handle.UploadId); return RetentionUnavailable(); }
            if (renew) entry.Retention!.ExpiresAtTicks = Math.Min(entry.Retention.MaximumTicks,
                checked(now!.Ticks + now.Frequency * RuntimePackageUploadRetentionLimits.RenewalSeconds));
            return RetentionResult(handle.UploadId, entry);
        }
    }

    internal RuntimePackageUploadRetentionResult ReleasePackageUploadRetention(RuntimePackageUploadRetentionHandle handle)
    {
        ValidateRetentionHandle(handle);
        lock (_uploadsGate)
        {
            if (_uploads.TryGetValue(handle.UploadId, out var entry))
            {
                if (!MatchesRetention(handle.UploadId, entry, handle)) return RetentionConflict();
                RemoveUpload(handle.UploadId);
            }
            return new(true, null, "Package upload released or already consumed/expired.", null);
        }
    }

    private static bool MatchesRetention(string uploadId, UploadEntry entry, RuntimePackageUploadRetentionHandle handle)
        => entry.Kind == RuntimeUploadKind.Package && handle.UploadId == uploadId && entry.Retention?.LeaseId == handle.LeaseId
            && entry.ContentHash == handle.ContentHash && entry.Length == handle.Length;

    private PolicyClockReading? ReadUploadClock()
    {
        try
        {
            var clock = _uploadClock.Read();
            RegistryActivePolicyClient.ValidateClock(clock);
            return clock;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return null; }
    }

    private static bool RetentionIsLive(UploadEntry entry, PolicyClockReading? now)
    {
        var retention = entry.Retention;
        if (retention is null || now is null || !now.SameEpoch(retention.LastClock) || now.Ticks < retention.LastClock.Ticks
            || now.Ticks >= retention.ExpiresAtTicks || now.Ticks >= retention.MaximumTicks) return false;
        retention.LastClock = now;
        return true;
    }

    private static RuntimePackageUploadRetentionResult RetentionResult(string uploadId, UploadEntry entry)
    {
        var lease = entry.Retention!;
        // UTC is display data. Expiry/renewal authority uses the suspend-aware continuous clock.
        DateTimeOffset DisplayTime(long tick) => entry.CreatedAtUtc.AddSeconds((tick - entry.CreatedClock.Ticks) / (double)entry.CreatedClock.Frequency);
        return new(true, null, "Package upload retained; re-preview package installation before resuming.",
            new(new(uploadId, lease.LeaseId, entry.ContentHash, entry.Length), DisplayTime(lease.ExpiresAtTicks), DisplayTime(lease.MaximumTicks)));
    }

    private static RuntimePackageUploadRetentionResult RetentionUnavailable()
        => new(false, RuntimePackageUploadRetentionErrorCodes.Unavailable,
            "The upload retention expired, was consumed/released, or belongs to a previous Host instance. Upload the original file again and re-preview.", null);

    private static RuntimePackageUploadRetentionResult RetentionConflict()
        => new(false, RuntimePackageUploadRetentionErrorCodes.Conflict, "The upload identity or retention owner does not match.", null);

    private static void ValidateUploadIdentity(string uploadId, string hash, long length)
    {
        if (!Guid.TryParseExact(uploadId, "N", out _) || !PackageContentSignatureValidator.IsLowercaseSha256(hash)
            || length < 0 || length > MaxPackageUploadBytes)
            throw new RuntimeValidationException("A valid immutable package upload id, SHA-256 and length are required.");
    }

    private static void ValidateRetentionHandle(RuntimePackageUploadRetentionHandle handle)
    {
        ValidateUploadIdentity(handle.UploadId, handle.ContentHash, handle.Length);
        if (!Guid.TryParseExact(handle.LeaseId, "N", out _)) throw new RuntimeValidationException("A valid package-upload retention owner is required.");
    }

    private sealed class UploadRetention(string leaseId, PolicyClockReading lastClock, long expiresAtTicks, long maximumTicks)
    {
        internal string LeaseId { get; } = leaseId;
        internal PolicyClockReading LastClock { get; set; } = lastClock;
        internal long ExpiresAtTicks { get; set; } = expiresAtTicks;
        internal long MaximumTicks { get; } = maximumTicks;
    }
}
