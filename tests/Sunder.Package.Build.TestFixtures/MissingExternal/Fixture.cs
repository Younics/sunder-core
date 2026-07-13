using Sunder.Package.Build.Tests.Fixtures.ExternalDependency;
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(Id = "test.missing.external", Name = "Missing External Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.MissingExternal;

public sealed class ExternalConsumer : ExternalBase
{
    public string CallExternal(string value) => ExternalApi.Normalize(value);
}
