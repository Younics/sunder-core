namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeGuardianLaunchException(int exitCode)
    : IOException($"The Sunder process guardian exited before package startup (exit code {exitCode}). Check the Sunder Host deployment and its required .NET runtime.");
