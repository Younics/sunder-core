using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template;

public sealed class PackageRuntimeState(IPackageKeyValueStore state)
{
    private const string LastRunKey = "runtime.last-run-utc";

    public Task<string?> GetLastRunUtcAsync(CancellationToken cancellationToken = default)
        => state.GetValueAsync(LastRunKey, cancellationToken);

    public Task SetLastRunUtcAsync(DateTimeOffset value, CancellationToken cancellationToken = default)
        => state.SetValueAsync(LastRunKey, value.ToString("O"), cancellationToken);
}
