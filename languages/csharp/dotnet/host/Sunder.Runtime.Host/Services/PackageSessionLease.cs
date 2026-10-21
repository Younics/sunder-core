namespace Sunder.Runtime.Host.Services;

internal readonly record struct PackageActivationIdentity(
    object SessionIdentity,
    Guid RuntimeActivationId);

internal sealed class PackageSessionLease : IDisposable
{
    private Action? _release;

    internal PackageSessionLease(
        ActivePackageSession session,
        long generation,
        long leaseId,
        object identity,
        object generationIdentity,
        CancellationToken retirementToken,
        CancellationToken generationRetirementToken,
        Action release)
    {
        Session = session;
        Generation = generation;
        LeaseId = leaseId;
        Identity = identity;
        GenerationIdentity = generationIdentity;
        RetirementToken = retirementToken;
        GenerationRetirementToken = generationRetirementToken;
        _release = release;
    }

    public ActivePackageSession Session { get; }

    public long Generation { get; }

    internal long LeaseId { get; }

    public CancellationToken RetirementToken { get; }

    internal CancellationToken GenerationRetirementToken { get; }

    internal object Identity { get; }

    internal object GenerationIdentity { get; }

    internal PackageActivationIdentity GetPackageActivationIdentity(ActiveLoadedPackage package)
        => new(Identity, package.RuntimeActivationId);

    // Called only by the acquiring owner before handing the lease to work.
    internal void Own(IDisposable lifetime)
    {
        var release = _release ?? throw new ObjectDisposedException(nameof(PackageSessionLease));
        _release = () => { try { release(); } finally { lifetime.Dispose(); } };
    }

    public CancellationTokenSource CreateLinkedCancellation(
        CancellationToken requestCancellation,
        CancellationToken hostCancellation = default)
        => CancellationTokenSource.CreateLinkedTokenSource(
            [
                requestCancellation,
                hostCancellation,
                RetirementToken,
                GenerationRetirementToken,
            ]);

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
