using System.Text.Json.Serialization;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GltfAnimationMetadataLookupCache.CacheDocument))]
[JsonSerializable(typeof(GltfAnimationMetadataLookupCache.CachedPack))]
[JsonSerializable(typeof(GltfAnimationMetadataLookupCache.CachedFragment))]
[JsonSerializable(typeof(GltfAnimationMetadataLookupCache.CachedSlotAnimation))]
[JsonSerializable(typeof(GltfAnimationMetadataLookupCache.CachedEntry))]
internal partial class GltfAnimationMetadataLookupCacheJsonContext : JsonSerializerContext
{
}
