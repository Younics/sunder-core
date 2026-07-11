namespace Sunder.Cli;

internal sealed class CliApplication
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly Func<CliOptions, ICliRuntimeClient> _runtimeFactory;
    private readonly Func<CliOptions, IRegistryClient> _registryFactory;
    private readonly IBrowserLauncher _browser;

    internal CliApplication(
        TextWriter stdout,
        TextWriter stderr,
        Func<CliOptions, ICliRuntimeClient> runtimeFactory,
        Func<CliOptions, IRegistryClient> registryFactory,
        IBrowserLauncher browser)
    {
        _stdout = stdout;
        _stderr = stderr;
        _runtimeFactory = runtimeFactory;
        _registryFactory = registryFactory;
        _browser = browser;
    }

    public static CliApplication CreateDefault(TextWriter stdout, TextWriter stderr)
        => new(
            stdout,
            stderr,
            options => new CliRuntimeClient(options.RuntimeUrl),
            options => new RegistryClient(options.RegistryApiUrl),
            new BrowserLauncher());

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        CliOutput output;
        CliInvocation invocation;
        try
        {
            invocation = CliCommandParser.Parse(args);
            output = new CliOutput(_stdout, _stderr, invocation.Options.Json);
        }
        catch (Exception exception) when (exception is CliUsageException or ArgumentException)
        {
            output = new CliOutput(_stdout, _stderr, args.Any(arg => string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase)));
            output.Error(exception.Message);
            output.Complete(CliExitCodes.Usage);
            return CliExitCodes.Usage;
        }

        if (invocation.Command is HelpCommand)
        {
            output.Line(CliUsage.Text);
            output.Data(new { usage = CliUsage.Text });
            output.Complete(CliExitCodes.Success);
            return CliExitCodes.Success;
        }

        using var timeout = new CancellationTokenSource(invocation.Options.RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var runtime = _runtimeFactory(invocation.Options);
            using var registry = _registryFactory(invocation.Options);
            var dispatcher = new CliCommandDispatcher(runtime, registry, output, new CliProgress(output), _browser, invocation.Options);
            var exitCode = await dispatcher.ExecuteAsync(invocation.Command, linked.Token).ConfigureAwait(false);
            output.Complete(exitCode);
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.Warning("Operation cancelled.");
            output.Complete(CliExitCodes.Cancelled);
            return CliExitCodes.Cancelled;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            output.Error("Operation timed out. Use --timeout <duration> to increase the request timeout.");
            output.Complete(CliExitCodes.Timeout);
            return CliExitCodes.Timeout;
        }
        catch (Exception exception)
        {
            var exitCode = CliErrorMapper.FromException(exception);
            output.Error(exception.Message);
            output.Complete(exitCode);
            return exitCode;
        }
    }
}
