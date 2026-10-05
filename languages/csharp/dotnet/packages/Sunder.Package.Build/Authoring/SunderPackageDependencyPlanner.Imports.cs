using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Sunder.Package.Build.Authoring;

internal sealed partial class SunderPackageDependencyPlanner
{
    private byte[] ProjectImports(string suffix, IReadOnlySet<string> selected)
    {
        var path = Path.Combine(_extensions, Path.GetFileName(_project) + suffix);
        using var input = new MemoryStream(Read(path));
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        var root = document.Root ?? throw Invalid("A NuGet import document has no root.");
        var packageRoot = ((System.Text.Json.Nodes.JsonObject)_assets["project"]!["restore"]!)["packagesPath"]?.GetValue<string>();
        foreach (var import in root.Descendants().Where(element => element.Name.LocalName == "Import").ToArray())
        {
            var project = import.Attribute("Project")?.Value ?? throw Invalid("A NuGet import has no project path.");
            var expanded = project.Replace("$(NuGetPackageRoot)", packageRoot ?? string.Empty, StringComparison.Ordinal)
                .Replace("$(MSBuildThisFileDirectory)", _extensions + Path.DirectorySeparatorChar, StringComparison.Ordinal).Replace('\\', Path.DirectorySeparatorChar);
            if (expanded.Contains("$(", StringComparison.Ordinal)) throw Invalid($"NuGet import '{project}' has unsupported dynamic path ownership.", "dependency-import-unsupported");
            var fullPath = Path.GetFullPath(expanded, _extensions);
            var owners = _packagePaths.Where(pair => fullPath.StartsWith(pair.Value + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Select(pair => pair.Key).ToArray();
            if (owners.Length != 1) throw Invalid($"NuGet import '{project}' has no unique restored-package owner.", "dependency-import-unsupported");
            if (!selected.Contains(owners[0])) import.Remove();
            else _ = Read(fullPath);
        }
        // Generated package-path properties must not expose an excluded library as
        // an alternate route for an authored target to copy its files into this role.
        foreach (var property in root.Elements().Where(element => element.Name.LocalName == "PropertyGroup").Elements()
                     .Where(element => element.Name.LocalName.StartsWith("Pkg", StringComparison.Ordinal)).ToArray())
        {
            var value = property.Value.Replace('\\', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
            var owner = _packagePaths.FirstOrDefault(pair => pair.Value == value).Key;
            if (owner is not null && !selected.Contains(owner)) property.Remove();
        }
        return new UTF8Encoding(false).GetBytes(document.ToString(SaveOptions.DisableFormatting));
    }
}
