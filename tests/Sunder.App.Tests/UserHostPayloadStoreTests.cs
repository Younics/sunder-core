using Sunder.App.Services;
using Sunder.Host.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class UserHostPayloadStoreTests
{
    [Fact]
    public void Prepare_StagesCompletePayloadOutsideAppDirectory()
    {
        var root = CreateRoot();
        try
        {
            var source = CreatePayloadSource(root, "source-v1", "one");
            var payloadRoot = Path.Combine(root, "user-host", "payloads");
            var store = new UserHostPayloadStore(source, payloadRoot, "1.2.3");

            using var payload = store.Prepare();

            Assert.StartsWith(Path.GetFullPath(payloadRoot), payload.ExecutablePath, PathComparison);
            Assert.True(File.Exists(payload.ExecutablePath));
            Assert.Equal("nested-one", File.ReadAllText(Path.Combine(payload.DirectoryPath, "RuntimeHost", "worker.dat")));
            Assert.Null(payload.Previous);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Commit_AdvancesDescriptorAndRemovesPreviousPayload()
    {
        var root = CreateRoot();
        try
        {
            var payloadRoot = Path.Combine(root, "user-host", "payloads");
            var firstStore = new UserHostPayloadStore(
                CreatePayloadSource(root, "source-v1", "one"),
                payloadRoot,
                "1.0.0");
            var first = firstStore.Prepare();
            firstStore.Commit(first);

            var secondStore = new UserHostPayloadStore(
                CreatePayloadSource(root, "source-v2", "two"),
                payloadRoot,
                "2.0.0");
            var second = secondStore.Prepare();

            Assert.Equal(first.DirectoryPath, second.Previous?.DirectoryPath);
            Assert.True(Directory.Exists(first.DirectoryPath));

            secondStore.Commit(second);

            Assert.False(Directory.Exists(first.DirectoryPath));
            Assert.True(Directory.Exists(second.DirectoryPath));
            Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(payloadRoot, "current.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Prepare_RejectsLinksInBundledPayload()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot();
        try
        {
            var source = CreatePayloadSource(root, "source", "one");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(Path.Combine(source, "unsafe-link"), outside);
            var store = new UserHostPayloadStore(source, Path.Combine(root, "payloads"), "1.0.0");

            var exception = Assert.Throws<InvalidDataException>(() => store.Prepare());

            Assert.Contains("unsupported link", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Prepare_WhenStagedPayloadIsCorrupted_RestagesBundledContent()
    {
        var root = CreateRoot();
        try
        {
            var source = CreatePayloadSource(root, "source", "original");
            var payloadRoot = Path.Combine(root, "payloads");
            var store = new UserHostPayloadStore(source, payloadRoot, "1.0.0");
            var first = store.Prepare();
            store.Commit(first);
            File.WriteAllText(first.ExecutablePath, "corrupted");

            var repaired = store.Prepare();

            Assert.Equal("original", File.ReadAllText(repaired.ExecutablePath));
            Assert.NotEqual(first.DirectoryPath, repaired.DirectoryPath);
            Assert.True(repaired.ReplacesCurrent);
            Assert.Null(repaired.Previous);

            store.Commit(repaired);

            Assert.False(Directory.Exists(first.DirectoryPath));
            Assert.True(Directory.Exists(repaired.DirectoryPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Matches_RejectsSameVersionHostWithoutRequiredLifecycleProtocol()
    {
        var root = CreateRoot();
        try
        {
            var store = new UserHostPayloadStore(
                CreatePayloadSource(root, "source", "one"),
                Path.Combine(root, "payloads"),
                "1.0.0");
            using var payload = store.Prepare();
            var handshake = new HostHandshakeResponse(
                HostProtocol.Identity,
                HostProtocol.CurrentRevision,
                HostProtocol.MinimumSupportedRevision,
                HostProtocol.MaximumSupportedRevision,
                Guid.NewGuid(),
                Guid.NewGuid(),
                [HostProtocolFeatures.RuntimeGatewayV1],
                new HostProductVersionDiagnostics("Sunder.Host.Supervisor", "1.0.0", "1.0.0"));

            Assert.False(store.Matches(payload, handshake));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Prepare_RejectsNonEmptyUnidentifiedPayloadRoot()
    {
        var root = CreateRoot();
        try
        {
            var payloadRoot = Path.Combine(root, "not-a-payload-root");
            Directory.CreateDirectory(payloadRoot);
            File.WriteAllText(Path.Combine(payloadRoot, "keep.txt"), "unrelated");
            var store = new UserHostPayloadStore(
                CreatePayloadSource(root, "source", "one"),
                payloadRoot,
                "1.0.0");

            var exception = Assert.Throws<InvalidDataException>(() => store.Prepare());

            Assert.Contains("non-empty", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(payloadRoot, "keep.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RemoveStagedPayloads_DeletesIdentifiedRootWhileExternalLockRemains()
    {
        var root = CreateRoot();
        try
        {
            var payloadRoot = Path.Combine(root, "user-host", "payloads");
            var store = new UserHostPayloadStore(
                CreatePayloadSource(root, "source", "one"),
                payloadRoot,
                "1.0.0");
            store.Commit(store.Prepare());

            Assert.False(store.InstallationLockPath.StartsWith(
                Path.GetFullPath(payloadRoot) + Path.DirectorySeparatorChar,
                PathComparison));
            Assert.True(UserHostPayloadStore.TryRemoveStagedPayloads(payloadRoot, TimeSpan.Zero));

            Assert.False(Directory.Exists(payloadRoot));
            Assert.True(File.Exists(store.InstallationLockPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RemoveStagedPayloads_WhenInstallationLockIsBusy_PreservesPayload()
    {
        var root = CreateRoot();
        try
        {
            var payloadRoot = Path.Combine(root, "user-host", "payloads");
            var store = new UserHostPayloadStore(
                CreatePayloadSource(root, "source", "one"),
                payloadRoot,
                "1.0.0");
            var payload = store.Prepare();
            store.Commit(payload);

            using (var held = new FileStream(
                       store.InstallationLockPath,
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                Assert.False(UserHostPayloadStore.TryRemoveStagedPayloads(payloadRoot, TimeSpan.Zero));
                Assert.True(Directory.Exists(payload.DirectoryPath));
            }

            Assert.True(UserHostPayloadStore.TryRemoveStagedPayloads(payloadRoot, TimeSpan.Zero));
            Assert.False(Directory.Exists(payloadRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DisposedActivationCannotCommitOrRecreateRemovedPayloadRoot()
    {
        var root = CreateRoot();
        try
        {
            var payloadRoot = Path.Combine(root, "user-host", "payloads");
            var store = new UserHostPayloadStore(
                CreatePayloadSource(root, "source", "one"),
                payloadRoot,
                "1.0.0");
            var payload = store.Prepare();
            payload.Dispose();
            Assert.True(UserHostPayloadStore.TryRemoveStagedPayloads(payloadRoot, TimeSpan.Zero));

            Assert.Throws<InvalidOperationException>(() => store.Commit(payload));
            store.Abandon(payload);

            Assert.False(Directory.Exists(payloadRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Prepare_HoldsActivationLockUntilTransactionCompletes()
    {
        var root = CreateRoot();
        try
        {
            var payloadRoot = Path.Combine(root, "user-host", "payloads");
            var firstStore = new UserHostPayloadStore(
                CreatePayloadSource(root, "source-v1", "one"),
                payloadRoot,
                "1.0.0");
            var secondStore = new UserHostPayloadStore(
                CreatePayloadSource(root, "source-v2", "two"),
                payloadRoot,
                "2.0.0");
            using var first = firstStore.Prepare();

            var exception = Assert.Throws<InvalidOperationException>(() => secondStore.Prepare());

            Assert.Contains("updating", exception.Message, StringComparison.OrdinalIgnoreCase);
            firstStore.Commit(first);
            using var second = secondStore.Prepare();
            secondStore.Commit(second);
            Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(payloadRoot, "current.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RemoveStagedPayloads_WhenRootIsUnidentified_PreservesContents()
    {
        var root = CreateRoot();
        try
        {
            var payloadRoot = Path.Combine(root, "not-a-payload-root");
            Directory.CreateDirectory(payloadRoot);
            var sentinel = Path.Combine(payloadRoot, "keep.txt");
            File.WriteAllText(sentinel, "unrelated");

            Assert.Throws<InvalidDataException>(() =>
                UserHostPayloadStore.TryRemoveStagedPayloads(payloadRoot, TimeSpan.Zero));

            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-user-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreatePayloadSource(string root, string name, string content)
    {
        var source = Path.Combine(root, name);
        var worker = Path.Combine(source, "RuntimeHost");
        Directory.CreateDirectory(worker);
        var executable = Path.Combine(
            source,
            OperatingSystem.IsWindows() ? "Sunder.Host.Supervisor.exe" : "Sunder.Host.Supervisor");
        File.WriteAllText(executable, content);
        File.WriteAllText(Path.Combine(worker, "worker.dat"), $"nested-{content}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return source;
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
