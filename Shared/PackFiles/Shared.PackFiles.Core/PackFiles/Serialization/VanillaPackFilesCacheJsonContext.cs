using System.Text.Json.Serialization;
using Shared.Core.PackFiles.Utility;

namespace Shared.Core.PackFiles.Serialization;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VanillaPackFilesCacheReader.CacheDocument))]
[JsonSerializable(typeof(VanillaPackFilesCacheReader.CacheEntry))]
[JsonSerializable(typeof(VanillaPackFilesCacheReader.CachedPackedFileDto))]
[JsonSerializable(typeof(VanillaPackFilesCacheReader.CachePackHeader))]
[JsonSerializable(typeof(Dictionary<string, VanillaPackFilesCacheReader.CacheEntry>))]
[JsonSerializable(typeof(List<VanillaPackFilesCacheReader.CachedPackedFileDto>))]
[JsonSerializable(typeof(List<string>))]
internal partial class VanillaPackFilesCacheJsonContext : JsonSerializerContext
{
}
