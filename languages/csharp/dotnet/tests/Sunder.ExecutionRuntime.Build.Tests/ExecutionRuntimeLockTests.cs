using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Sunder.ExecutionRuntime.Build.Tests;

public sealed class ExecutionRuntimeLockTests
{
    [Fact]
    public async Task FixtureLock_ContainsTheExactCuratedMatrix()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(
            FixtureArtifacts.LockPath,
            ExecutionRuntimeLockPolicy.TestFixtureKind);

        Assert.Equal(12, document.Entries!.Count);
        Assert.Equal(
            [
                "sunder.runtime.dotnet/10.0.1/win-x64",
                "sunder.runtime.dotnet/10.0.1/win-arm64",
                "sunder.runtime.dotnet/10.0.1/linux-x64",
                "sunder.runtime.dotnet/10.0.1/linux-arm64",
                "sunder.runtime.dotnet/10.0.1/osx-x64",
                "sunder.runtime.dotnet/10.0.1/osx-arm64",
                "sunder.runtime.node/24.0.0/win-x64",
                "sunder.runtime.node/24.0.0/win-arm64",
                "sunder.runtime.node/24.0.0/linux-x64",
                "sunder.runtime.node/24.0.0/linux-arm64",
                "sunder.runtime.node/24.0.0/osx-x64",
                "sunder.runtime.node/24.0.0/osx-arm64",
            ],
            document.Entries.Select(static entry => $"{entry!.Id}/{entry.Version}/{entry.Rid}"));
        Assert.All(document.Entries, static entry =>
        {
            Assert.Equal("test-only", entry!.Trust!.Scope);
            Assert.Equal("synthetic-fixture", entry.Trust.SourceApproval!.Status);
            Assert.EndsWith(".invalid", new Uri(entry.Source!.Uri!).Host, StringComparison.Ordinal);
            Assert.Equal("fixture-json", entry.Source.ArchiveFormat);
            Assert.Contains(".test.", entry.Trust.ReleaseSigningKeyId, StringComparison.Ordinal);
            Assert.Contains(".test.", entry.Trust.AttestationSigningKeyId, StringComparison.Ordinal);
            Assert.Equal(1, entry.MinimumHostTrustGeneration);
            Assert.Equal(ExecutionRuntimeLockPolicy.LongTermSupportLine, entry.SupportLine);
            Assert.Equal(ExecutionRuntimeLockPolicy.SupportedDefaultStatus, entry.DefaultStatus);
        });
    }

    [Fact]
    public async Task FixtureSources_RegenerateEveryExactLockedDigest()
    {
        var document = await ExecutionRuntimeLockLoader.LoadAsync(FixtureArtifacts.LockPath);
        var expectedLicenseHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(FixtureArtifacts.LicenseText))).ToLowerInvariant();

        foreach (var entry in document.Entries!)
        {
            var actualSourceHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(FixtureArtifacts.CreateSourceBytes(entry!)))
                .ToLowerInvariant();
            Assert.Equal(entry!.Source!.Sha256, actualSourceHash);
            Assert.Equal(expectedLicenseHash, entry.License!.Files![0]!.Sha256);
        }
    }

    [Fact]
    public void Schema_ExplicitlyRequiresTheTwelveEntryCoverageContract()
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(FixtureArtifacts.SchemaPath));
        var root = schema.RootElement;

        Assert.Equal(ExecutionRuntimeLockPolicy.SchemaUri, root.GetProperty("$id").GetString());
        var entries = root.GetProperty("properties").GetProperty("entries");
        Assert.Equal(12, entries.GetProperty("minItems").GetInt32());
        Assert.Equal(12, entries.GetProperty("maxItems").GetInt32());
        var requiredEntryProperties = root.GetProperty("$defs").GetProperty("entry").GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Contains("minimumHostTrustGeneration", requiredEntryProperties);
        Assert.Contains("supportLine", requiredEntryProperties);
        Assert.Contains("defaultStatus", requiredEntryProperties);
        var coverage = root.GetProperty("properties").GetProperty("coverage").GetProperty("properties");
        Assert.Equal(2, coverage.GetProperty("runtimes").GetProperty("minItems").GetInt32());
        Assert.Equal(6, coverage.GetProperty("rids").GetProperty("minItems").GetInt32());
    }

    [Fact]
    public async Task FixtureLock_IsRejectedWhenProductionIsRequired()
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ExecutionRuntimeLockLoader.LoadAsync(
                FixtureArtifacts.LockPath,
                ExecutionRuntimeLockPolicy.ProductionKind));

        Assert.Contains("does not match required kind 'production'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loader_RejectsUnknownAndDuplicateProperties()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var original = await File.ReadAllTextAsync(FixtureArtifacts.LockPath);
            var unknownPath = Path.Combine(root, "unknown.json");
            var unknown = JsonNode.Parse(original)!.AsObject();
            unknown["unreviewed"] = true;
            await File.WriteAllTextAsync(unknownPath, unknown.ToJsonString());
            var unknownException = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeLockLoader.LoadAsync(unknownPath));
            Assert.Contains("could not be mapped", unknownException.Message, StringComparison.OrdinalIgnoreCase);

            var duplicatePath = Path.Combine(root, "duplicate.json");
            await File.WriteAllTextAsync(
                duplicatePath,
                original.Replace(
                    "\"schemaVersion\": 1,",
                    "\"schemaVersion\": 1, \"schemaVersion\": 1,",
                    StringComparison.Ordinal));
            var duplicateException = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeLockLoader.LoadAsync(duplicatePath));
            Assert.Contains("Duplicate JSON property 'schemaVersion'", duplicateException.Message, StringComparison.Ordinal);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("version", "11.0.0", "major-10")]
    [InlineData("sha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "lower-case SHA-256")]
    [InlineData("scope", "production", "trust.scope")]
    [InlineData("archiveFormat", "zip", "archiveFormat")]
    [InlineData("minimumHostTrustGeneration", "0", "must be positive")]
    [InlineData("minimumHostTrustGeneration", "9007199254740992", "safe JSON integer range")]
    [InlineData("supportLine", "current", "must be 'lts'")]
    [InlineData("defaultStatus", "staged", "must be 'supported'")]
    public async Task Loader_RejectsCrossFieldSemanticDrift(string field, string value, string expectedError)
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(FixtureArtifacts.LockPath))!.AsObject();
            var entry = document["entries"]!.AsArray()[0]!.AsObject();
            switch (field)
            {
                case "version":
                    entry["version"] = value;
                    break;
                case "sha256":
                    entry["source"]![field] = value;
                    break;
                case "scope":
                    entry["trust"]![field] = value;
                    break;
                case "archiveFormat":
                    entry["source"]![field] = value;
                    break;
                case "minimumHostTrustGeneration":
                    entry[field] = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "supportLine":
                case "defaultStatus":
                    entry[field] = value;
                    break;
            }
            var path = Path.Combine(root, "invalid.json");
            await File.WriteAllTextAsync(path, document.ToJsonString());

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeLockLoader.LoadAsync(path));
            Assert.Contains(expectedError, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Loader_RejectsRecipeThatExcludesItsLauncher()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(FixtureArtifacts.LockPath))!.AsObject();
            var recipe = document["entries"]!.AsArray()[0]!["recipe"]!.AsObject();
            recipe["excludedSourcePaths"] = new JsonArray("dotnet.exe");
            var path = Path.Combine(root, "excluded-launcher.json");
            await File.WriteAllTextAsync(path, document.ToJsonString());

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExecutionRuntimeLockLoader.LoadAsync(path));
            Assert.Contains("must not remove the launcher", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Cli_HasBuildSurfaceButNoPrivateKeyOption()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var privateKeyExit = await ExecutionRuntimeBuildCli.RunAsync(
            [
                "validate-lock",
                "--lock", FixtureArtifacts.LockPath,
                "--kind", "test-fixture",
                "--private-key", "forbidden",
            ],
            output,
            error);
        var buildExit = await ExecutionRuntimeBuildCli.RunAsync(["build"], output, error);

        Assert.Equal(2, privateKeyExit);
        Assert.Equal(2, buildExit);
        Assert.Contains("Unknown option '--private-key'", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Every command option requires exactly one value", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Sunder.ExecutionRuntime.Build build", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_BindsNativeLaneToTheExactLockedRunnerProfile()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var accepted = await ExecutionRuntimeBuildCli.RunAsync(
            [
                "validate-native-lane",
                "--lock", FixtureArtifacts.LockPath,
                "--kind", "test-fixture",
                "--rid", "linux-arm64",
                "--runner-profile", "fixture-linux-arm64",
            ],
            output,
            error);
        var rejected = await ExecutionRuntimeBuildCli.RunAsync(
            [
                "validate-native-lane",
                "--lock", FixtureArtifacts.LockPath,
                "--kind", "test-fixture",
                "--rid", "linux-arm64",
                "--runner-profile", "fixture-linux-x64",
            ],
            output,
            error);

        Assert.Equal(0, accepted);
        Assert.Equal(1, rejected);
        Assert.Contains("is not bound to runner profile", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalSigner_RejectsAdapterOutsidePinnedSha256Identity()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var adapterPath = Path.Combine(root, "adapter.bin");
            await File.WriteAllTextAsync(adapterPath, "not the approved adapter");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ExternalExecutionRuntimeSigner.CreateAsync(
                    "sunder.release.test.v1",
                    adapterPath,
                    new string('a', 64)));

            Assert.Contains("SHA-256 mismatch", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExternalSigner_UsesBoundedRawStdinStdoutProtocol()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var adapterPath = Path.Combine(root, "adapter.sh");
            await File.WriteAllTextAsync(
                adapterPath,
                """
                #!/usr/bin/env bash
                set -euo pipefail
                [[ "$#" -eq 3 && "$1" == "sign-ed25519" && "$2" == "--key-id" && "$3" == "sunder.release.test.v1" ]]
                [[ "$(od -An -t x1 | tr -d ' \n')" == "01020304" ]]
                dd if=/dev/zero bs=64 count=1 2>/dev/null
                """);
            File.SetUnixFileMode(adapterPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var adapterSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(adapterPath)))
                .ToLowerInvariant();

            using var signer = await ExternalExecutionRuntimeSigner.CreateAsync(
                "sunder.release.test.v1",
                adapterPath,
                adapterSha256);
            var signature = signer.Sign([1, 2, 3, 4]);

            Assert.Equal(64, signature.Length);
            Assert.All(signature, static value => Assert.Equal((byte)0, value));
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }
}
