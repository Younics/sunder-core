using System.Runtime.CompilerServices;
using Sunder.Sdk.Services;

namespace Sunder.Package.Template;

/// <summary>Provides the starter package's greeting operations.</summary>
[SunderService]
public sealed partial class SampleService
{
    /// <summary>Creates a greeting for one name.</summary>
    /// <param name="name">The name to greet.</param>
    /// <param name="cancellationToken">Cancels this operation.</param>
    /// <returns>The greeting text.</returns>
    public Task<string> GreetAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult($"Hello, {name}!");
    }

    /// <summary>Streams one greeting for each name.</summary>
    /// <param name="names">The names to greet in order.</param>
    /// <param name="cancellationToken">Cancels this enumeration.</param>
    /// <returns>The greeting texts.</returns>
    public async IAsyncEnumerable<string> GreetManyAsync(
        IReadOnlyList<string> names,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var name in names)
            yield return await GreetAsync(name, cancellationToken).ConfigureAwait(false);
    }
}
