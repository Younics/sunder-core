using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimePackageManifestValidatorTests
{
    [Fact]
    public void Validate_WhenRequiredFieldsAreMissing_ReturnsExpectedErrors()
    {
        var errors = Validate(new SunderPackageManifest(), CreateTempDirectory());

        Assert.Contains(errors, error => error.Contains("manifestVersion 1", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Package id", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("missing name", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SemVer 2.0", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("missing entryAssembly", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("must declare hostRoles", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenManifestReferencesMissingEntryAssembly_ReturnsEntryAssemblyError()
    {
        var shadowFolder = CreateTempDirectory();
        var manifest = CreateManifest(entryAssembly: "Missing.Package.dll");

        var errors = Validate(manifest, shadowFolder);

        Assert.Contains(errors, error => error.Contains("missing entry assembly 'Missing.Package.dll'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenManifestIsComplete_ReturnsNoErrors()
    {
        var shadowFolder = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(shadowFolder, "lib"));
        File.Copy(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location, Path.Combine(shadowFolder, "lib", "Test.Package.dll"));

        var errors = Validate(CreateManifest(), shadowFolder);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenSdkApiVersionIsUnsupported_ReturnsCompatibilityError()
    {
        var shadowFolder = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(shadowFolder, "lib"));
        File.Copy(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location, Path.Combine(shadowFolder, "lib", "Test.Package.dll"));

        var errors = Validate(CreateManifest(sdkApiVersion: 2), shadowFolder);

        Assert.Contains(errors, error => error.Contains("requires SDK API version 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenSdkCapabilityIsUnsupported_ReturnsCompatibilityError()
    {
        var shadowFolder = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(shadowFolder, "lib"));
        File.Copy(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location, Path.Combine(shadowFolder, "lib", "Test.Package.dll"));

        var errors = Validate(
            CreateManifest(requiredSdkCapabilities: ["callbacks.v2"]),
            shadowFolder);

        Assert.Contains(errors, error => error.Contains("requires SDK capability 'callbacks.v2'", StringComparison.Ordinal));
    }

    private static SunderPackageManifest CreateManifest(
        string entryAssembly = "Test.Package.dll",
        int? sdkApiVersion = 1,
        IReadOnlyList<string>? requiredSdkCapabilities = null)
        => new()
        {
            ManifestVersion = 1,
            Id = "test.package",
            Name = "Test Package",
            Version = "1.0.0",
            EntryAssembly = entryAssembly,
            HostRoles = [SunderPackageFormat.AppHostRole, SunderPackageFormat.RuntimeHostRole],
            SdkApiVersion = sdkApiVersion,
            SdkPackageVersion = "1.1.0",
            RequiredSdkCapabilities = ["sdk-baseline-1-1.v1", .. requiredSdkCapabilities ?? ["core.v1"]],
        };

    private static IReadOnlyList<string> Validate(SunderPackageManifest manifest, string rootPath)
        => SunderPackageManifestValidator.Validate(manifest, rootPath, SunderPackageManifestLayout.Activation)
            .Concat(SunderSdkCompatibilityProfile.Validate(manifest))
            .ToArray();

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
