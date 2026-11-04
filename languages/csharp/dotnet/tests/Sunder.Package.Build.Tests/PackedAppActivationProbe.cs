using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Sunder.App.Bridge;

internal static class PackedAppActivationProbe
{
    public static async Task<int> Main(string[] arguments)
    {
        try
        {
            if (arguments.Length != 3) throw new ArgumentException("Expected shared assemblies, entry point and content root.");
            AssemblyLoadContext.Default.Resolving += (_, name) =>
                AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(arguments[0], name.Name + ".dll"));
            await ActivateAsync(arguments[1], arguments[2]);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ActivateAsync(string entryPath, string root)
    {
        var context = new PackedAppContext(entryPath);
        try
        {
            using var reflection = context.EnterContextualReflection();
            var entry = (IAppPackageEntryPointV1)Activator.CreateInstance(context.LoadFromAssemblyPath(entryPath)
                .GetType("Sunder.Generated.Package.AppModule", throwOnError: true)!)!;
            var host = DispatchProxy.Create<IAppPackageHostV1, PackedAppHost>();
            ((PackedAppHost)(object)host).Root = root;
            var sink = new PackedAppViews();
            await using var activation = await entry.CreateAsync(host, sink);
            activation.Start(default);
            var view = sink.Views.Single()();
            Check(view.DataContext is not null, "View model was not created.");
            Check(AssemblyLoadContext.GetLoadContext(view.GetType().Assembly) == context, "View escaped its package context.");
            Check(AssemblyLoadContext.GetLoadContext(view.DataContext!.GetType().Assembly) == context, "View model escaped its package context.");
            foreach (var name in new[] { "Sunder.Sdk", "Sunder.Sdk.Avalonia", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions" })
                Check(AssemblyLoadContext.GetLoadContext(context.LoadFromAssemblyName(new(name))) == context, "Package dependency was shared: " + name);
            Check(context.LoadFromAssemblyName(typeof(IAppPackageEntryPointV1).Assembly.GetName()) == typeof(IAppPackageEntryPointV1).Assembly,
                "The App bridge was not shared.");
        }
        finally { context.Unload(); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class PackedAppContext(string entryPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly string _folder = Path.GetDirectoryName(entryPath)!;
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "Sunder.App.Bridge" || name.Name!.StartsWith("Avalonia", StringComparison.Ordinal))
                return Default.LoadFromAssemblyName(name);
            var path = Path.Combine(_folder, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    public class PackedAppHost : DispatchProxy
    {
        public string Root { get; set; } = "";
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_PackageId" => "example.generated",
            "get_PackageVersion" => "7.6.5",
            "get_ContentRootPath" => Root,
            "Get" when (string)args![0]! == "rpc.profile" => JsonSerializer.SerializeToElement<object?>(null),
            _ => throw new NotSupportedException(method.Name),
        };
    }

    private sealed class PackedAppViews : IAppContributionSinkV1
    {
        internal List<Func<Control>> Views { get; } = [];
        public void RegisterView(AppViewRegistrationV1 registration, Func<Control> factory) => Views.Add(factory);
        public void RegisterSettingsView(Func<Control> factory) => throw new NotSupportedException();
    }
}
