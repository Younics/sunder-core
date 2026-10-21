using System.Diagnostics;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class GeneratedOutputLockRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sunder-lock-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _markers = [];

    [Fact]
    public void NewEvidenceUsesPrivateUserProfileInsteadOfJobTemp()
    {
        var output = Path.Combine(_root, "output");
        using var lease = GeneratedOutputLock.Acquire([output]);
        var owner = ReadOwner(output);
        var marker = owner["processMarkerPath"]!.GetValue<string>();
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sunder", "generated-output", "processes", owner["processMarkerToken"]!.GetValue<string>() + ".json");
        Assert.Equal(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(expected), marker);
        Assert.Equal(2, owner["schemaVersion"]!.GetValue<int>());
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(marker))!["schemaVersion"]!.GetValue<int>());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(0, (int)File.GetUnixFileMode(Path.GetDirectoryName(marker)!) & 0x3f);
            Assert.Equal(0, (int)File.GetUnixFileMode(marker) & 0x3f);
        }
    }

    [Fact]
    public void StableEvidenceReclaimsDeadOutputAndTransition()
    {
        var output = Path.Combine(_root, "output");
        var owner = OwnerTemplate();
        owner["pid"] = int.MaxValue;
        PublishOwner(output + GeneratedOutputLock.LockSuffix, owner);
        PublishOwner(output + GeneratedOutputLock.LockSuffix + ".transition", owner.DeepClone().AsObject());
        using (GeneratedOutputLock.Acquire([output], TimeSpan.FromSeconds(5))) { }
        Assert.False(Directory.Exists(output + GeneratedOutputLock.LockSuffix));
        Assert.Single(Directory.EnumerateDirectories(_root, "output.sunder-output.lock.transition.stale-*"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("arbitrary-path")]
    [InlineData("symlink")]
    [InlineData("foreign-host")]
    public void UnverifiableEvidenceFailsPromptlyWithoutChangingLockOrOutput(string damage)
    {
        var output = Path.Combine(_root, "output");
        var owner = OwnerTemplate();
        owner["pid"] = int.MaxValue;
        if (damage == "foreign-host") owner["hostname"] = "foreign.invalid";
        var lockPath = output + GeneratedOutputLock.LockSuffix;
        var markerPath = PublishOwner(lockPath, owner);
        if (damage == "missing") File.Delete(markerPath);
        if (damage == "tampered") File.WriteAllText(markerPath, "{}");
        if (damage is "arbitrary-path" or "symlink")
        {
            var arbitrary = Path.Combine(_root, "arbitrary.json");
            File.Move(markerPath, arbitrary);
            if (damage == "symlink") File.CreateSymbolicLink(markerPath, arbitrary);
            else
            {
                owner["processMarkerPath"] = arbitrary;
                WriteOwner(lockPath, owner);
            }
        }
        File.WriteAllText(output, "user output");
        var before = File.ReadAllBytes(Path.Combine(lockPath, "owner.json"));
        var clock = Stopwatch.StartNew();
        var error = Assert.Throws<IOException>(() => GeneratedOutputLock.Acquire([output]));
        Assert.Contains("unverifiable ownership evidence", error.Message);
        Assert.Contains("independently confirm", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), error.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(lockPath, "owner.json")));
        Assert.Equal("user output", File.ReadAllText(output));
    }

    [Fact]
    public void LiveHolderStillWaitsAndCancellationRemainsBounded()
    {
        var output = Path.Combine(_root, "output");
        var waiting = new List<string>();
        using var holder = GeneratedOutputLock.Acquire([output], waitingForOutput: waiting.Add);
        Assert.Empty(waiting);
        var before = File.ReadAllBytes(Path.Combine(output + GeneratedOutputLock.LockSuffix, "owner.json"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1400));
        Assert.ThrowsAny<OperationCanceledException>(() => GeneratedOutputLock.Acquire([output], cancellationToken: cancellation.Token,
            waitingForOutput: path =>
            {
                Assert.False(Directory.Exists(path + ".transition"));
                waiting.Add(path);
            }));
        Assert.Equal(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(output + GeneratedOutputLock.LockSuffix), Assert.Single(waiting));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(output + GeneratedOutputLock.LockSuffix, "owner.json")));
        holder.VerifyHeld();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedLegacyWorkerEvidenceFailsPromptlyForOutputAndTransition(bool transition)
    {
        var output = Path.Combine(_root, "output");
        var owner = OwnerTemplate();
        owner["pid"] = int.MaxValue;
        owner["processMarkerPath"] = Path.Combine(_root, "removed-worker-temp", "sunder-generated-output", "processes",
            owner["processMarkerToken"]!.GetValue<string>() + ".json");
        var directory = output + GeneratedOutputLock.LockSuffix + (transition ? ".transition" : "");
        WriteOwner(directory, owner);
        var before = File.ReadAllBytes(Path.Combine(directory, "owner.json"));
        var clock = Stopwatch.StartNew();
        var error = Assert.Throws<IOException>(() => GeneratedOutputLock.Acquire([output]));
        Assert.Contains("legacy Worker temp marker", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), error.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "owner.json")));
    }

    private JsonObject OwnerTemplate()
    {
        var output = Path.Combine(_root, "template");
        using var lease = GeneratedOutputLock.Acquire([output]);
        return ReadOwner(output);
    }

    private string PublishOwner(string directory, JsonObject owner)
    {
        owner["token"] = Guid.NewGuid().ToString("D");
        owner["processMarkerToken"] = Guid.NewGuid().ToString("D");
        var path = Path.Combine(Path.GetDirectoryName(owner["processMarkerPath"]!.GetValue<string>())!,
            owner["processMarkerToken"]!.GetValue<string>() + ".json");
        owner["processMarkerPath"] = path;
        var marker = owner.DeepClone().AsObject();
        marker["schemaVersion"] = 1;
        marker["token"] = owner["processMarkerToken"]!.GetValue<string>();
        marker["createdAt"] = DateTime.UtcNow.ToString("O");
        marker.Remove("processMarkerToken"); marker.Remove("processMarkerPath"); marker.Remove("acquiredAt");
        File.WriteAllText(path, marker.ToJsonString());
        _markers.Add(path);
        WriteOwner(directory, owner);
        return path;
    }

    private static void WriteOwner(string directory, JsonObject owner)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "owner.json"), owner.ToJsonString());
    }

    private static JsonObject ReadOwner(string output)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(output + GeneratedOutputLock.LockSuffix, "owner.json")))!.AsObject();

    public void Dispose()
    {
        foreach (var marker in _markers) File.Delete(marker);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
