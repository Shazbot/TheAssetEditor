using System.Text.Json.Serialization;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;

namespace WH3AssetHost;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AssetHostResponse))]
[JsonSerializable(typeof(AssetHostError))]
[JsonSerializable(typeof(AssetHostHelloResult))]
[JsonSerializable(typeof(AssetHostInitializationResult))]
[JsonSerializable(typeof(AssetHostShutdownResult))]
[JsonSerializable(typeof(AssetHostAnimationCatalog))]
[JsonSerializable(typeof(AssetHostAnimationReference))]
[JsonSerializable(typeof(AssetHostBatchExportResult))]
[JsonSerializable(typeof(AssetHostPaintedVariantResult))]
[JsonSerializable(typeof(AssetHostMissingSkeletonDecisionRequest))]
[JsonSerializable(typeof(ExportResult))]
internal partial class AssetHostJsonContext : JsonSerializerContext
{
}
