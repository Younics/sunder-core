using Xunit;

namespace Sunder.Runtime.Host.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RuntimeProcessIntegrationCollection
{
    public const string Name = "Runtime process integration";
}
