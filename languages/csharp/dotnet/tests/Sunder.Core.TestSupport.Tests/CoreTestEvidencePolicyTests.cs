using System.Text.Json;
using Xunit;

namespace Sunder.Core.TestSupport.Tests;

public sealed class CoreTestEvidencePolicyTests
{
    [Fact]
    [Trait("Category", "Contract")]
    [Trait("Capability", "Filesystem")]
    [Trait("Size", "Small")]
    public void CompactPolicyDeclaresLaneOwnersAndExpectedSkipReasons()
    {
        var repositoryRoot = FindRepositoryRoot();
        var policyPath = Path.Combine(repositoryRoot, "eng", "test-oracles", "core-test-policy.json");
        Assert.InRange(new FileInfo(policyPath).Length, 1, 16 * 1024 - 1);

        using var document = JsonDocument.Parse(File.ReadAllBytes(policyPath));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var lanes = root.GetProperty("primaryLanes")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(lanes);

        foreach (var project in root.GetProperty("projectLanes").EnumerateObject())
        {
            var owners = project.Value.EnumerateArray().Select(item => item.GetString()!).ToArray();
            Assert.NotEmpty(owners);
            Assert.Equal(owners.Length, owners.Distinct(StringComparer.Ordinal).Count());
            Assert.All(owners, owner => Assert.Contains(owner, lanes));
        }

        ValidateExpectedSkips(root.GetProperty("expectedSkips").EnumerateArray().Select(item => new ExpectedSkip(
            item.GetProperty("id").GetString()!,
            item.GetProperty("owner").GetString()!,
            item.GetProperty("reason").GetString()!)), lanes);
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("Capability", "Filesystem")]
    [Trait("Size", "Small")]
    public void ExpectedSkipPolicyRejectsASeededDuplicateIdentity()
    {
        var lanes = new HashSet<string>(["host-platform"], StringComparer.Ordinal);
        var mutation = new[]
        {
            new ExpectedSkip("seeded", "host-platform", "first"),
            new ExpectedSkip("seeded", "host-platform", "duplicate"),
        };

        var exception = Assert.Throws<InvalidDataException>(() => ValidateExpectedSkips(mutation, lanes));
        Assert.Contains("seeded", exception.Message, StringComparison.Ordinal);
    }

    private static void ValidateExpectedSkips(IEnumerable<ExpectedSkip> expectedSkips, IReadOnlySet<string> lanes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expectedSkip in expectedSkips)
        {
            if (!ids.Add(expectedSkip.Id))
            {
                throw new InvalidDataException($"Expected skip '{expectedSkip.Id}' is declared more than once.");
            }

            if (!lanes.Contains(expectedSkip.Owner) || string.IsNullOrWhiteSpace(expectedSkip.Reason))
            {
                throw new InvalidDataException($"Expected skip '{expectedSkip.Id}' has no valid owner and reason.");
            }
        }

        if (ids.Count == 0)
        {
            throw new InvalidDataException("At least one explicit expected skip is required.");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
    }

    private sealed record ExpectedSkip(string Id, string Owner, string Reason);
}
