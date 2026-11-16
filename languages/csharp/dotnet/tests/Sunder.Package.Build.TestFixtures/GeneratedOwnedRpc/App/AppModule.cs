using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Services;

namespace Example.Generated.App;

public sealed class AppModule : ISunderAppPackageModule, Sunder.App.Bridge.IAppPackageEntryPointV1
{
    public ValueTask<Sunder.App.Bridge.IAppPackageActivationV1> CreateAsync(
        Sunder.App.Bridge.IAppPackageHostV1 host, Sunder.App.Bridge.IAppContributionSinkV1 contributions)
        => Sunder.Sdk.Avalonia.Hosting.AppPackageRuntime.CreateAsync(this, host, contributions);

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => services.AddTransient<DevelopmentCaptureView>();

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterPackageView<DevelopmentCaptureView>(new PackageViewRegistration("example.generated.developer", "Development capture",
            defaultPlacement: PackageViewPlacement.LeftTop));
}

// A real installed consumer: all development authority and image transfer go
// through the public SDK injected into this package-local activation.
public sealed class DevelopmentCaptureView(IPackageDevelopmentService development) : UserControl
{
    private const string ViewId = "test.development.capture.view";

    public async Task CaptureAsync(string developmentFolder, string outputFolder)
    {
        Background = Brushes.Red;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var session = await development.LoadAsync(developmentFolder, watch: false, token);
        Console.WriteLine("installed-consumer: SDK development owner acquired");
        PackageDevelopmentStatus status;
        do
        {
            status = await session.GetStatusAsync(token);
            if (status.AppReadiness is PackageDevelopmentAppReadiness.Quarantined or PackageDevelopmentAppReadiness.Unavailable)
                throw new InvalidOperationException($"Development App failed: {status.AppReadiness}; {status.ErrorMessage}");
            if (status.AppReadiness != PackageDevelopmentAppReadiness.Activated || status.AppliedRevision is null)
                await Task.Delay(50, token);
        } while (status.AppReadiness != PackageDevelopmentAppReadiness.Activated || status.AppliedRevision is null);

        var views = await session.GetViewsAsync(token);
        if (session.PackageId != "test.development.capture" || views.Count != 1 || views[0].ViewId != ViewId)
            throw new InvalidDataException("Development discovery escaped the SDK session's package.");
        Console.WriteLine("installed-consumer: applied revision; opening native view");
        await session.OpenViewAsync(ViewId, token);
        Console.WriteLine("installed-consumer: native shell view opened");

        PackageDevelopmentViewSnapshot? snapshot = null;
        PackageDevelopmentViewElement? selected = null;
        while (selected is null)
        {
            try
            {
                snapshot = (await session.QueryViewAsync(ViewId, new() { Role = "button", Name = "Capture target" }, token)).Snapshot;
                selected = snapshot.Elements.SingleOrDefault(item => item.Name == "Capture target");
            }
            catch (InvalidOperationException error) when (error.Message == "The owned package browser has no current trusted document.")
            {
                // Opening the shell panel does not synchronously load its native document.
            }
            if (selected is null) await Task.Delay(50, token);
        }
        if (!(await session.GetViewsAsync(token)).Single().CanCapture)
            throw new InvalidOperationException("The live native development view did not advertise capture.");
        Console.WriteLine("installed-consumer: trusted native document inspected");

        await SaveAsync(await session.CaptureViewAsync(ViewId, new(), 128, token), Path.Combine(outputFolder, "whole.png"), token);
        var scope = new PackageDevelopmentViewQuery { SnapshotId = snapshot!.SnapshotId, ElementReference = selected.Reference };
        await SaveAsync(await session.CaptureViewAsync(ViewId, scope, 128, token), Path.Combine(outputFolder, "selected.png"), token);
        try
        {
            await using var forbidden = await session.CaptureViewAsync("example.generated.developer", new(), 128, token);
            throw new InvalidDataException("The development session captured its installed developer tool.");
        }
        catch (InvalidOperationException) { }

        var packageId = session.PackageId;
        var applied = status.AppliedRevision;
        await session.DisposeAsync();
        try
        {
            await using var retired = await session.CaptureViewAsync(ViewId, scope, 128, token);
            throw new InvalidDataException("A retired SDK development session returned an image.");
        }
        catch (ObjectDisposedException) { }
        await File.WriteAllLinesAsync(Path.Combine(outputFolder, "consumer.txt"),
            [packageId, applied.SourceContentIdentity, "sdk-owned; applied-revision; native-capture; package-only; retired-session-rejected"], token);
    }

    private static async Task SaveAsync(SunderContent content, string path, CancellationToken token)
    {
        await using var owned = content;
        await using var input = await content.OpenReadAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await input.CopyToAsync(output, token);
    }
}
