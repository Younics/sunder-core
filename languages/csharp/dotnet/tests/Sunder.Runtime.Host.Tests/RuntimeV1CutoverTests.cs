using Sunder.Runtime.Client;
using Sunder.Runtime.LocalState;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeV1CutoverTests
{
    [Fact]
    public void Default_layout_places_every_runtime_category_under_one_v1_root()
    {
        var paths = new RuntimePackagePaths();
        var root = Path.GetFullPath(RuntimeLocalState.GetV1RootPath());

        Assert.Equal(root, paths.RootPath);
        Assert.All(new[]
        {
            paths.CatalogRootPath,
            paths.InstalledRootPath,
            paths.StagingRootPath,
            paths.TransactionRootPath,
            paths.TombstoneRootPath,
            paths.TransferRootPath,
            paths.PackageDataRootPath,
            paths.RegistryCredentialRootPath,
            paths.StateFilePath,
            paths.LeaseFilePath,
            paths.ConnectionInfoFilePath,
            RuntimeConnectionInfoStore.GetDefaultPath(),
        }, path => Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.Ordinal));
        Assert.EndsWith(Path.Combine("runtime", "v1", "connection.json"), RuntimeConnectionInfoStore.GetDefaultPath(), StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_runtime_root_owns_connection_metadata_after_environment_is_cleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-path-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(root);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "connection.json"), paths.ConnectionInfoFilePath);
    }

    [Fact]
    public void Reset_challenge_is_one_time_and_rejects_wrong_confirmation()
    {
        var service = new RuntimeResetChallengeService();
        var first = service.Create();

        Assert.False(service.TryConsume("wrong"));
        Assert.False(service.TryConsume(first.Challenge));

        var second = service.Create();
        Assert.True(service.TryConsume(second.Challenge));
        Assert.False(service.TryConsume(second.Challenge));
    }

    [Fact]
    public void Credential_provider_namespace_is_runtime_v1_specific()
    {
        Assert.Equal(
            "io.sunder.runtime.v1.package-storage.master-key",
            MacOsKeychainMasterKeyStore.Service);
    }

    [Fact]
    public void Interrupted_schema_temp_is_recovered_during_startup()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-local-state-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var staleTemp = Path.Combine(root, $".{RuntimeLocalState.SchemaFileName}.interrupted.tmp");
        File.WriteAllText(staleTemp, "{truncated");
        try
        {
            RuntimeLocalState.Validate(root);
            RuntimeLocalState.EnsureInitialized(root);

            Assert.True(File.Exists(Path.Combine(root, RuntimeLocalState.SchemaFileName)));
            Assert.False(File.Exists(staleTemp));
            RuntimeLocalState.Validate(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Local_state_descriptor_collections_are_frozen()
    {
        var category = RuntimeV1StateDescriptor.ResetCategories[0];

        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)category.RelativePaths)[0] = "mutated");
        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<string>)RuntimeV1StateDescriptor.KnownRootEntries).Add("mutated"));
    }
}
