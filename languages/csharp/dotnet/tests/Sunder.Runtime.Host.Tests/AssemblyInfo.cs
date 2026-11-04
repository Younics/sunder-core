using Xunit;
using System.Runtime.CompilerServices;

[assembly: CollectionBehavior(MaxParallelThreads = 4)]
[assembly: InternalsVisibleTo("Sunder.App.Integration.Tests")]
