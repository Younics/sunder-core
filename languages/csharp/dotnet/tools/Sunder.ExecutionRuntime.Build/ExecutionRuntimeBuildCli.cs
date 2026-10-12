using System.ComponentModel;
using System.Security.Cryptography;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.ExecutionRuntime.Build;

internal static class ExecutionRuntimeBuildCli
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default,
        Action<string>? platformBeforePublication = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage());
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "check-platform-catalog-migration" => await CheckPlatformCatalogMigrationAsync(args[1..], output, cancellationToken),
                "platform-contracts" => await PlatformContractsAsync(args[1..], output, cancellationToken, platformBeforePublication),
                "core-stack-transport" => await CoreStackTransportAsync(args[1..], output, cancellationToken),
                _ => throw new CliUsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (CliUsageException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync(Usage());
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Core engineering operation was canceled.");
            return 130;
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or IOException
                                           or TimeoutException
                                           or UnauthorizedAccessException
                                           or Win32Exception
                                           or CryptographicException
                                           or HttpRequestException
                                           or ArgumentException)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static async Task<int> CheckPlatformCatalogMigrationAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, ["--descriptor"]);
        var bytes = await EngineeringFileOperations.ReadStableAsync(
            Required(options, "--descriptor"), SunderRpcProtocol.MaximumDescriptorBytes, cancellationToken);
        SunderRpcContractDescriptor descriptor;
        try
        {
            descriptor = SunderRpcContractDescriptor.Parse(bytes);
        }
        catch (SunderRpcDescriptorException exception)
        {
            throw new InvalidDataException("Invalid Stack migration descriptor.", exception);
        }
        // Reviewed migration evidence for the real SDK Stack contract, not the synthetic catalog ping fixture.
        if (descriptor.ContractId != "sunder.stack.contributor" || descriptor.Version != "2.0.0"
            || descriptor.DescriptorVersion != 1 || descriptor.SchemaProfile != "sunder.rpc.values.v1"
            || descriptor.Sha256 != "38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898"
            || descriptor.AbiSha256 != "6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8"
            || descriptor.Services.Count != 1 || descriptor.Services[0].ServiceId != "stack-contributor"
            || !descriptor.Services[0].Methods.Select(static method => method.MethodId).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "export", "import", "import-applied", "list-export-items", "metadata", "preview-import" }, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Stack migration descriptor does not match the reviewed identity, descriptor/ABI hashes, and six-method inventory.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(
            "Stack migration descriptor verified only: sunder.stack.contributor 2.0.0, six methods. No source-derived catalog or release authorization was produced.");
        return 0;
    }

    private static async Task<int> CoreStackTransportAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is not ("generate" or "generate-host"))
            throw new CliUsageException("core-stack-transport requires generate or generate-host.");
        var options = ParseOptions(args[1..], ["--core-root", "--request", "--request-sha256", "--request-stamp"]);
        var root = Required(options, "--core-root");
        var request = new CoreStackTransportRequestFile(Required(options, "--request"),
            Required(options, "--request-sha256"), Required(options, "--request-stamp"));
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(CoreStackTransportGeneration.Generate(root, request, cancellationToken, hostClient: args[0] == "generate-host"));
        return 0;
    }

    private static async Task<int> PlatformContractsAsync(
        string[] args, TextWriter output, CancellationToken cancellationToken, Action<string>? beforePublication)
    {
        if (args.Length == 0) throw new CliUsageException("platform-contracts requires check, accept, freeze, export, or import-publication.");
        string[] allowed = args[0] switch
        {
            "check" => ["--core-root"],
            "accept" => ["--core-root", "--key", "--current-version", "--next-version", "--acknowledge-breaking-change"],
            "freeze" => ["--core-root", "--key", "--catalog-release"],
            "export" => ["--core-root", "--catalog-release", "--output"],
            "import-publication" => ["--core-root", "--catalog", "--registry"],
            _ => throw new CliUsageException($"Unknown Core platform contract action '{args[0]}'."),
        };
        var options = ParseOptions(args[1..], allowed);
        var coreRoot = Required(options, "--core-root");
        options.TryGetValue("--key", out var key);
        options.TryGetValue("--current-version", out var current);
        options.TryGetValue("--next-version", out var next);
        var breaking = false;
        if (options.TryGetValue("--acknowledge-breaking-change", out var acknowledgement)
            && !bool.TryParse(acknowledgement, out breaking))
            throw new CliUsageException("--acknowledge-breaking-change requires true or false.");
        if (current is not null && !SemanticVersion.TryParse(current, out _)
            || next is not null && !SemanticVersion.TryParse(next, out _)
            || current is not null && next is null
            || key is null && (current is not null || next is not null || breaking)
            || breaking && (current is null || next is null))
            throw new CliUsageException("Acceptance version selection requires an exact key and old/new semantic version pair; only initial contracts allow next-version without current-version.");
        var release = args[0] is "freeze" or "export" ? Required(options, "--catalog-release") : null;
        if (release is not null && (!SemanticVersion.TryParse(release, out var version) || version.Prerelease is not null))
            throw new CliUsageException("--catalog-release requires an exact stable semantic version.");
        var destination = args[0] == "export" ? Required(options, "--output") : null;

        var operation = new PlatformContractSourceOperations(coreRoot, cancellationToken);
        if (args[0] == "check")
        {
            operation.Check();
            await output.WriteLineAsync("Core platform contracts match actual source; no files changed.");
        }
        else if (args[0] == "export")
        {
            await operation.WriteCatalogAsync(release!, destination!, beforePublication);
            await output.WriteLineAsync($"Exported canonical Core platform catalog '{destination}' for administrator publication to the selected Registry.");
        }
        else if (args[0] == "import-publication")
        {
            var changed = await operation.ImportPublicationAsync(Required(options, "--catalog"), Required(options, "--registry"), beforePublication);
            await output.WriteLineAsync(changed ? "Imported verified Registry platform publication evidence." : "Platform publication evidence is unchanged.");
        }
        else
        {
            var changed = args[0] == "accept"
                ? operation.Accept(key, current, next, breaking, beforePublication)
                : operation.Freeze(release!, key, beforePublication);
            await output.WriteLineAsync(changed
                ? $"Core platform {args[0]} updated eng/platform-contracts/sunder-contracts.owned.json; no publication state was produced."
                : "Core platform baseline is unchanged; no files changed.");
        }
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args, string[] allowed)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
        {
            throw new CliUsageException("Every command option requires exactly one value.");
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                throw new CliUsageException($"Unknown option '{name}'.");
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliUsageException($"Option '{name}' requires a non-empty value.");
            }
            if (!options.TryAdd(name, value))
            {
                throw new CliUsageException($"Option '{name}' may be specified only once.");
            }
        }
        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value)
            ? value
            : throw new CliUsageException($"Option '{name}' is required.");

    private static string Usage()
        => """
           Core platform catalog and Stack transport engineering tool.
           Usage:
             Sunder.ExecutionRuntime.Build check-platform-catalog-migration --descriptor <real-sdk-stack-descriptor-path>
             Sunder.ExecutionRuntime.Build platform-contracts check --core-root <root>
             Sunder.ExecutionRuntime.Build platform-contracts accept --core-root <root> [--key <key>] [--current-version <old> --next-version <next>] [--acknowledge-breaking-change <true|false>]
             Sunder.ExecutionRuntime.Build platform-contracts freeze --core-root <root> --catalog-release <stable-version> [--key <key>]
             Sunder.ExecutionRuntime.Build platform-contracts export --core-root <root> --catalog-release <stable-version> --output <new-path>
             Sunder.ExecutionRuntime.Build platform-contracts import-publication --core-root <root> --catalog <retained-catalog> --registry <https-origin>
             Sunder.ExecutionRuntime.Build core-stack-transport generate --core-root <root> --request <evaluated-obj-request> --request-sha256 <writer-hash> --request-stamp <writer-stamp>
             Sunder.ExecutionRuntime.Build core-stack-transport generate-host --core-root <root> --request <evaluated-obj-request> --request-sha256 <writer-hash> --request-stamp <writer-stamp>

           Core platform bootstrap requires --key stack-contributor-rpc-service-adapter --next-version 2.0.0 without --current-version.
           Core platform commands never sign or authorize releases. Payload output never overwrites an existing path.
           """;

    private sealed class CliUsageException(string message) : Exception(message);
}
