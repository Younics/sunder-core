using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;

internal static class PackageLifetimeProcessProbe
{
    private static async Task<int> Main(string[] args)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(PackageLifetimeProcessProbe).Assembly.Location)!, ".."));
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(parent, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(parent, "Sunder.App.Tests.dll"));
            var type = assembly.GetType("Sunder.App.Tests.PackageViewHostServiceTests", throwOnError: true)!;
            var method = type.GetMethod(args[0], BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Unknown package lifetime scenario.");
            using var json = JsonDocument.Parse(args[1]);
            var parameters = method.GetParameters();
            if (json.RootElement.GetArrayLength() != parameters.Length) throw new ArgumentException("Different scenario arguments.");
            var arguments = new object?[parameters.Length];
            for (var index = 0; index < arguments.Length; index++)
                arguments[index] = json.RootElement[index].Deserialize(parameters[index].ParameterType);
            await (Task)method.Invoke(Activator.CreateInstance(type), arguments)!;
            Console.WriteLine("package-lifetime-scenario-passed:" + args[0]);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException { InnerException: { } inner } ? inner : error);
            return 1;
        }
    }
}
