using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.template",
    Name = "Sunder Package Template",
    Summary = "Adds a custom Sunder package extension.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.host.package",
    VersionRange = ">=1.0.0")]
