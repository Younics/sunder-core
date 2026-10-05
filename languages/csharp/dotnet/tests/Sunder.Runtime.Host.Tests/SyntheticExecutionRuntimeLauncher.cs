using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Threading.Tasks;

internal static class SyntheticExecutionRuntimeLauncher
{
    private static async Task<int> Main(string[] arguments)
    {
        var entryPoint = arguments switch
        {
            ["exec", var path] => path,
            [var path] => path,
            _ => null,
        };
        if (entryPoint is null || !Path.IsPathFullyQualified(entryPoint) || !File.Exists(entryPoint))
        {
            return 64;
        }

        entryPoint = Path.GetFullPath(entryPoint);
        var packageRoot = Path.GetDirectoryName(entryPoint)!;
        Assembly? ResolvePackageAssembly(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(packageRoot, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }

        AssemblyLoadContext.Default.Resolving += ResolvePackageAssembly;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(entryPoint);
            var main = assembly.EntryPoint
                       ?? throw new InvalidDataException("The package entry point is not executable.");
            var invocationArguments = main.GetParameters().Length == 0
                ? null
                : new object?[] { Array.Empty<string>() };
            object? result;
            try
            {
                result = main.Invoke(null, invocationArguments);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }

            if (result is not Task task)
            {
                return result is int exitCode ? exitCode : 0;
            }

            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task) is int taskExitCode
                ? taskExitCode
                : 0;
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= ResolvePackageAssembly;
        }
    }
}
