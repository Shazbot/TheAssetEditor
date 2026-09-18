using System.Text.Json.Serialization;
using Shared.Core.PackFiles.Models;

namespace Shared.Core.PackFiles.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PackFileSettings))]
internal partial class PackFileSettingsJsonContext : JsonSerializerContext
{
}
