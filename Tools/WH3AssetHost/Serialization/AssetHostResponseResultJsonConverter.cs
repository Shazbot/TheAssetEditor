using System.Text.Json;
using System.Text.Json.Serialization;
using Editors.ImportExport.Exporting.Exporters.RmvToGltf;

namespace WH3AssetHost;

internal sealed class AssetHostResponseResultJsonConverter : JsonConverter<object>
{
    public override object? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => throw new NotSupportedException("Asset-host responses are write-only on the host.");

    public override void Write(
        Utf8JsonWriter writer,
        object? value,
        JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case AssetHostHelloResult hello:
                JsonSerializer.Serialize(writer, hello, AssetHostJsonContext.Default.AssetHostHelloResult);
                break;
            case AssetHostInitializationResult initialization:
                JsonSerializer.Serialize(
                    writer,
                    initialization,
                    AssetHostJsonContext.Default.AssetHostInitializationResult);
                break;
            case AssetHostShutdownResult shutdown:
                JsonSerializer.Serialize(writer, shutdown, AssetHostJsonContext.Default.AssetHostShutdownResult);
                break;
            case AssetHostAnimationCatalog catalog:
                JsonSerializer.Serialize(writer, catalog, AssetHostJsonContext.Default.AssetHostAnimationCatalog);
                break;
            case AssetHostBatchExportResult batch:
                JsonSerializer.Serialize(writer, batch, AssetHostJsonContext.Default.AssetHostBatchExportResult);
                break;
            case AssetHostPaintedVariantResult paintedVariant:
                JsonSerializer.Serialize(
                    writer,
                    paintedVariant,
                    AssetHostJsonContext.Default.AssetHostPaintedVariantResult);
                break;
            case ExportResult export:
                JsonSerializer.Serialize(writer, export, AssetHostJsonContext.Default.ExportResult);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                throw new JsonException($"Unsupported asset-host response result type '{value.GetType()}'.");
        }
    }
}
