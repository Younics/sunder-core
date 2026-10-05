using Sunder.Package.Projections;

namespace Sunder.Runtime.Host.Services;

internal sealed class PreparedPackageProjectionLease : IAsyncDisposable
{
    private readonly PackageProjectionCache _cache;
    private readonly PackageProjectionReference _reference;
    private readonly Guid _hostInstanceId;
    private readonly string _packageId;
    private readonly IReadOnlyList<string> _warmValidationPaths;
    private PackageProjectionAcquisition? _acquisition;

    public PreparedPackageProjectionLease(
        PackageProjectionCache cache,
        PackageProjectionAcquisition acquisition,
        Guid hostInstanceId,
        string packageId,
        IReadOnlyList<string> warmValidationPaths)
    {
        _cache = cache;
        _acquisition = acquisition;
        _reference = acquisition.Reference;
        _hostInstanceId = hostInstanceId;
        _packageId = packageId;
        _warmValidationPaths = warmValidationPaths;
    }

    public PackageProjectionAcquisition? Take()
        => Interlocked.Exchange(ref _acquisition, null);

    public PreparedPackageProjectionLease Reacquire(string generationId)
    {
        var acquisition = _cache.AcquireAndLeaseAsync(
                _reference,
                new PackageProjectionOwner(
                    "runtime-host",
                    _hostInstanceId,
                    _packageId,
                    generationId),
                _warmValidationPaths,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return new PreparedPackageProjectionLease(
            _cache,
            acquisition,
            _hostInstanceId,
            _packageId,
            _warmValidationPaths);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _acquisition, null) is { } acquisition)
        {
            await acquisition.DisposeAsync().ConfigureAwait(false);
        }
    }
}
