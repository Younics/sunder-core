namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionLease : IDisposable
{
    private Action? _release;

    internal PackageSessionLease(
        ActivePackageSession session,
        long generation,
        object identity,
        CancellationToken retirementToken,
        Action release)
    {
        Session = session;
        Generation = generation;
        Identity = identity;
        RetirementToken = retirementToken;
        _release = release;
    }

    public ActivePackageSession Session { get; }

    public long Generation { get; }

    public CancellationToken RetirementToken { get; }

    internal object Identity { get; }

    public CancellationTokenSource CreateLinkedCancellation(
        CancellationToken requestCancellation,
        CancellationToken hostCancellation = default)
        => CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellation,
            hostCancellation,
            RetirementToken);

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
