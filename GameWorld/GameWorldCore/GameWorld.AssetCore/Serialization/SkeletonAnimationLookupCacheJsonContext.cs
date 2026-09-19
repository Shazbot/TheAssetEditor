using System.Text.Json.Serialization;
using GameWorld.Core.Services;

namespace GameWorld.Core.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SkeletonAnimationLookupCache.CacheDocument))]
[JsonSerializable(typeof(SkeletonAnimationLookupCache.CachedPack))]
[JsonSerializable(typeof(SkeletonAnimationLookupCache.CachedAnimation))]
internal partial class SkeletonAnimationLookupCacheJsonContext : JsonSerializerContext
{
}
