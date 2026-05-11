using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class DevPackageManifestValidatorTests
{
    [Fact]
    public void Validate_WhenRequiredFieldsAreMissing_ReturnsExpectedErrors()
    {
        var errors = DevPackageManifestValidator.Validate(new DevPackageManifest(), CreateTempDirectory());

        Assert.Contains(errors, error => error.Contains("'manifestVersion' 1", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("missing 'id'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("missing 'name'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("missing 'version'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("missing 'entryAssembly'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenManifestReferencesMissingEntryAssembly_ReturnsEntryAssemblyError()
    {
        var shadowFolder = CreateTempDirectory();
        var manifest = CreateManifest(entryAssembly: "Missing.Package.dll");

        var errors = DevPackageManifestValidator.Validate(manifest, shadowFolder);

        Assert.Contains(errors, error => error.Contains("missing entry assembly 'Missing.Package.dll'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenManifestIsComplete_ReturnsNoErrors()
    {
        var shadowFolder = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(shadowFolder, "lib"));
        File.WriteAllText(Path.Combine(shadowFolder, "lib", "Test.Package.dll"), string.Empty);

        var errors = DevPackageManifestValidator.Validate(CreateManifest(), shadowFolder);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenSdkApiVersionIsUnsupported_ReturnsCompatibilityError()
    {
        var shadowFolder = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(shadowFolder, "lib"));
        File.WriteAllText(Path.Combine(shadowFolder, "lib", "Test.Package.dll"), string.Empty);

        var errors = DevPackageManifestValidator.Validate(CreateManifest(sdkApiVersion: 2), shadowFolder);

        Assert.Contains(errors, error => error.Contains("requires SDK API version 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenSdkCapabilityIsUnsupported_ReturnsCompatibilityError()
    {
        var shadowFolder = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(shadowFolder, "lib"));
        File.WriteAllText(Path.Combine(shadowFolder, "lib", "Test.Package.dll"), string.Empty);

        var errors = DevPackageManifestValidator.Validate(
            CreateManifest(requiredSdkCapabilities: ["callbacks.v2"]),
            shadowFolder);

        Assert.Contains(errors, error => error.Contains("requires SDK capability 'callbacks.v2'", StringComparison.Ordinal));
    }

    private static DevPackageManifest CreateManifest(
        string entryAssembly = "Test.Package.dll",
        int? sdkApiVersion = null,
        IReadOnlyList<string>? requiredSdkCapabilities = null)
        => new()
        {
            ManifestVersion = 1,
            Id = "test.package",
            Name = "Test Package",
            Version = "1.0.0",
            EntryAssembly = entryAssembly,
            SdkApiVersion = sdkApiVersion,
            RequiredSdkCapabilities = requiredSdkCapabilities,
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
