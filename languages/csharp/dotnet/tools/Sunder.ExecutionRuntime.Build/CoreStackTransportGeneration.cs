using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Sunder.Package.Build.Tasks;

namespace Sunder.ExecutionRuntime.Build;

internal sealed record CoreStackTransportRequest(NullableContextOptions NullableContext,
    IReadOnlyList<string> SourcePaths, IReadOnlyList<string> ReferencePaths, IReadOnlyList<string> DefineConstants);

internal sealed record CoreStackTransportRequestFile(string Path, string Sha256, string Stamp);

// Only the Core Stack project uses this producer. No package authority, descriptor, or output overrides.
internal static class CoreStackTransportGeneration
{
    internal const string SourceName = "CoreStackTransport.g.cs";
    internal const string HostSourceName = "CoreStackHostTransport.g.cs";
    internal const string MarkerName = ".sunder-core-transport";
    private const string MarkerPrefix = "Sunder.Core Stack transport v1\n";

    internal static string Generate(string coreRoot, CoreStackTransportRequestFile requestFile,
        CancellationToken cancellationToken = default, Action<string>? beforePublication = null, Action<string>? beforeMove = null, bool hostClient = false)
    {
        var project = RequestPath(coreRoot, requestFile.Path);
        SigningFileOperations.FileStamp? ownedRequest = null;
        try
        {
            return GenerateOwnedRequest();
        }
        finally
        {
            SigningFileOperations.TryDeleteOwnedFile(requestFile.Path, ownedRequest, requestFile.Sha256);
        }

        string GenerateOwnedRequest()
        {
            var requestInputs = new SunderRpcInputSnapshot();
            var observed = SigningFileOperations.ObserveFile(requestFile.Path);
            var bytes = requestInputs.Read(requestFile.Path, 1_048_576, "evaluated Core transport request");
            if (requestFile.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes))
                || requestFile.Stamp != FormattableString.Invariant($"{observed.Length}:{observed.CreatedUtc.Ticks}:{observed.WrittenUtc.Ticks}")
                || SigningFileOperations.ObserveFile(requestFile.Path) != observed)
                throw new IOException("Core transport request no longer matches the private writer's ownership proof.");
            ownedRequest = observed;
            cancellationToken.ThrowIfCancellationRequested();
            var lines = new UTF8Encoding(false, true).GetString(bytes).Split('\n');
            if (lines.Length is < 2 or > 1025 || lines[0] != "sunder.core.stack-transport.request.v1" || lines[^1] != ""
                || lines[1..^1].Any(line => line.Length > 8192 || !line.Contains('=')))
                throw new InvalidDataException("Core transport request exceeds its bounded line format.");
            var fields = lines[1..^1].Select(line => line.Split('=', 2)).ToLookup(pair => pair[0], pair => Decode(pair[1]), StringComparer.Ordinal);
            if (fields.Any(field => field.Key is not ("configuration" or "target-framework" or "nullable" or "source" or "reference" or "define"
                    or "intermediate-output" or "artifacts-path" or "generated-source" or "sdk-project" or "sdk-reference")))
                throw new InvalidDataException("Core transport request contains an unknown field.");
            var configuration = Single("configuration");
            if (configuration.Length is < 1 or > 64 || configuration.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))
                || Single("target-framework") != "net10.0")
                throw new InvalidDataException("Core transport requires a safe configuration name and net10.0.");
            var nullable = Single("nullable") switch
            {
                "enable" => NullableContextOptions.Enable, "disable" => NullableContextOptions.Disable,
                "warnings" => NullableContextOptions.Warnings, "annotations" => NullableContextOptions.Annotations,
                _ => throw new InvalidDataException("Core transport requires the evaluated Nullable setting."),
            };
            var sources = Paths("source", PlatformContractSourceOperations.MaximumSourceFiles);
            var references = Paths("reference", 512);
            CoreStackTransportInputs.Validate(project, fields, sources, references, requestInputs);
            if (references.Any(path => Path.GetFileName(path) is not ("Sunder.Sdk.dll" or "mscorlib.dll" or "netstandard.dll" or "WindowsBase.dll"
                        or "Microsoft.Win32.Primitives.dll" or "Microsoft.Win32.Registry.dll"
                        or "Microsoft.CSharp.dll" or "Microsoft.VisualBasic.dll" or "Microsoft.VisualBasic.Core.dll"
                        or "Microsoft.Extensions.DependencyInjection.Abstractions.dll" or "Microsoft.Extensions.Logging.Abstractions.dll")
                    && !(Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.Ordinal))))
                throw new InvalidDataException("Core transport accepts only this Stack project's sources and evaluated BCL/SDK dependency references.");
            var defines = fields["define"].ToArray();
            if (defines.Length > 256 || defines.Any(value => !Regex.IsMatch(value, "\\A[A-Za-z_][A-Za-z0-9_]{0,127}\\z")))
                throw new InvalidDataException("Core transport define count or identifier is invalid.");
            var request = new CoreStackTransportRequest(nullable, Array.AsReadOnly(sources), Array.AsReadOnly(references), Array.AsReadOnly(defines));
            var operation = new PlatformContractSourceOperations(coreRoot, cancellationToken, buildContext: request);
            var sourceName = hostClient ? HostSourceName : SourceName;
            var generated = Encoding.UTF8.GetBytes(hostClient ? operation.GenerateHostTransport() : operation.GenerateTransport());
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(generated));
            var marker = Encoding.UTF8.GetBytes((hostClient ? "Sunder.Core Stack host transport v1\n" : MarkerPrefix) + sha256 + "\n");
            var parent = Path.Combine(project, "obj", $"sunder-core-{(hostClient ? "host-" : "")}transport-{configuration}-net10.0");
            var output = Path.Combine(parent, sha256);
            GeneratedOutputPathSafety.EnsureSafeAncestors(parent, includeLeaf: true, "Core transport configuration directory");
            // Completed immutable trees require no writes, including no lease files inside a read-only tree.
            if (GeneratedOutputPathSafety.PathExists(output)) return Reuse();
            Directory.CreateDirectory(parent);
            GeneratedOutputPathSafety.EnsureRegularDirectory(parent, "Core transport configuration directory");
            var parentCreated = Directory.GetCreationTimeUtc(parent);
            string? stage = null;
            using var lease = GeneratedOutputLock.Acquire([output], cancellationToken: cancellationToken);
            VerifyInputs();
            if (GeneratedOutputPathSafety.PathExists(output)) return Reuse();

            var staged = new SunderRpcInputSnapshot();
            var stageRecorded = false;
            DateTime stageCreated = default;
            SigningFileOperations.FileStamp? sourceStamp = null, markerStamp = null;
            try
            {
                stage = Path.Combine(parent, ".stage-" + Guid.NewGuid().ToString("N"));
                SigningFileOperations.EnsureAbsent(stage);
                Directory.CreateDirectory(stage);
                stageCreated = Directory.GetCreationTimeUtc(stage);
                WriteNew(Path.Combine(stage, sourceName), generated);
                WriteNew(Path.Combine(stage, MarkerName), marker);
                ReadTree(stage, staged, generated, marker, sourceName);
                sourceStamp = SigningFileOperations.ObserveFile(Path.Combine(stage, sourceName));
                markerStamp = SigningFileOperations.ObserveFile(Path.Combine(stage, MarkerName));
                stageRecorded = true;
                beforePublication?.Invoke(Path.Combine(stage, sourceName));
                SigningFileOperations.EnsureAbsent(output);
                beforeMove?.Invoke(output);
                VerifyInputs();
                GeneratedOutputPathSafety.EnsureRegularDirectory(parent, "Core transport configuration directory");
                if (Directory.GetCreationTimeUtc(parent) != parentCreated)
                    throw new IOException("Core transport configuration directory changed during generation.");
                VerifyStage();
                cancellationToken.ThrowIfCancellationRequested();
                // Never overwrite, back up, roll back, or delete a destination that appeared during publication.
                CoreStackTransportDirectoryMove.Move(stage, output);
                stage = null;
                return Path.Combine(output, sourceName);
            }
            finally
            {
                if (stage is not null)
                {
                    try
                    {
                        VerifyStage();
                        SigningFileOperations.TryDeleteOwnedFile(Path.Combine(stage, sourceName), sourceStamp, sha256);
                        SigningFileOperations.TryDeleteOwnedFile(Path.Combine(stage, MarkerName), markerStamp, Convert.ToHexStringLower(SHA256.HashData(marker)));
                        GeneratedOutputPathSafety.EnsureRegularDirectory(stage, "Core transport stage cleanup");
                        if (Directory.GetCreationTimeUtc(stage) == stageCreated) Directory.Delete(stage);
                    }
                    catch (IOException) { /* Preserve foreign or interrupted stages rather than deleting unproven bytes. */ }
                    catch (InvalidDataException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            string Single(string name) => fields[name].Count() == 1 ? fields[name].Single()
                : throw new InvalidDataException($"Core transport request requires exactly one '{name}'.");
            string[] Paths(string name, int maximum)
            {
                var values = fields[name].ToArray();
                if (values.Length < 1 || values.Length > maximum || values.Any(path => !Path.IsPathFullyQualified(path)))
                    throw new InvalidDataException($"Core transport requires 1..{maximum} absolute '{name}' paths.");
                return values.Select(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            }
            void VerifyInputs()
            {
                requestInputs.Verify();
                if (SigningFileOperations.ObserveFile(requestFile.Path) != ownedRequest)
                    throw new IOException("Core transport request was replaced after planning.");
                operation.VerifyInputs();
            }
            string Reuse()
            {
                var existing = new SunderRpcInputSnapshot();
                ReadTree(output, existing, generated, marker, sourceName);
                VerifyInputs();
                existing.Verify();
                CheckMembers(output, sourceName);
                return Path.Combine(output, sourceName);
            }
            void VerifyStage()
            {
                if (!stageRecorded || stage is null || Directory.GetCreationTimeUtc(stage) != stageCreated)
                    throw new IOException("Core transport stage changed during generation.");
                CheckMembers(stage, sourceName);
                staged.Verify();
                if (SigningFileOperations.ObserveFile(Path.Combine(stage, sourceName)) != sourceStamp
                    || SigningFileOperations.ObserveFile(Path.Combine(stage, MarkerName)) != markerStamp)
                    throw new IOException("Core transport stage members were replaced.");
            }
        }
    }

    private static string Decode(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            var value = new UTF8Encoding(false, true).GetString(bytes);
            if (Convert.ToBase64String(bytes) != encoded || value.Any(char.IsControl))
                throw new InvalidDataException("Core transport request values must use canonical base64 without control characters.");
            return value;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("Core transport request contains an invalid encoded value.", exception);
        }
    }

    private static string RequestPath(string coreRoot, string requestPath)
    {
        coreRoot = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(coreRoot);
        requestPath = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(requestPath);
        var project = Path.Combine(coreRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Sdk.Stacks");
        var name = Path.GetFileName(requestPath);
        if (Path.GetDirectoryName(requestPath) != Path.Combine(project, "obj") || name.Length != 63
            || !name.StartsWith(".sunder-core-transport-", StringComparison.Ordinal) || !name.EndsWith(".request", StringComparison.Ordinal)
            || !Guid.TryParseExact(name[23..^8], "N", out _))
            throw new InvalidDataException("Core transport requests must be invocation-local files directly under the Stack project obj directory.");
        GeneratedOutputPathSafety.EnsureRegularFile(Path.Combine(coreRoot, "Sunder.Core.slnx"), "Core solution marker");
        GeneratedOutputPathSafety.EnsureRegularDirectory(project, "Core Stack project");
        GeneratedOutputPathSafety.EnsureSafeAncestors(Path.Combine(project, "obj"), includeLeaf: true, "Core transport obj directory");
        GeneratedOutputPathSafety.EnsureRegularDirectory(Path.Combine(project, "obj"), "Core transport obj directory");
        GeneratedOutputPathSafety.EnsureSafeAncestors(requestPath, includeLeaf: true, "Core transport request");
        return project;
    }

    private static void ReadTree(string path, SunderRpcInputSnapshot snapshot, byte[] expectedSource, byte[] expectedMarker, string sourceName)
    {
        CheckMembers(path, sourceName);
        var source = snapshot.Read(Path.Combine(path, sourceName), 4_194_304, "Core generated transport");
        var marker = snapshot.Read(Path.Combine(path, MarkerName), 256, "Core generated transport marker");
        if (!source.AsSpan().SequenceEqual(expectedSource) || !marker.AsSpan().SequenceEqual(expectedMarker))
            throw new InvalidDataException("Core transport output is foreign or modified; its ownership marker does not match.");
    }

    private static void CheckMembers(string path, string sourceName)
    {
        GeneratedOutputPathSafety.EnsureRegularDirectory(path, "Core transport tree");
        var members = Directory.EnumerateFileSystemEntries(path).Take(3).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!members.SequenceEqual(new[] { MarkerName, sourceName }.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Core transport output must contain only its generated source and ownership marker.");
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}
