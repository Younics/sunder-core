using System.Runtime.CompilerServices;
using Sunder.Sdk.Services;

namespace Sunder.Package.Template;

[SunderService]
public sealed partial class SampleService
{
    public Task<string> GreetAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult($"Hello, {name}!");
    }

    public async IAsyncEnumerable<string> GreetManyAsync(
        IReadOnlyList<string> names,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var name in names)
            yield return await GreetAsync(name, cancellationToken).ConfigureAwait(false);
    }
}
