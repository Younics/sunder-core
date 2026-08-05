namespace Sunder.App.ViewModels;

public sealed record RegistryPackageProfileLinkViewModel(string Label, Uri NavigateUri)
{
    public string DisplayUrl => NavigateUri.AbsoluteUri;
}
