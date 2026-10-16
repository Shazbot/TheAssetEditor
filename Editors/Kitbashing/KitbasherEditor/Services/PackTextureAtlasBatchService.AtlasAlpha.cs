using System.Xml;
using DirectXTexNet;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Editors.ImportExport.TextureAtlas;
using Shared.Core.Settings;
using Shared.GameFormats.RigidModel.Types;

namespace Editors.KitbasherEditor.Services
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // The WH3 BaseColour and MaterialMap atlases are BC1_UNORM_SRGB, which has no alpha
        // channel beyond a 1-bit cut: a source texel with alpha < 128 would be encoded as
        // transparent black. Per source texture in such a channel:
        //  * BC1 source, or every texel opaque: nothing changes;
        //  * non-opaque alpha that no shader using it reads: the source is added to the
        //    force-opaque set, so its alpha is 255 before encoding;
        //  * non-opaque alpha that a shader reads, or an unknown shader/slot/format: the material
        //    is not atlased (discovery skip) and keeps its original textures.
        internal enum TextureAlphaState
        {
            Opaque,
            HasAlpha,
            Unknown,
        }

        private static bool IsBc1AtlasTarget(TextureType type)
            => DDSFormatHelper.GetDDSFormat(GameTypeEnum.Warhammer3, type) == DXGI_FORMAT.BC1_UNORM_SRGB;

        internal static TextureAlphaState ClassifyTextureAlpha(byte[] dds)
        {
            try
            {
                var layout = ReadCompactionLayout(dds);
                if (layout == null)
                    return TextureAlphaState.Unknown;

                return layout.Format switch
                {
                    // BC1 is already 1-bit alpha at most, so re-encoding to BC1 changes nothing.
                    "BC1" => TextureAlphaState.Opaque,
                    "BC2" or "BC3" => AreAllTexelsOpaque(dds, layout)
                        ? TextureAlphaState.Opaque
                        : TextureAlphaState.HasAlpha,
                    _ => TextureAlphaState.Unknown,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return TextureAlphaState.Unknown;
            }
        }

        // One decode per texture path per run.
        private static TextureAlphaState GetTextureAlphaState(
            BatchState state,
            string texturePath,
            Func<byte[]?> readBytes)
        {
            texturePath = Normalize(texturePath);
            if (state.TextureAlphaStates.TryGetValue(texturePath, out var cached))
                return cached;

            var bytes = readBytes();
            var alpha = bytes == null ? TextureAlphaState.Unknown : ClassifyTextureAlpha(bytes);
            state.TextureAlphaStates[texturePath] = alpha;
            return alpha;
        }

        // Null when the texture can be atlased (its alpha is opaque, or no shader reads it and it
        // is forced opaque); else why the material must keep its own textures.
        internal static string? CheckAtlasAlphaSafety(
            TextureAlphaState alpha,
            string? shaderPath,
            string slot)
        {
            if (alpha == TextureAlphaState.Opaque)
                return null;

            if (alpha == TextureAlphaState.Unknown)
            {
                return $"{slot} has a DDS format whose alpha cannot be verified, and the BC1 " +
                       "atlas would turn any alpha below 128 into transparent black.";
            }

            var shader = GetCompactionShaderName(shaderPath);
            var (table, error) = Wh3ShaderChannels.Value;
            if (table == null)
            {
                return $"{slot} has non-opaque alpha that the BC1 atlas cannot keep, and the shader " +
                       $"channel table is not available to prove that nothing reads it ({error}).";
            }

            if (shader == null || !table.Shaders.TryGetValue(shader, out var slots))
            {
                return $"{slot} has non-opaque alpha that the BC1 atlas cannot keep, and shader " +
                       $"{shader ?? "(none)"} is not in the channel table.";
            }

            if (!slots.TryGetValue(slot, out var channels))
            {
                return $"{slot} has non-opaque alpha that the BC1 atlas cannot keep, and the slot " +
                       $"is not bound by shader {shader}.";
            }

            if (channels.Contains('a', StringComparison.OrdinalIgnoreCase))
            {
                return $"Shader {shader} reads the alpha of {slot}, which the BC1 atlas cannot keep.";
            }

            return null;
        }

        private static string? CheckAtlasAlphaLoss(
            BatchState state,
            XmlDocument materialDoc,
            HashSet<string> resolvedChannels,
            Dictionary<string, TextureAtlasConstantColor> constantChannels)
        {
            var shader = materialDoc.SelectSingleNode("/material/shader")?.InnerText;
            foreach (var channel in AtlasChannels)
            {
                if (!IsBc1AtlasTarget(channel.Type))
                    continue;

                TextureAlphaState alpha;
                if (resolvedChannels.Contains(channel.Slot))
                {
                    var path = GetTexturePath(materialDoc, channel.Slot);
                    alpha = GetTextureAlphaState(
                        state,
                        path,
                        () => FindForReadStatic(state, path)?.DataSource.ReadData());
                }
                else if (constantChannels.TryGetValue(channel.Slot, out var constant))
                {
                    alpha = constant.A == byte.MaxValue
                        ? TextureAlphaState.Opaque
                        : TextureAlphaState.HasAlpha;
                }
                else
                {
                    continue;
                }

                var reason = CheckAtlasAlphaSafety(alpha, shader, channel.Slot);
                if (reason != null)
                    return reason;
            }

            return null;
        }

        // Discovery already refused every material whose shader could read the alpha, so a
        // source that still has alpha here only needs it flattened before the BC1 encode.
        private static HashSet<int>? GetForceOpaqueAlphaSourceIds(
            BatchState state,
            TextureType type,
            IReadOnlyDictionary<int, string> sourcePaths,
            IReadOnlyDictionary<int, byte[]> textureBytes,
            IReadOnlyDictionary<int, TextureAtlasConstantColor> constantSources)
        {
            if (!IsBc1AtlasTarget(type))
                return null;

            HashSet<int>? forced = null;
            foreach (var (id, bytes) in textureBytes)
            {
                var alpha = sourcePaths.TryGetValue(id, out var path)
                    ? GetTextureAlphaState(state, path, () => bytes)
                    : ClassifyTextureAlpha(bytes);
                if (alpha == TextureAlphaState.HasAlpha)
                    (forced ??= []).Add(id);
            }

            foreach (var (id, color) in constantSources)
            {
                if (color.A != byte.MaxValue)
                    (forced ??= []).Add(id);
            }

            return forced;
        }
    }
}
