using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Sunder.App.Bridge;

#if SDK_TWO
[assembly: AssemblyVersion("2.0.0.0")]
#else
[assembly: AssemblyVersion("1.0.0.0")]
#endif

namespace IncompatibleSdk;

// Intentionally incompatible SDK binaries, neither of which exists in the shell.
public static class PackageSdk
{
#if SDK_TWO
    public static IAppPackageActivationV1 ActivateV2(int value, IAppPackageHostV1 host, IAppContributionSinkV1 sink)
        => new Activation(value.ToString(), host, sink);
#else
    public static IAppPackageActivationV1 ActivateV1(string value, IAppPackageHostV1 host, IAppContributionSinkV1 sink)
        => new Activation(value, host, sink);
#endif
    private sealed class Activation : IAppPackageActivationV1
    {
        private readonly IAppPackageHostV1 _host;
        internal Activation(string value, IAppPackageHostV1 host, IAppContributionSinkV1 sink)
        {
            _host = host;
            sink.RegisterView(new(host.PackageId + ".view", value, null, 1, true), () => {
                var view = new TextBlock { Text = value, DataContext = new object() };
                AppViewCallbacksV1.Attach(view, new Navigation(view));
                return view;
            });
        }
        public void Start(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(_host.ContentRootPath, "started"), _host.PackageId);
        }
        public ValueTask DisposeAsync()
        {
            File.WriteAllText(Path.Combine(_host.ContentRootPath, "disposed"), _host.PackageId);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Navigation(TextBlock view) : IAppViewCallbacksV1
    {
        public bool SupportsPreparation => true;
        public ValueTask WarmupAsync(CancellationToken token) { view.Text += ":warm"; return ValueTask.CompletedTask; }
        public ValueTask NavigateAsync(string id, IReadOnlyDictionary<string, string?> parameters, CancellationToken token)
        { view.Text += ":" + parameters["value"]; return ValueTask.CompletedTask; }
        public ValueTask<bool> PrepareAsync(string id, IReadOnlyDictionary<string, string?> parameters, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask PresentedAsync(string id, IReadOnlyDictionary<string, string?> parameters, CancellationToken token)
        { view.Text += ":presented"; return ValueTask.CompletedTask; }
    }
}
