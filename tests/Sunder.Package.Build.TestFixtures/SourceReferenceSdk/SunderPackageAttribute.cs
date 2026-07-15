namespace Sunder.Sdk.Packaging;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class SunderPackageAttribute : Attribute
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string? Icon { get; set; }
}
