using System.Text.Json.Serialization;
using Editors.PackOptimizer.Core;

namespace WH3AssetHost.Optimize;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(OptimizeRequest))]
[JsonSerializable(typeof(OptimizeOptionsRequest))]
[JsonSerializable(typeof(OptimizeProgressLine))]
[JsonSerializable(typeof(OptimizeResultLine))]
[JsonSerializable(typeof(OptimizeErrorLine))]
[JsonSerializable(typeof(OptimizeProbeLine))]
internal partial class OptimizeJsonContext : JsonSerializerContext
{
}
