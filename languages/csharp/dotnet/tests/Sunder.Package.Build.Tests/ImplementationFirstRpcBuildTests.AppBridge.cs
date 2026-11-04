using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Controls;
using Sunder.App.Bridge;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    private static async Task ActivatePackedAppAsync(string entryPath, string root)
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
            var view = Assert.Single(sink.Views)();
            Assert.NotNull(view.DataContext);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(view.GetType().Assembly));
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(view.DataContext.GetType().Assembly));
            foreach (var name in new[] { "Sunder.Sdk", "Sunder.Sdk.Avalonia", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions" })
                Assert.Same(context, AssemblyLoadContext.GetLoadContext(context.LoadFromAssemblyName(new(name))));
            Assert.Same(typeof(IAppPackageEntryPointV1).Assembly, context.LoadFromAssemblyName(typeof(IAppPackageEntryPointV1).Assembly.GetName()));
        }
        finally { context.Unload(); }
    }

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
