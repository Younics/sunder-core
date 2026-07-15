using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "test.source.reference.package",
    Name = "Source Reference Package",
    Summary = "Exercises source-reference SDK build outputs.")]

namespace Sunder.Package.Build.Tests.Fixtures.SourceReferencePackage;

public static class PackageMarker;
