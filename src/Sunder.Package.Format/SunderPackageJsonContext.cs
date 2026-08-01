using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

[JsonSerializable(typeof(SunderPackageManifest))]
[JsonSerializable(typeof(SunderPackageContentIndex))]
[JsonSerializable(typeof(SunderPackageProjectionDescriptor))]
[JsonSerializable(typeof(SunderStackManifest))]
[JsonSerializable(typeof(SunderStackContentIndex))]
internal sealed partial class SunderPackageJsonContext : JsonSerializerContext;
