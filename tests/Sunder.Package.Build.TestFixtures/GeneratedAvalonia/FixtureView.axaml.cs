using Avalonia.Controls;
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(Id = "test.generated.avalonia", Name = "Generated Avalonia Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.GeneratedAvalonia;

public sealed partial class FixtureView : UserControl
{
    public FixtureView() => InitializeComponent();
}

public sealed record FixtureRecord(string Value);
