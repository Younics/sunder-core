using Xunit;

[assembly: CollectionBehavior(MaxParallelThreads = 2)]

namespace Sunder.App.Integration.Tests;

[CollectionDefinition("App/Runtime integration", DisableParallelization = true)]
public sealed class AppRuntimeIntegrationCollection;
