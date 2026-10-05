using Sunder.Package.Format;

namespace Sunder.ExecutionRuntime.Build;

internal static class ExecutionRuntimeLockValidator
{
    public static List<string> Validate(ExecutionRuntimeLockDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        if (!string.Equals(document.Schema, ExecutionRuntimeLockPolicy.SchemaUri, StringComparison.Ordinal))
        {
            errors.Add($"$schema must be '{ExecutionRuntimeLockPolicy.SchemaUri}'.");
        }
        if (document.SchemaVersion != 1)
        {
            errors.Add("schemaVersion must be 1.");
        }
        if (document.LockKind is not ExecutionRuntimeLockPolicy.ProductionKind
            and not ExecutionRuntimeLockPolicy.TestFixtureKind)
        {
            errors.Add("lockKind must be 'production' or 'test-fixture'.");
        }

        ValidateCoverage(document.Coverage, errors);
        ValidateEntries(document, errors);
        return errors;
    }

    private static void ValidateCoverage(ExecutionRuntimeLockCoverage? coverage, ICollection<string> errors)
    {
        if (coverage is null)
        {
            errors.Add("coverage is required.");
            return;
        }

        if (coverage.Runtimes is null
            || coverage.Runtimes.Count != ExecutionRuntimeLockPolicy.RuntimeFamilies.Count)
        {
            errors.Add("coverage.runtimes must explicitly contain .NET major 10 and Node major 24.");
        }
        else
        {
            for (var index = 0; index < ExecutionRuntimeLockPolicy.RuntimeFamilies.Count; index++)
            {
                var expected = ExecutionRuntimeLockPolicy.RuntimeFamilies[index];
                var actual = coverage.Runtimes[index];
                if (actual is null
                    || !string.Equals(actual.Id, expected.Id, StringComparison.Ordinal)
                    || actual.Major != expected.Major)
                {
                    errors.Add(
                        $"coverage.runtimes[{index}] must be id '{expected.Id}' with major {expected.Major}.");
                }
            }
        }

        if (coverage.Rids is null
            || !coverage.Rids.SequenceEqual(ExecutionRuntimeLockPolicy.Rids, StringComparer.Ordinal))
        {
            errors.Add(
                "coverage.rids must contain win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and osx-arm64 in canonical order.");
        }
    }

    private static void ValidateEntries(ExecutionRuntimeLockDocument document, ICollection<string> errors)
    {
        var entries = document.Entries;
        var expectedCount = ExecutionRuntimeLockPolicy.RuntimeFamilies.Count * ExecutionRuntimeLockPolicy.Rids.Count;
        if (entries is null || entries.Count != expectedCount)
        {
            errors.Add($"entries must contain the complete canonical {expectedCount}-entry runtime/RID matrix.");
            if (entries is null)
            {
                return;
            }
        }

        var expectedPairs = ExecutionRuntimeLockPolicy.RuntimeFamilies
            .SelectMany(static family => ExecutionRuntimeLockPolicy.Rids.Select(rid => (family, rid)))
            .ToArray();
        var sourceUris = new HashSet<string>(StringComparer.Ordinal);
        var familyVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        var familyRevisions = new Dictionary<string, int>(StringComparer.Ordinal);
        var familyTrustGenerations = new Dictionary<string, long>(StringComparer.Ordinal);
        var familyReleaseKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var familyAttestationKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null)
            {
                errors.Add($"entries[{index}] must not be null.");
                continue;
            }

            if (index < expectedPairs.Length)
            {
                var expected = expectedPairs[index];
                if (!string.Equals(entry.Id, expected.family.Id, StringComparison.Ordinal)
                    || !string.Equals(entry.Rid, expected.rid, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"entries[{index}] must be the canonical '{expected.family.Id}'/'{expected.rid}' pairing.");
                }
            }

            ValidateEntry(
                document.LockKind,
                entry,
                index,
                sourceUris,
                familyVersions,
                familyRevisions,
                familyTrustGenerations,
                familyReleaseKeys,
                familyAttestationKeys,
                errors);
        }
    }

    private static void ValidateEntry(
        string? lockKind,
        ExecutionRuntimeLockEntry entry,
        int index,
        ISet<string> sourceUris,
        IDictionary<string, string> familyVersions,
        IDictionary<string, int> familyRevisions,
        IDictionary<string, long> familyTrustGenerations,
        IDictionary<string, string> familyReleaseKeys,
        IDictionary<string, string> familyAttestationKeys,
        ICollection<string> errors)
    {
        var prefix = $"entries[{index}]";
        var family = ExecutionRuntimeLockPolicy.FindFamily(entry.Id);
        if (family is null)
        {
            errors.Add($"{prefix}.id is not a curated Sunder execution runtime.");
        }
        if (!ExecutionRuntimeLockPolicy.TryParseStableVersion(entry.Version, out var major)
            || family is not null && major != family.Major)
        {
            errors.Add($"{prefix}.version must be one exact stable major-{family?.Major} SemVer version.");
        }
        else if (family is not null)
        {
            if (familyVersions.TryGetValue(family.Id, out var existingVersion)
                && !string.Equals(existingVersion, entry.Version, StringComparison.Ordinal))
            {
                errors.Add($"All {family.Id} entries must lock the same exact version.");
            }
            else
            {
                familyVersions[family.Id] = entry.Version!;
            }
        }
        if (entry.PackagingRevision is <= 0 or > SunderExecutionRuntimeFormat.MaxPackagingRevision)
        {
            errors.Add($"{prefix}.packagingRevision is outside the execution-runtime format bounds.");
        }
        else if (family is not null
                 && familyRevisions.TryGetValue(family.Id, out var existingRevision)
                 && existingRevision != entry.PackagingRevision)
        {
            errors.Add($"All {family.Id} entries must lock the same exact packagingRevision.");
        }
        else if (family is not null)
        {
            familyRevisions[family.Id] = entry.PackagingRevision;
        }
        if (!ExecutionRuntimeLockPolicy.Rids.Contains(entry.Rid, StringComparer.Ordinal))
        {
            errors.Add($"{prefix}.rid is not supported.");
        }
        if (entry.MinimumHostTrustGeneration is <= 0 or > ExecutionRuntimeLockPolicy.MaxSafeJsonInteger)
        {
            errors.Add($"{prefix}.minimumHostTrustGeneration must be positive and within the safe JSON integer range.");
        }
        else if (family is not null
                 && familyTrustGenerations.TryGetValue(family.Id, out var existingGeneration)
                 && existingGeneration != entry.MinimumHostTrustGeneration)
        {
            errors.Add($"All {family.Id} entries must lock the same minimumHostTrustGeneration.");
        }
        else if (family is not null)
        {
            familyTrustGenerations[family.Id] = entry.MinimumHostTrustGeneration;
        }
        if (!string.Equals(entry.SupportLine, ExecutionRuntimeLockPolicy.LongTermSupportLine, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.supportLine must be '{ExecutionRuntimeLockPolicy.LongTermSupportLine}'.");
        }
        if (!string.Equals(entry.DefaultStatus, ExecutionRuntimeLockPolicy.SupportedDefaultStatus, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.defaultStatus must be '{ExecutionRuntimeLockPolicy.SupportedDefaultStatus}'.");
        }

        ValidateSource(lockKind, entry, family, prefix, sourceUris, errors);
        ValidateLicense(entry, family, prefix, errors);
        ValidateProfileAndRecipe(entry, family, prefix, errors);
        ValidateTrust(
            lockKind,
            entry,
            family,
            prefix,
            familyReleaseKeys,
            familyAttestationKeys,
            errors);
    }

    private static void ValidateSource(
        string? lockKind,
        ExecutionRuntimeLockEntry entry,
        ExecutionRuntimeLockPolicy.RuntimeFamily? family,
        string prefix,
        ISet<string> sourceUris,
        ICollection<string> errors)
    {
        var source = entry.Source;
        if (source is null)
        {
            errors.Add($"{prefix}.source is required.");
            return;
        }

        if (family is not null
            && (!string.Equals(source.Name, family.UpstreamName, StringComparison.Ordinal)
                || !string.Equals(source.Publisher, family.Publisher, StringComparison.Ordinal)))
        {
            errors.Add($"{prefix}.source name/publisher does not match runtime '{family.Id}'.");
        }
        if (!ExecutionRuntimeLockPolicy.TryGetHttpsUri(source.Uri, out var sourceUri))
        {
            errors.Add($"{prefix}.source.uri must be an immutable absolute HTTPS URI without credentials, query, or fragment.");
        }
        else
        {
            if (!sourceUris.Add(source.Uri!))
            {
                errors.Add($"{prefix}.source.uri duplicates another curated source.");
            }
            var fixtureHost = sourceUri!.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(sourceUri.Host, "invalid", StringComparison.OrdinalIgnoreCase);
            if (lockKind == ExecutionRuntimeLockPolicy.TestFixtureKind && !fixtureHost)
            {
                errors.Add($"{prefix}.source.uri for a test-fixture lock must use the reserved .invalid DNS space.");
            }
            if (lockKind == ExecutionRuntimeLockPolicy.ProductionKind
                && ExecutionRuntimeLockPolicy.IsReservedHost(sourceUri.Host))
            {
                errors.Add($"{prefix}.source.uri for a production lock must not use a reserved or literal host.");
            }
        }
        if (!ExecutionRuntimeLockPolicy.IsLowercaseSha256(source.Sha256))
        {
            errors.Add($"{prefix}.source.sha256 must be one exact lower-case SHA-256 digest.");
        }
        var expectedFormat = lockKind == ExecutionRuntimeLockPolicy.TestFixtureKind
            ? ExecutionRuntimeLockPolicy.FixtureArchiveFormat
            : entry.Rid?.StartsWith("win-", StringComparison.Ordinal) == true
                ? ExecutionRuntimeLockPolicy.ZipArchiveFormat
                : ExecutionRuntimeLockPolicy.TarGzipArchiveFormat;
        if (!string.Equals(source.ArchiveFormat, expectedFormat, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.source.archiveFormat must be '{expectedFormat}' for this lock kind and RID.");
        }
        if (!ExecutionRuntimeLockPolicy.TryNormalizeSourceRoot(source.SourceRoot, out _))
        {
            errors.Add($"{prefix}.source.sourceRoot must be '.' or one safe exact archive-relative directory.");
        }
    }

    private static void ValidateLicense(
        ExecutionRuntimeLockEntry entry,
        ExecutionRuntimeLockPolicy.RuntimeFamily? family,
        string prefix,
        ICollection<string> errors)
    {
        var license = entry.License;
        if (license is null)
        {
            errors.Add($"{prefix}.license is required.");
            return;
        }
        if (license.Spdx is null || !license.Spdx.SequenceEqual(["MIT"], StringComparer.Ordinal))
        {
            errors.Add($"{prefix}.license.spdx must explicitly contain the curated MIT license token.");
        }
        if (license.Files is null || license.Files.Count != 1 || license.Files[0] is null)
        {
            errors.Add($"{prefix}.license.files must contain exactly one hashed upstream license file.");
            return;
        }

        var file = license.Files[0]!;
        if (!ExecutionRuntimeLockPolicy.TryNormalizeRelativePath(file.Path, out _)
            || family is not null && !string.Equals(file.Path, family.LicensePath, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.license.files[0].path does not match the curated upstream license path.");
        }
        if (!ExecutionRuntimeLockPolicy.IsLowercaseSha256(file.Sha256))
        {
            errors.Add($"{prefix}.license.files[0].sha256 must be one exact lower-case SHA-256 digest.");
        }
    }

    private static void ValidateProfileAndRecipe(
        ExecutionRuntimeLockEntry entry,
        ExecutionRuntimeLockPolicy.RuntimeFamily? family,
        string prefix,
        ICollection<string> errors)
    {
        var profile = entry.Profile;
        var recipe = entry.Recipe;
        if (profile is null)
        {
            errors.Add($"{prefix}.profile is required.");
        }
        if (recipe is null)
        {
            errors.Add($"{prefix}.recipe is required.");
        }
        if (profile is null || recipe is null)
        {
            return;
        }

        if (family is not null
            && (!string.Equals(profile.Name, family.ProfileName, StringComparison.Ordinal)
                || !string.Equals(profile.ModuleAbi, family.ModuleAbi, StringComparison.Ordinal)))
        {
            errors.Add($"{prefix}.profile does not match runtime '{family.Id}'.");
        }
        if (!string.Equals(profile.WorkerProtocol, SunderExecutionRuntimeFormat.WorkerProtocol, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.profile.workerProtocol must be '{SunderExecutionRuntimeFormat.WorkerProtocol}'.");
        }
        if (!string.Equals(recipe.Name, ExecutionRuntimeLockPolicy.RecipeName, StringComparison.Ordinal)
            || !string.Equals(recipe.PayloadRoot, ExecutionRuntimeLockPolicy.PayloadRoot, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.recipe must use the closed upstream-tree repack profile.");
        }
        if (!ExecutionRuntimeLockPolicy.TryNormalizeRelativePath(recipe.LauncherSource, out var launcherSource)
            || !ExecutionRuntimeLockPolicy.TryNormalizeRelativePath(profile.LaunchExecutable, out var launchExecutable)
            || !string.Equals(
                launchExecutable,
                $"{ExecutionRuntimeLockPolicy.PayloadRoot}/{launcherSource}",
                StringComparison.Ordinal))
        {
            errors.Add($"{prefix} launcher source and payload destination are not an exact safe mapping.");
        }

        var windows = entry.Rid?.StartsWith("win-", StringComparison.Ordinal) == true;
        var expectedLauncher = family?.Id == SunderExecutionRuntimeFormat.DotNetRuntimeId
            ? "dotnet"
            : windows ? "node" : "bin/node";
        if (windows)
        {
            expectedLauncher += ".exe";
        }
        if (!string.Equals(recipe.LauncherSource, expectedLauncher, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.recipe.launcherSource must be '{expectedLauncher}'.");
        }

        if (recipe.ExcludedSourcePaths is null || recipe.ExcludedSourcePaths.Count > 64)
        {
            errors.Add($"{prefix}.recipe.excludedSourcePaths must be a bounded explicit list.");
        }
        else
        {
            string? previous = null;
            foreach (var excludedPath in recipe.ExcludedSourcePaths)
            {
                if (!ExecutionRuntimeLockPolicy.TryNormalizeRelativePath(excludedPath, out var normalized))
                {
                    errors.Add($"{prefix}.recipe.excludedSourcePaths contains an unsafe path.");
                    continue;
                }
                if (previous is not null && string.Compare(previous, normalized, StringComparison.Ordinal) >= 0)
                {
                    errors.Add($"{prefix}.recipe.excludedSourcePaths must be in unique ordinal order.");
                }
                if (IsPathAtOrBelow(recipe.LauncherSource, normalized)
                    || entry.License?.Files?.Any(file => IsPathAtOrBelow(file?.Path, normalized)) == true)
                {
                    errors.Add($"{prefix}.recipe.excludedSourcePaths must not remove the launcher or a locked license file.");
                }
                previous = normalized;
            }
        }

        var expectedLaunchArguments = family?.Id == SunderExecutionRuntimeFormat.DotNetRuntimeId
            ? new[] { SunderExecutionRuntimeFormat.DotNetExecArgument, SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder }
            : new[] { SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder };
        if (profile.LaunchArguments is null
            || !profile.LaunchArguments.SequenceEqual(expectedLaunchArguments, StringComparer.Ordinal))
        {
            errors.Add($"{prefix}.profile.launchArguments does not match the closed runtime profile.");
        }

        if (recipe.Toolchain is null
            || family is not null && !string.Equals(recipe.Toolchain.Name, family.ToolchainName, StringComparison.Ordinal)
            || !ExecutionRuntimeLockPolicy.TryParseStableVersion(recipe.Toolchain?.Version, out var toolchainMajor)
            || family is not null && toolchainMajor != family.Major)
        {
            errors.Add($"{prefix}.recipe.toolchain must identify one exact stable matching-major toolchain.");
        }
        var expectedSmokeArguments = family?.Id == SunderExecutionRuntimeFormat.DotNetRuntimeId
            ? new[] { "--info" }
            : new[] { "--version" };
        if (recipe.SmokeArguments is null
            || !recipe.SmokeArguments.SequenceEqual(expectedSmokeArguments, StringComparer.Ordinal))
        {
            errors.Add($"{prefix}.recipe.smokeArguments does not match the curated native smoke recipe.");
        }
    }

    private static void ValidateTrust(
        string? lockKind,
        ExecutionRuntimeLockEntry entry,
        ExecutionRuntimeLockPolicy.RuntimeFamily? family,
        string prefix,
        IDictionary<string, string> familyReleaseKeys,
        IDictionary<string, string> familyAttestationKeys,
        ICollection<string> errors)
    {
        var trust = entry.Trust;
        if (trust is null)
        {
            errors.Add($"{prefix}.trust is required.");
            return;
        }

        var fixture = lockKind == ExecutionRuntimeLockPolicy.TestFixtureKind;
        var expectedScope = fixture ? "test-only" : "production";
        var expectedApproval = fixture ? "synthetic-fixture" : "approved";
        if (!string.Equals(trust.Scope, expectedScope, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.trust.scope must be '{expectedScope}'.");
        }
        if (trust.SourceApproval is null
            || !string.Equals(trust.SourceApproval.Status, expectedApproval, StringComparison.Ordinal)
            || !ExecutionRuntimeLockPolicy.IsBoundedIdentifier(trust.SourceApproval.ApprovedBy)
            || !ExecutionRuntimeLockPolicy.TryGetHttpsUri(trust.SourceApproval.Reference, out var approvalUri))
        {
            errors.Add($"{prefix}.trust.sourceApproval must contain an exact status, HTTPS evidence reference, and approver identity.");
        }
        else
        {
            var fixtureReference = approvalUri!.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(approvalUri.Host, "invalid", StringComparison.OrdinalIgnoreCase);
            if (fixture && !fixtureReference
                || !fixture && ExecutionRuntimeLockPolicy.IsReservedHost(approvalUri.Host))
            {
                errors.Add($"{prefix}.trust.sourceApproval.reference does not match the lock trust domain.");
            }
        }

        foreach (var (label, keyId) in new[]
                 {
                     ("releaseSigningKeyId", trust.ReleaseSigningKeyId),
                     ("attestationSigningKeyId", trust.AttestationSigningKeyId),
                 })
        {
            if (!ExecutionRuntimeLockPolicy.IsBoundedIdentifier(keyId))
            {
                errors.Add($"{prefix}.trust.{label} must be one exact lower-case key identity.");
            }
            else if (fixture != keyId!.Contains(".test.", StringComparison.Ordinal))
            {
                errors.Add($"{prefix}.trust.{label} does not match the lock trust domain.");
            }
        }
        if (family is not null
            && ExecutionRuntimeLockPolicy.IsBoundedIdentifier(trust.ReleaseSigningKeyId)
            && familyReleaseKeys.TryGetValue(family.Id, out var existingReleaseKey)
            && !string.Equals(existingReleaseKey, trust.ReleaseSigningKeyId, StringComparison.Ordinal))
        {
            errors.Add($"All {family.Id} entries must lock the same releaseSigningKeyId.");
        }
        else if (family is not null && ExecutionRuntimeLockPolicy.IsBoundedIdentifier(trust.ReleaseSigningKeyId))
        {
            familyReleaseKeys[family.Id] = trust.ReleaseSigningKeyId!;
        }
        if (family is not null
            && ExecutionRuntimeLockPolicy.IsBoundedIdentifier(trust.AttestationSigningKeyId)
            && familyAttestationKeys.TryGetValue(family.Id, out var existingAttestationKey)
            && !string.Equals(existingAttestationKey, trust.AttestationSigningKeyId, StringComparison.Ordinal))
        {
            errors.Add($"All {family.Id} entries must lock the same attestationSigningKeyId.");
        }
        else if (family is not null && ExecutionRuntimeLockPolicy.IsBoundedIdentifier(trust.AttestationSigningKeyId))
        {
            familyAttestationKeys[family.Id] = trust.AttestationSigningKeyId!;
        }
        if (!ExecutionRuntimeLockPolicy.IsBoundedIdentifier(trust.AttestationWorkflow, 128))
        {
            errors.Add($"{prefix}.trust.attestationWorkflow must be one exact lower-case workflow identity.");
        }
        if (!ExecutionRuntimeLockPolicy.IsBoundedIdentifier(trust.NativeRunner, 128)
            || fixture != trust.NativeRunner!.StartsWith("fixture-", StringComparison.Ordinal)
            || fixture && !string.Equals(trust.NativeRunner, $"fixture-{entry.Rid}", StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.trust.nativeRunner must identify an exact runner in the lock trust domain.");
        }

        var signatureRequired = entry.Rid?.StartsWith("win-", StringComparison.Ordinal) == true
                                || entry.Rid?.StartsWith("osx-", StringComparison.Ordinal) == true;
        var notarizationRequired = entry.Rid?.StartsWith("osx-", StringComparison.Ordinal) == true;
        ValidateTrustCheck(trust.PlatformSignature, signatureRequired, $"{prefix}.trust.platformSignature", errors);
        ValidateTrustCheck(trust.Notarization, notarizationRequired, $"{prefix}.trust.notarization", errors);
    }

    private static bool IsPathAtOrBelow(string? path, string root)
        => string.Equals(path, root, StringComparison.Ordinal)
           || path?.StartsWith(root + "/", StringComparison.Ordinal) == true;

    private static void ValidateTrustCheck(
        string? actual,
        bool required,
        string path,
        ICollection<string> errors)
    {
        var expected = required
            ? ExecutionRuntimeLockPolicy.RequiredTrustCheck
            : ExecutionRuntimeLockPolicy.NotApplicableTrustCheck;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            errors.Add($"{path} must be '{expected}' for the RID.");
        }
    }
}
