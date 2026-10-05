using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.ExecutionRuntime.Build.Tests;

public sealed class DevelopmentExecutionRuntimeKeyStoreTests
{
    [Fact]
    public void Ensure_IsIdempotentAndPersistsOnlyPublicMetadataOutsidePrivateKeyFiles()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        var stateRoot = Path.Combine(root, "keys");
        try
        {
            var first = DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot);
            var second = DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot);

            Assert.Equal(first, second);
            Assert.Equal(DevelopmentExecutionRuntimeKeyStore.CurrentSchemaVersion, first.SchemaVersion);
            Assert.Equal(1, first.Generation);
            Assert.NotEqual(first.ReleaseKey.KeyId, first.NativeAttestationKey.KeyId);
            Assert.NotEqual(first.ReleaseKey.PublicKeyBase64, first.NativeAttestationKey.PublicKeyBase64);
            AssertPublicKey(first.ReleaseKey, "sunder.runtime.development.release.");
            AssertPublicKey(
                first.NativeAttestationKey,
                "sunder.runtime.development.native-attestation.");

            var stateJson = File.ReadAllText(
                Path.Combine(stateRoot, DevelopmentExecutionRuntimeKeyStore.StateFileName));
            Assert.DoesNotContain("private", stateJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(stateRoot, stateJson, StringComparison.Ordinal);

            var generationDirectory = DevelopmentExecutionRuntimeKeyStore.GetGenerationDirectory(
                stateRoot,
                first.Generation);
            var expectedFiles = new[]
            {
                DevelopmentExecutionRuntimeKeyStore.NativeAttestationPrivateKeyFileName,
                DevelopmentExecutionRuntimeKeyStore.NativeAttestationPublicKeyFileName,
                DevelopmentExecutionRuntimeKeyStore.ReleasePrivateKeyFileName,
                DevelopmentExecutionRuntimeKeyStore.ReleasePublicKeyFileName,
                DevelopmentExecutionRuntimeKeyStore.GenerationTrustFileName,
            };
            Assert.Equal(
                expectedFiles,
                Directory.EnumerateFiles(generationDirectory)
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
            Assert.Equal(
                32,
                new FileInfo(Path.Combine(
                    generationDirectory,
                    DevelopmentExecutionRuntimeKeyStore.ReleasePrivateKeyFileName)).Length);
            AssertSecurePermissions(stateRoot);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public void SigningSession_UsesDistinctKeysAndZeroesPrivateMaterialOnDispose()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        var stateRoot = Path.Combine(root, "keys");
        try
        {
            var input = Encoding.UTF8.GetBytes("development-signature-input");
            var session = DevelopmentExecutionRuntimeKeyStore.OpenSigningSession(stateRoot);
            var releaseSignature = session.Sign(input);
            var attestationSignature = session.SignNativeAttestation(input);
            var verifier = new SunderExecutionRuntimeEd25519SignatureVerifier();

            Assert.True(verifier.Verify(
                input,
                releaseSignature,
                Convert.FromBase64String(session.Trust.ReleaseKey.PublicKeyBase64)));
            Assert.True(verifier.Verify(
                input,
                attestationSignature,
                Convert.FromBase64String(session.Trust.NativeAttestationKey.PublicKeyBase64)));
            Assert.False(verifier.Verify(
                input,
                releaseSignature,
                Convert.FromBase64String(session.Trust.NativeAttestationKey.PublicKeyBase64)));

            session.Dispose();
            Assert.Throws<ObjectDisposedException>(() => session.Sign(input));
            Assert.Throws<ObjectDisposedException>(() => session.SignNativeAttestation(input));
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public void RotateAndReset_AdvanceTrustExplicitlyAndNeverReuseKeyIds()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        var stateRoot = Path.Combine(root, "keys");
        try
        {
            var initial = DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot);
            var rotated = DevelopmentExecutionRuntimeKeyStore.Rotate(stateRoot);

            Assert.Equal(2, rotated.Generation);
            Assert.NotEqual(initial.ReleaseKey.KeyId, rotated.ReleaseKey.KeyId);
            Assert.NotEqual(
                initial.NativeAttestationKey.KeyId,
                rotated.NativeAttestationKey.KeyId);
            Assert.Equal(rotated, DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot));
            Assert.True(Directory.Exists(
                DevelopmentExecutionRuntimeKeyStore.GetGenerationDirectory(stateRoot, 1)));
            Assert.True(Directory.Exists(
                DevelopmentExecutionRuntimeKeyStore.GetGenerationDirectory(stateRoot, 2)));

            DevelopmentExecutionRuntimeKeyStore.Reset(stateRoot);
            Assert.False(Directory.Exists(stateRoot));
            var replacement = DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot);
            Assert.Equal(1, replacement.Generation);
            Assert.NotEqual(rotated.ReleaseKey.KeyId, replacement.ReleaseKey.KeyId);
            Assert.NotEqual(
                rotated.NativeAttestationKey.KeyId,
                replacement.NativeAttestationKey.KeyId);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ConcurrentEnsure_ConvergesOnOneGeneration()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        var stateRoot = Path.Combine(root, "keys");
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
                () => DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot))));

            Assert.All(results, result => Assert.Equal(results[0], result));
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(
                stateRoot,
                DevelopmentExecutionRuntimeKeyStore.GenerationsDirectoryName)));
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Ensure_RejectsMalformedOrUnexpectedStateWithoutRegenerating()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        try
        {
            var malformedRoot = Path.Combine(root, "malformed");
            var initial = DevelopmentExecutionRuntimeKeyStore.Ensure(malformedRoot);
            File.WriteAllText(
                Path.Combine(malformedRoot, DevelopmentExecutionRuntimeKeyStore.StateFileName),
                "{}");
            Assert.Throws<InvalidDataException>(
                () => DevelopmentExecutionRuntimeKeyStore.Ensure(malformedRoot));
            Assert.True(Directory.Exists(
                DevelopmentExecutionRuntimeKeyStore.GetGenerationDirectory(
                    malformedRoot,
                    initial.Generation)));

            var unexpectedRoot = Path.Combine(root, "unexpected");
            _ = DevelopmentExecutionRuntimeKeyStore.Ensure(unexpectedRoot);
            File.WriteAllText(Path.Combine(unexpectedRoot, "unexpected.txt"), "unmanaged");
            Assert.Throws<InvalidDataException>(
                () => DevelopmentExecutionRuntimeKeyStore.Ensure(unexpectedRoot));
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Ensure_RejectsLinkedState()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        if (OperatingSystem.IsWindows())
        {
            FixtureArtifacts.DeleteDirectory(root);
            return;
        }
        try
        {
            var stateRoot = Path.Combine(root, "keys");
            _ = DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot);
            var statePath = Path.Combine(stateRoot, DevelopmentExecutionRuntimeKeyStore.StateFileName);
            var outsidePath = Path.Combine(root, "outside.json");
            File.Move(statePath, outsidePath);
            File.CreateSymbolicLink(statePath, outsidePath);

            Assert.Throws<InvalidDataException>(
                () => DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot));
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Ensure_RejectsGroupOrWorldAccessibleManagedFiles()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        if (OperatingSystem.IsWindows())
        {
            FixtureArtifacts.DeleteDirectory(root);
            return;
        }
        try
        {
            var stateRoot = Path.Combine(root, "keys");
            var trust = DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot);
            var privateKeyPath = Path.Combine(
                DevelopmentExecutionRuntimeKeyStore.GetGenerationDirectory(stateRoot, trust.Generation),
                DevelopmentExecutionRuntimeKeyStore.ReleasePrivateKeyFileName);
            File.SetUnixFileMode(
                privateKeyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

            Assert.Throws<InvalidDataException>(
                () => DevelopmentExecutionRuntimeKeyStore.Ensure(stateRoot));
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DevelopmentKeyCli_EmitsPublicProjectionAndProductionBuildDoesNotFallback()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        var stateRoot = Path.Combine(root, "keys");
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var ensureExit = await ExecutionRuntimeBuildCli.RunAsync(
                ["development-keys", "ensure", "--state-root", stateRoot],
                output,
                error);

            Assert.Equal(0, ensureExit);
            Assert.Empty(error.ToString());
            Assert.Contains("publicKeyBase64", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("private", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(stateRoot, output.ToString(), StringComparison.Ordinal);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var fixtureDocument = await ExecutionRuntimeLockLoader.LoadAsync(
                FixtureArtifacts.LockPath,
                ExecutionRuntimeLockPolicy.TestFixtureKind);
            var fixtureEntry = fixtureDocument.Entries![0]!;
            var productionExit = await ExecutionRuntimeBuildCli.RunAsync(
                [
                    "build",
                    "--lock", FixtureArtifacts.LockPath,
                    "--kind", ExecutionRuntimeLockPolicy.TestFixtureKind,
                    "--id", fixtureEntry.Id!,
                    "--version", fixtureEntry.Version!,
                    "--rid", fixtureEntry.Rid!,
                    "--source", "source.tar.gz",
                    "--native-attestation", "attestation.json",
                    "--output", "output.sunderruntime",
                ],
                output,
                error);
            Assert.Equal(2, productionExit);
            Assert.Contains("--release-public-key", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    [Fact]
    public void PublicConfiguration_ProjectsOnlyPublicTrustIntoRegistryAndHostEnvironments()
    {
        var root = FixtureArtifacts.CreateTempDirectory();
        var stateRoot = Path.Combine(root, "keys");
        try
        {
            var trust = DevelopmentExecutionRuntimeKeyStore.Rotate(stateRoot);

            var registry = DevelopmentExecutionRuntimePublicConfiguration.CreateRegistryEnvironment(trust);
            var host = DevelopmentExecutionRuntimePublicConfiguration.CreateHostEnvironment(
                trust,
                allowInsecureLoopbackRegistry: true);

            Assert.Equal(6, registry.Count);
            Assert.Equal(4, host.Count);
            Assert.Equal(
                trust.ReleaseKey.PublicKeyBase64,
                registry[$"Registry__ExecutionRuntimes__Development__ReleaseKeys__{trust.ReleaseKey.KeyId}__PublicKeyBase64"]);
            Assert.Equal(
                trust.NativeAttestationKey.PublicKeyBase64,
                registry[$"Registry__ExecutionRuntimes__Development__NativeAttestationKeys__{trust.NativeAttestationKey.KeyId}__PublicKeyBase64"]);
            Assert.Equal(
                "1",
                registry[$"Registry__ExecutionRuntimes__Development__ReleaseKeys__{trust.ReleaseKey.KeyId}__MinimumHostTrustGeneration"]);
            Assert.Equal(
                "0",
                registry[$"Registry__ExecutionRuntimes__Development__NativeAttestationKeys__{trust.NativeAttestationKey.KeyId}__MinimumHostTrustGeneration"]);
            Assert.Equal("1", registry["Registry__ExecutionRuntimes__MinimumSupportedHostTrustGeneration"]);
            Assert.Equal("1", registry["Registry__ExecutionRuntimes__TrustPolicyGeneration"]);
            Assert.Equal(
                trust.ReleaseKey.KeyId,
                host[DevelopmentExecutionRuntimePublicConfiguration.HostReleaseKeyIdVariable]);
            Assert.Equal(
                trust.ReleaseKey.PublicKeyBase64,
                host[DevelopmentExecutionRuntimePublicConfiguration.HostReleasePublicKeyVariable]);
            Assert.Equal(
                "1",
                host[DevelopmentExecutionRuntimePublicConfiguration.HostTrustGenerationVariable]);
            Assert.Equal(
                "true",
                host[DevelopmentExecutionRuntimePublicConfiguration.HostAllowInsecureLoopbackRegistryVariable]);
            Assert.All(registry.Concat(host), pair =>
            {
                Assert.DoesNotContain("PRIVATE", pair.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("STATE_ROOT", pair.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(stateRoot, pair.Value, StringComparison.Ordinal);
            });
        }
        finally
        {
            FixtureArtifacts.DeleteDirectory(root);
        }
    }

    private static void AssertPublicKey(
        DevelopmentExecutionRuntimePublicKey key,
        string expectedIdPrefix)
    {
        var publicKey = Convert.FromBase64String(key.PublicKeyBase64);
        var fingerprint = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        Assert.Equal(SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes, publicKey.Length);
        Assert.Equal(fingerprint, key.PublicKeySha256);
        Assert.Equal(expectedIdPrefix + fingerprint[..24], key.KeyId);
    }

    private static void AssertSecurePermissions(string stateRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var privateDirectoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var privateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(privateDirectoryMode, File.GetUnixFileMode(stateRoot));
        Assert.Equal(
            privateDirectoryMode,
            File.GetUnixFileMode(Path.Combine(
                stateRoot,
                DevelopmentExecutionRuntimeKeyStore.GenerationsDirectoryName)));
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     DevelopmentExecutionRuntimeKeyStore.GetGenerationDirectory(stateRoot, 1)))
        {
            Assert.Equal(privateFileMode, File.GetUnixFileMode(path));
        }
        Assert.Equal(
            privateFileMode,
            File.GetUnixFileMode(Path.Combine(
                stateRoot,
                DevelopmentExecutionRuntimeKeyStore.StateFileName)));
    }
}
