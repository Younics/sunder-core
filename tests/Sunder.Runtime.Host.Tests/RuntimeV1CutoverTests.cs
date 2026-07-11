using Sunder.Runtime.Client;
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
            RuntimeConnectionInfoStore.GetDefaultPath(),
        }, path => Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.Ordinal));
        Assert.EndsWith(Path.Combine("runtime", "v1", "connection.json"), RuntimeConnectionInfoStore.GetDefaultPath(), StringComparison.Ordinal);
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
}
